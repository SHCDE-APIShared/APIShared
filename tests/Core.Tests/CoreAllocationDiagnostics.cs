// Attribution-only test helper. The project excludes this file in ordinary builds.
// Nothing in a measured allocation interval calls this helper; marker methods use
// typed WriteEvent overloads. Tracing changes allocation helpers and is not a fix.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

[EventSource(Name = "APIShared-CoreAllocationDiagnostics")]
internal sealed class CoreAllocationDiagnostics : EventSource
{
    private static readonly string[] startupNames = {
        "DOTNET_EnableEventPipe", "COMPlus_EnableEventPipe", "DOTNET_EventPipeConfig", "COMPlus_EventPipeConfig",
        "DOTNET_EventPipeOutputPath", "COMPlus_EventPipeOutputPath", "DOTNET_EventPipeCircularMB", "COMPlus_EventPipeCircularMB",
        "DOTNET_EventPipeOutputStreaming", "COMPlus_EventPipeOutputStreaming",
        "DOTNET_EventPipeEnableStackwalk", "COMPlus_EventPipeEnableStackwalk", "API_SHARED_ALLOCATION_COHORT"
    };
    private static readonly Dictionary<string, string?> startupEnvironment = ReadStartupEnvironment();
    internal static readonly CoreAllocationDiagnostics Log = new();
    private static readonly Window[] windows = new Window[5];
    private static object? metadata;
    private static string? destination;
    private static bool prepared, completed;
    private static AllocationControl? controlSink;

    [ModuleInitializer]
    [NonEvent]
    internal static void ContainStartupTracing()
    {
        // Runtime startup already consumed these settings. Preserve that snapshot
        // for evidence, then prevent unrelated subprocess fixtures inheriting a
        // second trace session/buffer. Only this diagnostic testhost's process
        // environment changes; the current native session is not stopped.
        foreach (string name in startupNames) Environment.SetEnvironmentVariable(name, null);
    }

    [NonEvent]
    private static Dictionary<string, string?> ReadStartupEnvironment()
    {
        var result = new Dictionary<string, string?>();
        foreach (string name in startupNames) result.Add(name, Environment.GetEnvironmentVariable(name));
        return result;
    }

    private struct Window
    {
        internal bool Began, Ended;
        internal int ManagedThreadId;
        internal long Before, After;
    }

    // A distinct escaping type makes the allocating trace control identifiable.
    internal sealed class AllocationControl
    {
        internal readonly byte[] Bytes = new byte[128];
    }

    [Event(1, Level = EventLevel.Informational)]
    public void Begin(int window, int managedThreadId)
    {
        if (window > 0 && window < windows.Length)
        {
            windows[window].Began = true;
            windows[window].ManagedThreadId = managedThreadId;
        }
        WriteEvent(1, window, managedThreadId);
    }

    [Event(2, Level = EventLevel.Informational)]
    public void End(long window, long before, long after)
    {
        // This event is the first operation after the captured ending counter;
        // JSON, control allocation and diagnostic I/O happen after its timestamp.
        WriteEvent(2, window, before, after);
        if (window > 0 && window < windows.Length)
        {
            ref Window row = ref windows[(int)window];
            row.Ended = true; row.Before = before; row.After = after;
        }
        if ((window == 1 && before != after) || window == 2) Complete();
    }

    [NonEvent]
    internal static void Prepare()
    {
        if (prepared) return;
        prepared = true;
        destination = startupEnvironment["API_SHARED_ALLOCATION_COHORT"];
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("The opt-in allocation diagnostic requires its owned cohort path.");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination))
            throw new InvalidOperationException("Allocation diagnostic evidence must be fresh.");

        // All provider, reflection, hash and native-module work precedes the existing
        // production warm-up. These observations describe this actual child, not CLI setup.
        var assembly = typeof(ValueModifierTests).Assembly;
        var coreLib = typeof(object).Assembly;
        MethodInfo caller = typeof(ValueModifierTests).GetMethod(
            nameof(ValueModifierTests.InvalidInputsAreRejectedAndCompiledCommonPathDoesNotAllocate))!;
        byte[] callerIl = caller.GetMethodBody()!.GetILAsByteArray()!;
        var modules = new List<object>(2);
        using (Process process = Process.GetCurrentProcess())
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(module.ModuleName, "clrjit.dll", StringComparison.OrdinalIgnoreCase))
                    modules.Add(new { name = module.ModuleName, path = module.FileName, sha256 = HashFile(module.FileName) });
            }
        }
        var environment = new Dictionary<string, string?>(startupEnvironment);
        foreach (string name in new[] {
            "DOTNET_TieredCompilation", "COMPlus_TieredCompilation", "DOTNET_TieredPGO", "COMPlus_TieredPGO",
            "DOTNET_ReadyToRun", "COMPlus_ReadyToRun", "DOTNET_gcServer", "COMPlus_gcServer" })
            environment.Add(name, Environment.GetEnvironmentVariable(name));
        metadata = new {
            processId = Environment.ProcessId,
            processPath = Environment.ProcessPath,
            frameworkDescription = RuntimeInformation.FrameworkDescription,
            runtimeVersion = Environment.Version.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            testAssembly = new { path = assembly.Location, sha256 = HashFile(assembly.Location), mvid = assembly.ManifestModule.ModuleVersionId.ToString() },
            coreLib = new { path = coreLib.Location, sha256 = HashFile(coreLib.Location), mvid = coreLib.ManifestModule.ModuleVersionId.ToString() },
            caller = new { name = caller.Name, token = caller.MetadataToken, ilSha256 = Convert.ToHexStringLower(SHA256.HashData(callerIl)) },
            runtimeModules = modules,
            environment,
            providerEnabledAtPrepare = Log.IsEnabled(),
            descendantStartupTracingRemoved = true,
            controlType = typeof(AllocationControl).FullName
        };
        Log.Begin(0, Environment.CurrentManagedThreadId);
        Log.End(0, 0, 0);
        // Initialize the exact counter entry used by the zero-work control. This is
        // diagnostic instrumentation, never additional production Calculate warm-up.
        _ = GC.GetAllocatedBytesForCurrentThread();
    }

    [NonEvent]
    internal static void EmptyMarkerControl()
    {
        Log.Begin(4, Environment.CurrentManagedThreadId);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long after = GC.GetAllocatedBytesForCurrentThread();
        Log.End(4, before, after);
    }

    [NonEvent]
    private static void Complete()
    {
        if (completed) return;
        completed = true;
        try
        {
            // When Calculate fails this occurs after its ending counter, before the
            // unchanged strict assertion. Otherwise it follows the ratio interval.
            Log.Begin(3, Environment.CurrentManagedThreadId);
            long before = GC.GetAllocatedBytesForCurrentThread();
            controlSink = new AllocationControl();
            long after = GC.GetAllocatedBytesForCurrentThread();
            Log.End(3, before, after);
            GC.KeepAlive(controlSink);

            var rows = new List<object>(4);
            for (int i = 1; i < windows.Length; i++)
            {
                Window row = windows[i];
                if (row.Began || row.Ended)
                    rows.Add(new { window = i, before = row.Before, after = row.After,
                        managedThreadId = row.ManagedThreadId, began = row.Began, ended = row.Ended });
            }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
                schema = "core-allocation-diagnostics/1",
                processId = Environment.ProcessId,
                testAssembly = GetMetadataProperty("testAssembly"),
                coreLib = GetMetadataProperty("coreLib"),
                frameworkDescription = RuntimeInformation.FrameworkDescription,
                processPath = Environment.ProcessPath,
                runtimeVersion = Environment.Version.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtimeModules = GetMetadataProperty("runtimeModules"),
                controlType = typeof(AllocationControl).FullName,
                windows = rows,
                prepared = metadata
            }, new JsonSerializerOptions { WriteIndented = true });
            if (bytes.Length > 65536) throw new InvalidDataException("Allocation cohort evidence exceeded 64 KiB.");
            using (var stream = new FileStream(destination!, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                stream.Write(bytes);
            Console.WriteLine("ALLOCATION_DIAGNOSTIC_COHORT " + destination);
        }
        catch (Exception error)
        {
            // Diagnostic failure cannot replace the original strict allocation assertion.
            Console.Error.WriteLine("ALLOCATION_DIAGNOSTIC_INCONCLUSIVE " + error.GetType().Name + ": " + error.Message);
        }
    }

    [NonEvent]
    private static object? GetMetadataProperty(string name) => metadata!.GetType().GetProperty(name)!.GetValue(metadata);

    [NonEvent]
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
