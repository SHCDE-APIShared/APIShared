using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace APIShared.AllocationTrace;

// Offline diagnostics only: no target assembly loading, symbols/network lookup,
// workload substitution, retry, tolerance, or change to the original test exit.
// Standard event10 ticks supply partial bracket correlations, never exact bytes.
internal static class Program
{
    private const long MaxTrace = 128L << 20, MaxDerived = 256L << 20;
    private const int MaxEvents = 1_000_000, MaxReport = 4 << 20;
    private const string MarkerProvider = "APIShared-CoreAllocationDiagnostics";
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const string RundownProvider = "Microsoft-Windows-DotNETRuntimeRundown";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return EvidencePolicy.SelfTest();
        string? output = null, scratch = null;
        bool scratchCreated = false;
        var evidence = new Evidence();
        object? cohort = null, pins = null;
        var joins = new List<object>();
        var watch = Stopwatch.StartNew();
        var conversion = new BoundedLog();
        string? failure = null;
        try
        {
            var options = ParseArgs(args);
            string trace = Input(options["--trace"], MaxTrace, ".nettrace");
            string cohortPath = Input(options["--cohort"], 64 << 10, ".json");
            // Hold no-write/delete read shares through conversion and both pins.
            using var traceGuard = new FileStream(trace, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var cohortGuard = new FileStream(cohortPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (traceGuard.Length <= 0 || traceGuard.Length > MaxTrace || cohortGuard.Length <= 0 || cohortGuard.Length > 64 << 10)
                throw new InvalidDataException("Opened input byte bound.");
            output = Path.GetFullPath(options["--output"]);
            if (File.Exists(output) || Directory.Exists(output)) throw new InvalidDataException("Output must be fresh.");
            if (SamePath(output, trace) || SamePath(output, cohortPath)) throw new InvalidDataException("Output overlaps input.");
            string directory = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Missing output directory.");
            RequirePlainDirectory(directory);
            scratch = output + ".work";
            if (Directory.Exists(scratch) || File.Exists(scratch)) throw new InvalidDataException("Scratch path must be fresh.");
            Directory.CreateDirectory(scratch);
            scratchCreated = true;
            var beforeTrace = Pin(trace, MaxTrace);
            var beforeCohort = Pin(cohortPath, 64 << 10);
            byte[] cohortBytes = new byte[checked((int)cohortGuard.Length)];
            cohortGuard.ReadExactly(cohortBytes);
            using JsonDocument doc = JsonDocument.Parse(cohortBytes, new JsonDocumentOptions { MaxDepth = 12 });
            Cohort data = ReadCohort(doc.RootElement);
            cohort = doc.RootElement.Clone();
            evidence.ProcessId = data.ProcessId;
            evidence.ControlType = data.ControlType;
            evidence.TestPath = data.TestPath;
            evidence.Counters.AddRange(data.Windows);
            conversion.Check = () => CheckBounds(watch, scratch);
            string etlx = Path.Combine(scratch, "capture.etlx");
            var logOptions = new TraceLogOptions
            {
                ContinueOnError = false,
                KeepAllEvents = true,
                MaxEventCount = MaxEvents,
                LocalSymbolsOnly = true,
                AlwaysResolveSymbols = false,
                ShouldResolveSymbols = _ => false,
                ConversionLog = conversion,
                OnLostEvents = (truncated, lost, _) =>
                {
                    evidence.Truncated |= truncated;
                    evidence.EventsLost = Math.Max(evidence.EventsLost, lost);
                }
            };
            // The parent CI job supplies the hard time/memory/derived-output cap.
            // These cooperative checks do not claim to interrupt TraceEvent itself.
            TraceLog.CreateFromEventPipeDataFile(trace, etlx, logOptions);
            CheckBounds(watch, scratch);
            using (var log = new TraceLog(etlx))
            {
                evidence.PointerSize = log.PointerSize;
                evidence.EventsLost = Math.Max(evidence.EventsLost, log.EventsLost);
                evidence.Truncated |= log.Truncated || log.EventCount >= MaxEvents;
                ReadTrace(log, data, evidence, joins, watch, scratch);
            }
            var afterTrace = Pin(trace, MaxTrace);
            var afterCohort = Pin(cohortPath, 64 << 10);
            if (beforeTrace != afterTrace || beforeCohort != afterCohort)
                evidence.Problems.Add("INPUT_CHANGED_DURING_READ");
            pins = new { trace = beforeTrace, cohort = beforeCohort };
        }
        catch (Exception ex)
        {
            failure = Short(ex.GetType().FullName + ": " + ex.Message, 2048);
            evidence.Problems.Add("READER_EXCEPTION");
        }
        finally
        {
            if (scratchCreated && scratch is not null)
            {
                try { DeleteOwnedScratch(scratch); }
                catch (Exception ex)
                {
                    evidence.Problems.Add("OWNED_SCRATCH_CLEANUP_FAILED");
                    failure = Short((failure ?? "") + " Cleanup: " + ex.Message, 2048);
                }
            }
        }
        PolicyResult policy = EvidencePolicy.Evaluate(evidence);
        var report = new
        {
            schema = "allocation-sample-correlation/1",
            pipelineStatus = policy.Status,
            status = policy.AttributionStatus,
            sampledCorrelationOnly = true, exactByteAttribution = false,
            matchingResolvedSampleCount = policy.MatchingResolvedSampleCount,
            limitation = "GCAllocationTick samples correlate only to wider marker brackets, not the exact GC counter span. Their accumulated amounts can span prior heap/context allocations and are never summed or assigned wholly to the named object/type/thread. A resolved sample is only a partial candidate hint. No sample cannot exclude the original98352-byte failure. Helper/assert edges, loss, truncation or unknown identities refuse qualification; the strict original test result is retained.",
            identityLimit = "Trace PID, runtime information and module paths join to the helper cohort; reported remote hashes/MVID are data. The owner's separate artifact manifest must bind DLL/PDB/runtime/deps/binlog bytes. Path equality does not prove cryptographic module identity.",
            pins, cohort, traceJoins = joins,
            reader = new { assembly = typeof(TraceLog).Assembly.FullName, location = typeof(TraceLog).Assembly.Location },
            elapsedSeconds = watch.Elapsed.TotalSeconds,
            bounds = new { maxTraceBytes = MaxTrace, maxDerivedBytes = MaxDerived, maxEvents = MaxEvents, maxRows = 512, maxFrames = 64, maxReportBytes = MaxReport, cooperativeDeadlineSeconds = 110, hardBoundsOwner = "CI parent process" },
            unretained = new { evidence.OutsideWindows, evidence.OtherThreads },
            conversionLog = conversion.ToString(), conversionLogTruncated = conversion.Truncated,
            failure, policy
        };
        try
        {
            byte[] bytes;
            using (var bounded = new ReportStream())
            {
                try { JsonSerializer.Serialize(bounded, report, JsonOptions); bytes = bounded.ToArray(); }
                catch (ReportLimitException)
                {
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = "allocation-sample-correlation/1", status = "INCONCLUSIVE", pipelineStatus = "INCONCLUSIVE", matchingResolvedSampleCount = 0, reason = "REPORT_BYTE_LIMIT" }, JsonOptions);
                    policy = policy with { Passed = false, Status = "INCONCLUSIVE" };
                }
            }
            if (output is null) throw new InvalidDataException("No validated output path.");
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            Console.WriteLine(policy.Status + ": " + output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("INCONCLUSIVE: report write refused: " + Short(ex.Message, 1024));
            return 2;
        }
        // Exit0 admits only the sampled pipeline to the owner. Attribution can
        // still be INCONCLUSIVE with no measured sample. The owner separately
        // retains the strict TRX/counters and original test exit; it never treats
        // this CLI code as a correction or a successful diagnostic CI result.
        return policy.Passed ? 0 : 2;
    }

    private static void ReadTrace(TraceLog log, Cohort data, Evidence evidence,
        List<object> joins, Stopwatch watch, string scratch)
    {
        var types = new Dictionary<(int Clr, ulong Type), string>();
        var runtimeIds = new HashSet<int>();
        var testModuleIds = new HashSet<int>();
        var coreModuleIds = new HashSet<int>();
        var rundown = new List<(int Clr, int Index)>();
        long visited = 0;
        foreach (TraceEvent e in log.Events)
        {
            if ((++visited & 4095) == 0) CheckBounds(watch, scratch);
            if (visited > MaxEvents) { evidence.Truncated = true; break; }
            int id = (int)e.ID, index = (int)e.EventIndex;
            if (e.ProviderName == MarkerProvider && id is 1 or 2)
            {
                int window = checked(Convert.ToInt32(e.PayloadValue(0), CultureInfo.InvariantCulture));
                if (window == 0) continue;
                if (evidence.Markers.Count >= 8) { Problem(evidence, "MARKER_ROW_LIMIT"); continue; }
                if (id == 1)
                    evidence.Markers.Add(new(e.ProcessID, e.ThreadID, index, e.TimeStampRelativeMSec, window, true,
                        Convert.ToInt32(e.PayloadValue(1), CultureInfo.InvariantCulture), 0, 0));
                else
                    evidence.Markers.Add(new(e.ProcessID, e.ThreadID, index, e.TimeStampRelativeMSec, window, false, 0,
                        Convert.ToInt64(e.PayloadValue(1), CultureInfo.InvariantCulture), Convert.ToInt64(e.PayloadValue(2), CultureInfo.InvariantCulture)));
            }
            if (e.ProcessID != data.ProcessId) continue;
            if (e is ModuleLoadUnloadTraceData module)
            {
                string path = Short(module.ModuleILPath, 2048);
                bool test = SamePath(path, data.TestPath), core = SamePath(path, data.CorePath);
                if (test) testModuleIds.Add(module.ClrInstanceID);
                if (core) coreModuleIds.Add(module.ClrInstanceID);
                if ((test || core) && joins.Count < 16)
                    joins.Add(new { kind = test ? "testAssemblyPath" : "coreLibPath", processId = e.ProcessID, path, module.ClrInstanceID });
            }
            if (e is RuntimeInformationTraceData runtime)
            {
                bool exactRuntime = data.RuntimePaths.Any(p => SamePath(p, runtime.RuntimeDllPath));
                evidence.RuntimeObserved |= exactRuntime;
                runtimeIds.Add(runtime.ClrInstanceID);
                if (joins.Count < 16) joins.Add(new { kind = "runtimeInformation", processId = e.ProcessID,
                    runtime.ClrInstanceID, path = Short(runtime.RuntimeDllPath, 2048), exactRuntime,
                    runtime.VMMajorVersion, runtime.VMMinorVersion, runtime.VMBuildNumber, runtime.VMQfeNumber });
            }
            if (e.ProviderName == RundownProvider && id == 146)
            {
                if (rundown.Count >= 8) Problem(evidence, "RUNDOWN_ROW_LIMIT");
                else rundown.Add((Convert.ToInt32(e.PayloadValue(0), CultureInfo.InvariantCulture), index));
            }
            if (e is GCBulkTypeTraceData bulk)
            {
                if (bulk.Count < 0 || bulk.Count > 32768) throw new InvalidDataException("BulkType count bound.");
                for (int i = 0; i < bulk.Count; i++)
                {
                    var value = bulk.Values(i);
                    var key = (bulk.ClrInstanceID, value.TypeID);
                    string rawName = value.TypeName;
                    if (rawName.Length > 1024) Problem(evidence, "TYPE_NAME_TRUNCATED");
                    string name = Short(rawName, 1024);
                    if (types.TryGetValue(key, out string? old) && old != name) Problem(evidence, "AMBIGUOUS_REUSED_TYPE_ID");
                    else if (!types.ContainsKey(key))
                    {
                        if (types.Count >= 32768) { Problem(evidence, "TYPE_MAP_LIMIT"); break; }
                        types.Add(key, name);
                    }
                }
            }
        }
        if (runtimeIds.Count != 1) Problem(evidence, "RUNTIME_INSTANCE_COUNT_AMBIGUOUS");
        else
        {
            int clr = runtimeIds.Single();
            evidence.TestModuleObserved = testModuleIds.Contains(clr);
            evidence.CoreLibObserved = coreModuleIds.Contains(clr);
            var matchingRundown = rundown.Where(r => r.Clr == clr).ToArray();
            evidence.RundownComplete = matchingRundown.Length == 1;
            if (matchingRundown.Length == 1) evidence.RundownIndex = matchingRundown[0].Index;
        }
        var spans = evidence.Markers.GroupBy(m => m.Window)
            .Where(g => g.Count(m => m.Begin) == 1 && g.Count(m => !m.Begin) == 1)
            .Select(g => (Start: g.Single(m => m.Begin), End: g.Single(m => !m.Begin))).ToArray();
        visited = 0;
        foreach (TraceEvent e in log.Events)
        {
            if ((++visited & 4095) == 0) CheckBounds(watch, scratch);
            if (visited > MaxEvents) { evidence.Truncated = true; break; }
            if (e.ProcessID != data.ProcessId || e.ProviderName != RuntimeProvider || (int)e.ID != 10) continue;
            if (e is not GCAllocationTickTraceData allocation || e.Version != 4)
            { Problem(evidence, "ALLOCATION_TICK_V4_DECODER_MISSING"); continue; }
            int index = (int)e.EventIndex;
            var matches = spans.Where(s => index > s.Start.Index && index < s.End.Index).Take(2).ToArray();
            if (matches.Length == 0) { evidence.OutsideWindows++; continue; }
            if (matches.Length != 1) { Problem(evidence, "AMBIGUOUS_SAMPLE_BRACKET"); continue; }
            var span = matches[0];
            if (e.ThreadID != span.Start.OsThreadId) { evidence.OtherThreads++; continue; }
            if (!runtimeIds.Contains(allocation.ClrInstanceID)) Problem(evidence, "SAMPLE_RUNTIME_INSTANCE_NOT_JOINED");
            if (evidence.Samples.Count >= 512) { Problem(evidence, "SAMPLE_ROW_LIMIT"); continue; }
            (Frame[] frames, bool complete) = ReadFrames(e);
            string rawName = allocation.TypeName ?? "";
            if (rawName.Length > 1024) Problem(evidence, "TYPE_NAME_TRUNCATED");
            string name = Short(rawName, 1024);
            if (types.TryGetValue((allocation.ClrInstanceID, allocation.TypeID), out string? mapped))
            {
                if (name.Length == 0) name = mapped;
                else if (name != mapped) Problem(evidence, "SAMPLE_TYPE_NAME_METADATA_MISMATCH");
            }
            evidence.Samples.Add(new(e.ProcessID, e.ThreadID, index, e.TimeStampRelativeMSec, span.Start.Window,
                allocation.ClrInstanceID, Hex(allocation.TypeID), Hex(allocation.Address), (int)allocation.AllocationKind,
                allocation.HeapIndex, allocation.AllocationAmount64, allocation.ObjectSize, name, frames, complete));
        }
    }

    private static (Frame[], bool) ReadFrames(TraceEvent e)
    {
        var frames = new List<Frame>();
        TraceCallStack? stack = e.CallStack();
        bool complete = stack is not null;
        while (stack is not null && frames.Count < 64)
        {
            TraceCodeAddress address = stack.CodeAddress;
            string rawMethod = address.FullMethodName, rawModule = address.ModuleFile?.FilePath ?? "";
            string method = Short(rawMethod, 512), module = Short(rawModule, 1024);
            if (method.Length == 0 || module.Length == 0 || rawMethod.Length > 512 || rawModule.Length > 1024) complete = false;
            frames.Add(new(Hex(address.Address), method, module, address.ILOffset));
            stack = stack.Caller;
        }
        if (stack is not null) complete = false;
        return (frames.ToArray(), complete);
    }

    private sealed record Cohort(int ProcessId, string TestPath, string CorePath, string[] RuntimePaths,
        string ControlType, CounterWindow[] Windows);
    private static Cohort ReadCohort(JsonElement root)
    {
        RejectDuplicateProperties(root);
        if (Text(root, "schema", 128) != "core-allocation-diagnostics/1") throw new InvalidDataException("Cohort schema.");
        int pid = root.GetProperty("processId").GetInt32();
        if (pid <= 0) throw new InvalidDataException("Cohort process.");
        RequireStartupEvidence(root, pid);
        JsonElement test = root.GetProperty("testAssembly"), core = root.GetProperty("coreLib");
        RequireHash(test); RequireHash(core);
        if (!Guid.TryParse(Text(test, "mvid", 64), out _)) throw new InvalidDataException("Cohort MVID.");
        _ = Text(root, "frameworkDescription", 1024);
        if (Text(root, "runtimeVersion", 64) != "10.0.15" || Text(root, "processArchitecture", 32) != "X64")
            throw new InvalidDataException("This diagnostic requires the pinned .NET10.0.15 x64 cohort.");
        _ = Text(root, "processPath", 2048);
        string control = Text(root, "controlType", 256);
        if (control != "CoreAllocationDiagnostics+AllocationControl[]") throw new InvalidDataException("Unexpected control identity.");
        var runtime = new List<string>();
        foreach (JsonElement item in root.GetProperty("runtimeModules").EnumerateArray())
        {
            if (runtime.Count >= 32) throw new InvalidDataException("Runtime module bound.");
            _ = Text(item, "name", 256); RequireHash(item);
            runtime.Add(Text(item, "path", 2048));
        }
        if (runtime.Count == 0) throw new InvalidDataException("Missing runtime modules.");
        var windows = new List<CounterWindow>();
        foreach (JsonElement item in root.GetProperty("windows").EnumerateArray())
        {
            if (windows.Count >= 4) throw new InvalidDataException("Counter window bound.");
            if (!item.GetProperty("began").GetBoolean() || !item.GetProperty("ended").GetBoolean())
                throw new InvalidDataException("Incomplete helper window.");
            windows.Add(new(item.GetProperty("window").GetInt32(), item.GetProperty("before").GetInt64(),
                item.GetProperty("after").GetInt64(), item.GetProperty("managedThreadId").GetInt32()));
        }
        return new(pid, Text(test, "path", 2048), Text(core, "path", 2048), runtime.ToArray(), control, windows.ToArray());
    }
    private static void RequireStartupEvidence(JsonElement root, int pid)
    {
        JsonElement prepared = root.GetProperty("prepared");
        if (prepared.GetProperty("processId").GetInt32() != pid || !prepared.GetProperty("providerEnabledAtPrepare").GetBoolean())
            throw new InvalidDataException("Helper preparation PID/provider join.");
        JsonElement environment = prepared.GetProperty("environment");
        if (Text(environment, "DOTNET_EventPipeEnableStackwalk", 8) != "1" ||
            Text(environment, "COMPlus_EventPipeEnableStackwalk", 8) != "1")
            throw new InvalidDataException("Actual child stackwalk evidence.");
        string config = Text(environment, "DOTNET_EventPipeConfig", 2048);
        bool runtime = false, markers = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string provider in config.Split(','))
        {
            string[] fields = provider.Split(':');
            if (fields.Length != 3 || !names.Add(fields[0])) throw new InvalidDataException("Ambiguous startup provider config.");
            string bits = fields[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? fields[1][2..] : fields[1];
            if (!ulong.TryParse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong keywords) || fields[2] != "5")
                throw new InvalidDataException("Startup provider keyword/level evidence.");
            // This one experimental profile requests event10 and existing resolution
            // metadata. Reject all-object/probabilistic alternatives rather than
            // silently treating their events as the same sampling contract.
            if (fields[0] == RuntimeProvider) runtime = keywords == 0x410b0099UL;
            if (fields[0] == MarkerProvider) markers = true;
        }
        if (!runtime || !markers) throw new InvalidDataException("The fixed sampled-GC/stack startup mask and marker provider are required.");
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty item in element.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new InvalidDataException("Ambiguous duplicate cohort property.");
                RejectDuplicateProperties(item.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static void RequireHash(JsonElement value)
    {
        string hash = Text(value, "sha256", 64);
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Cohort SHA256 shape.");
    }
    private static string Text(JsonElement element, string name, int max)
    {
        string? value = element.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new InvalidDataException("Invalid bounded cohort field: " + name);
        return value;
    }
    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        if (args.Length != 6) throw new ArgumentException("Use --trace file.nettrace --cohort helper.json --output fresh.json, or --self-test.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if ((args[i] != "--trace" && args[i] != "--cohort" && args[i] != "--output") || !result.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown or duplicate argument.");
        return result;
    }
    private sealed record FilePin(string Path, long Length, string Sha256);
    private static FilePin Pin(string path, long max)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = stream.Length;
        if (size <= 0 || size > max) throw new InvalidDataException("Input pin byte bound.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 << 10];
        long count = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            count = checked(count + read);
            if (count > max) throw new InvalidDataException("Input grew beyond pin byte bound.");
            hash.AppendData(buffer, 0, read);
        }
        if (count != size) throw new InvalidDataException("Input changed while pinned.");
        return new(path, size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
    private static string Input(string path, long max, string extension)
    {
        path = Path.GetFullPath(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > max || file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid bounded input: " + path);
        RequirePlainDirectory(file.DirectoryName ?? "");
        return path;
    }
    private static void RequirePlainDirectory(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Missing or redirected directory: " + directory.FullName);
    }
    private static void CheckBounds(Stopwatch watch, string scratch)
    {
        if (watch.Elapsed.TotalSeconds > 110) throw new TimeoutException("Cooperative reader deadline.");
        long size = 0;
        FileSystemInfo[] files = new DirectoryInfo(scratch).GetFileSystemInfos();
        if (files.Length > 16) throw new InvalidDataException("Derived file count bound.");
        foreach (FileSystemInfo file in files)
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file is not FileInfo ordinary)
                throw new InvalidDataException("Unexpected derived entry.");
            size = checked(size + ordinary.Length);
        }
        if (size > MaxDerived) throw new InvalidDataException("Derived bytes bound.");
    }
    private static void DeleteOwnedScratch(string scratch)
    {
        // Only flat files in our just-created directory may be removed. A replaced
        // reparse point/directory or foreign subdirectory refuses recursive cleanup.
        if (!Directory.Exists(scratch)) return;
        RequirePlainDirectory(scratch);
        foreach (FileSystemInfo file in new DirectoryInfo(scratch).GetFileSystemInfos())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file is not FileInfo ordinary)
                throw new InvalidDataException("Unexpected cleanup entry.");
            ordinary.Delete();
        }
        Directory.Delete(scratch, false);
    }
    private static bool SamePath(string left, string right) =>
        string.Equals(left.Replace('/', '\\'), right.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    private static string Short(string? value, int max) => value is null ? "" : value.Length <= max ? value : value[..max];
    private static void Problem(Evidence evidence, string reason)
    { if (evidence.Problems.Count < 64 && !evidence.Problems.Contains(reason)) evidence.Problems.Add(reason); }
    private sealed class ReportLimitException : IOException { }
    private sealed class ReportStream : MemoryStream
    {
        private void Require(int count)
        { if (count < 0 || Length + count > MaxReport) throw new ReportLimitException(); }
        public override void Write(byte[] buffer, int offset, int count) { Require(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Require(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Require(1); base.WriteByte(value); }
    }
    private sealed class BoundedLog : TextWriter
    {
        private readonly StringBuilder value = new();
        internal Action? Check;
        internal bool Truncated;
        private int writes;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char c)
        {
            if ((++writes & 1023) == 0) Check?.Invoke();
            if (value.Length < 64 << 10) value.Append(c); else Truncated = true;
        }
        public override string ToString() => value.ToString();
    }
}