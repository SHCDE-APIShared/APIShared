using APIShared.ModSettings;
using APIShared;
using CrusaderDE;
using Iced.Intel;
using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod]
        [TestCategory("Diagnostics")]
        public void VerifyRoutineLoggingLevels() => TestRoutineLoggingLevels();

        private static void TestRoutineLoggingLevels()
        {
            var previousListeners = BepInEx.Logging.Logger.Listeners.ToArray();
            // Snapshot the mutable process state, not a prescribed list of cache fields.
            var state = typeof(DebugLogHelper).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Concat(typeof(RuntimeHealth).GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                .Where(field => !field.IsInitOnly && !field.IsLiteral).ToDictionary(field => field, field => field.GetValue(null));
            string configPath = Path.Combine(Path.GetTempPath(), "APIShared-Logging-" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                foreach (bool diagnostics in new[] { false, true })
                foreach (bool debugListener in new[] { false, true })
                {
                    foreach (var field in state.Keys)
                        field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
                    var listener = new LoggingLevelListener { DisplayedLogLevel = BepInEx.Logging.LogLevel.Info | BepInEx.Logging.LogLevel.Warning | BepInEx.Logging.LogLevel.Error |
                        (debugListener ? BepInEx.Logging.LogLevel.Debug : BepInEx.Logging.LogLevel.None) };
                    BepInEx.Logging.Logger.Listeners.Clear();
                    BepInEx.Logging.Logger.Listeners.Add(listener);
                    var messages = new List<BepInEx.Logging.LogEventArgs>();
                    using (var source = new BepInEx.Logging.ManualLogSource("LoggingReductionTests"))
                    {
                        source.LogEvent += (_, args) => messages.Add(args);
                        var config = new BepInEx.Configuration.ConfigFile(configPath, false) { SaveOnConfigSet = false };
                        config.Bind("Diagnostics", "Enabled", diagnostics);
                        DebugLogHelper.InitializeDiagnostics(config, source);
                        messages.Clear(); // Explicit opt-in may report a startup warning of its own.
                        RuntimeHealth.Failure(null, "absent-source", true, "An absent caller logger must remain silent.");
                        int formats = 0;
                        DebugLogHelper.LogDebug(source, () => { formats++; return "routine"; });
                        NativeApiLog.Debug(source, "native routine");
                        DebugLogHelper.LogInfo(source, "saved file");
                        NativeApiLog.Info(source, "explicit result");
                        DebugLogHelper.LogWarning(source, "warning");
                        DebugLogHelper.LogError(source, "error");
                        var expected = new List<BepInEx.Logging.LogLevel>();
                        if (diagnostics && debugListener) expected.AddRange(new[] { BepInEx.Logging.LogLevel.Debug, BepInEx.Logging.LogLevel.Debug });
                        // Info is never filtered; its volume is limited at the call sites.
                        expected.AddRange(new[] { BepInEx.Logging.LogLevel.Info, BepInEx.Logging.LogLevel.Info });
                        expected.AddRange(new[] { BepInEx.Logging.LogLevel.Warning, BepInEx.Logging.LogLevel.Error });
                        // Services logging through the forwarding proxy follow the same rules,
                        // and lines they already stamped keep exactly one timestamp.
                        var proxy = DebugLogHelper.ForExternalCode(source);
                        proxy.LogDebug("service routine");
                        proxy.LogInfo("service state");
                        proxy.LogInfo("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "] stamped service state");
                        if (diagnostics && debugListener) expected.Add(BepInEx.Logging.LogLevel.Debug);
                        expected.AddRange(new[] { BepInEx.Logging.LogLevel.Info, BepInEx.Logging.LogLevel.Info });
                        Assert(formats == (diagnostics && debugListener ? 1 : 0), "Lazy Debug formatting ignores diagnostic/listener demand");
                        Assert(messages.Select(message => message.Level).SequenceEqual(expected),
                            "Logging levels differ for diagnostics=" + diagnostics + ", Debug listener=" + debugListener);
                        Assert(messages.All(message => Regex.IsMatch(message.Data.ToString(), @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] ")),
                            "Diagnostic or warning/error output lacks millisecond timestamps");
                        Assert(messages.All(message => Regex.Matches(message.Data.ToString(), @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}").Count == 1),
                            "A forwarded line received a second timestamp");
                    }
                }
            }
            finally
            {
                BepInEx.Logging.Logger.Listeners.Clear();
                foreach (var previous in previousListeners) BepInEx.Logging.Logger.Listeners.Add(previous);
                foreach (var field in state) field.Key.SetValue(null, field.Value);
                if (File.Exists(configPath)) File.Delete(configPath);
            }
        }
        [TestMethod]
        [TestCategory("Diagnostics")]
        public void VerifyCompiledPatternSearch() => TestCompiledPatternSearch();

        private static void TestCompiledPatternSearch()
        {
            const string aivPattern =
                "40 53 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 78 4C 63 F2";
            const string hudPattern =
                "48 8D 1D ? ? ? ? 48 8B F8 48 8B E9 48 8D 05 ? ? ? ? BE 0A 00 00 00 45 33 F6";

            byte[] installedImage = MapPeImage(File.ReadAllBytes(Path.Combine(
                GetGameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll")));
            Assert(CompiledBytePattern.Parse(aivPattern).FindUnique(installedImage) == 0x51790,
                "compiled AIV pattern must retain its canonical RVA");
            Assert(CompiledBytePattern.Parse(hudPattern).FindUnique(installedImage) == 0x186338,
                "compiled HUD pattern must retain its canonical RVA");

            var random = new Random(0x53E2);
            string[] patterns =
            {
                "AA", "AA BB CC", "? BB CC", "AA ? CC", "AA BB ?", "? ?", "10 ? ? 40"
            };
            foreach (string pattern in patterns)
            {
                CompiledBytePattern compiled = CompiledBytePattern.Parse(pattern);
                for (int iteration = 0; iteration < 100; iteration++)
                {
                    var data = new byte[random.Next(0, 160)];
                    random.NextBytes(data);
                    if (iteration % 4 == 0 && data.Length >= compiled.Length)
                        StampPattern(data, random.Next(0, data.Length - compiled.Length + 1), pattern, random);
                    if (iteration % 11 == 0 && data.Length >= compiled.Length * 2)
                    {
                        StampPattern(data, 0, pattern, random);
                        StampPattern(data, data.Length - compiled.Length, pattern, random);
                    }
                    Assert(compiled.FindUnique(data) == FindUniqueReference(data, pattern),
                        "compiled pattern search differs from reference semantics for " + pattern);
                }
            }
        }
        [TestMethod]
        [TestCategory("Diagnostics")]
        public void VerifyAivBuildStepBroker() => TestAivBuildStepBroker();

        private static void TestAivBuildStepBroker()
        {
            var service = new AivBuildStepService(ApiSharedRuntime.SupportedHash, null);
            var calls = new List<string>();
            IAivBuildStepCapability zOwner = service.Bind("z.owner");
            IAivBuildStepCapability aOwner = service.Bind("a.owner");
            var zObserver = new RecordingAivObserver("z", calls);
            var aSecondObserver = new RecordingAivObserver("a2", calls);
            var aFirstObserver = new RecordingAivObserver("a1", calls);

            Assert(zOwner.TryRegisterObserver("one", zObserver, out _), "AIV observer registration should succeed");
            Assert(aOwner.TryRegisterObserver("two", aSecondObserver, out _), "AIV observer registration should succeed");
            Assert(aOwner.TryRegisterObserver("one", aFirstObserver, out _), "AIV observer registration should succeed");
            Assert(aOwner.TryRegisterObserver("one", aFirstObserver, out _), "identical AIV registration should be idempotent");
            Assert(!aOwner.TryRegisterObserver("one", new RecordingAivObserver("conflict", calls), out NativeCapabilityDiagnostic conflict) &&
                conflict.State == NativeCapabilityState.Conflict,
                "a different observer under the same AIV registration identity must conflict");

            int originals = 0;
            var context = new AivBuildStepContext(0x1234, 4, 9, 2, 1);
            int result = service.DispatchForTest(context, () => { calls.Add("vanilla"); originals++; return 77; });
            Assert(result == 77 && originals == 1,
                "AIV broker must return the unchanged result from exactly one Vanilla call");
            AssertSequenceEqual(calls.ToArray(), new[]
            {
                "begin:a1", "begin:a2", "begin:z", "vanilla",
                "complete:z:True:77", "complete:a2:True:77", "complete:a1:True:77"
            }, "AIV observers must begin deterministically and unwind in reverse order");

            calls.Clear();
            var isolated = new AivBuildStepService(ApiSharedRuntime.SupportedHash, null);
            isolated.Bind("a").TryRegisterObserver("begin-failure", new RecordingAivObserver("bad-begin", calls, true, false), out _);
            isolated.Bind("b").TryRegisterObserver("complete-failure", new RecordingAivObserver("bad-complete", calls, false, true), out _);
            isolated.Bind("c").TryRegisterObserver("healthy", new RecordingAivObserver("healthy", calls), out _);
            originals = 0;
            result = isolated.DispatchForTest(context, () => { calls.Add("vanilla"); originals++; return 12; });
            Assert(result == 12 && originals == 1 && calls.Contains("complete:healthy:True:12"),
                "observer exceptions must not suppress Vanilla or healthy AIV observers");

            calls.Clear();
            var reentrant = new AivBuildStepService(ApiSharedRuntime.SupportedHash, null);
            reentrant.Bind("owner").TryRegisterObserver("observer", new RecordingAivObserver("observer", calls), out _);
            originals = 0;
            int outer = reentrant.DispatchForTest(context, () =>
            {
                originals++;
                int inner = reentrant.DispatchForTest(context, () => { originals++; return 5; });
                calls.Add("inner-result:" + inner);
                return 6;
            });
            Assert(outer == 6 && originals == 2 && Count(string.Join("|", calls), "begin:observer") == 2,
                "nested native calls must be dispatched reentrantly with one Original call per invocation");

            calls.Clear();
            var exceptional = new AivBuildStepService(ApiSharedRuntime.SupportedHash, null);
            exceptional.Bind("owner").TryRegisterObserver("observer", new RecordingAivObserver("observer", calls), out _);
            AssertThrows<InvalidOperationException>(
                () => exceptional.DispatchForTest(context, () => throw new InvalidOperationException("injected Vanilla failure")),
                "Vanilla exceptions must propagate through the AIV broker");
            Assert(calls.Contains("complete:observer:False:0"),
                "AIV completion must identify an Original call that did not complete");

            Assert(!AivBuildStepService.TryCreate(
                    "UNKNOWN", 0, ReadOnlySpan<byte>.Empty, null, null,
                    out _, out NativeCapabilityDiagnostic unknown) &&
                unknown.State == NativeCapabilityState.UnsupportedBuild,
                "unknown builds must fail closed before resolving the AIV target");
            Assert(!AivBuildStepService.TryCreate(
                    ApiSharedRuntime.SupportedHash, 0, ReadOnlySpan<byte>.Empty, null, null,
                    out _, out NativeCapabilityDiagnostic resolverFailure) &&
                resolverFailure.State == NativeCapabilityState.ValidationFailed,
                "missing native AIV resolver inputs must fail closed");
        }

    }
}
