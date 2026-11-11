using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using Mono.Cecil;
using SHCDESE.API;
using SHCDESE.API.Components.AI;
using SHCDESE.API.Components.ModManager;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace APISharedTests
{
    [TestClass]
    public class LordMediaResolverTests
    {
        [TestMethod]
        public void EmptyIdentityRetainsOriginalMediaWithoutCallingTheExtenderLookup()
        {
            int calls = 0;
            bool Next(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio)
            { calls++; video = "custom-video"; audio = "custom-audio"; return true; }
            foreach (string name in new[] { null, "" })
            {
                Assert.IsFalse(LordMediaResolverRuntime.Resolve(Next, null, name, default, out string video, out string audio));
                Assert.AreEqual(string.Empty, video);
                Assert.AreEqual(string.Empty, audio);
            }
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void RegisteredIdentitiesRetainTheOriginalResultArgumentsAndExceptions()
        {
            int calls = 0;
            var ai = FakeAi("known", "no-clips", " \t");
            bool Next(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio)
            {
                calls++;
                Assert.AreSame(ai, self);
                Assert.AreEqual(default(AILordMessageType), type);
                video = "video:" + name;
                audio = "audio:" + name;
                return name == "known";
            }
            foreach (string name in new[] { "known", "no-clips", " \t" })
            {
                Assert.AreEqual(name == "known", LordMediaResolverRuntime.Resolve(Next, ai, name, default, out string video, out string audio));
                Assert.AreEqual("video:" + name, video);
                Assert.AreEqual("audio:" + name, audio);
            }
            Assert.AreEqual(3, calls);
            bool Throw(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio) => throw new ApplicationException("original failure");
            Assert.ThrowsExactly<ApplicationException>(() => LordMediaResolverRuntime.Resolve(Throw, ai, "known", default, out _, out _));
        }

        [TestMethod]
        public void NamedVanillaPackagesSkipTheMisleadingExtenderErrorBranch()
        {
            bool Next(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio) =>
                throw new AssertFailedException("No registration means no Extender lookup or its error log.");
            Assert.IsFalse(LordMediaResolverRuntime.Resolve(Next, FakeAi(), "Vanilla-style Lord", default, out string video, out string audio));
            Assert.AreEqual("", video);
            Assert.AreEqual("", audio);
        }

        [TestMethod]
        public void RegistrationGuardUsesTheSameCultureAsTheExtender()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                bool Next(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio)
                { video = "original"; audio = ""; return true; }
                Assert.IsTrue(LordMediaResolverRuntime.Resolve(Next, FakeAi("I"), "I", default, out _, out _));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        private static GameAIManagerAPI FakeAi(params string[] names)
        {
            var ai = (GameAIManagerAPI)FormatterServices.GetUninitializedObject(typeof(GameAIManagerAPI));
            var registered = new Dictionary<string, CustomLordEntry>();
            foreach (string name in names) registered.Add(name.ToLower(), new CustomLordEntry());
            typeof(GameAIManagerAPI).GetProperty("LordDataDict").GetSetMethod(true).Invoke(ai, new object[] { registered });
            return ai;
        }

        [TestMethod]
        public void InstalledExtenderEntryPassesTheProductionInstallationChecks()
        {
            MethodInfo target = typeof(GameAIManagerAPI).GetMethod("TryGetVideoAndAudio", BindingFlags.NonPublic | BindingFlags.Instance);
            LordMediaResolverRuntime.ValidateTarget(target);
        }

        [TestMethod]
        public void ChangedSignatureOrEntryIsRefusedBeforeHookPublication()
        {
            Assert.ThrowsExactly<MissingMethodException>(() => LordMediaResolverRuntime.ValidateTarget(null));
            foreach (string name in new[] { "WrongResult", "Static", "ChangedEntry", "NoBody" })
                Assert.ThrowsExactly<InvalidOperationException>(() => LordMediaResolverRuntime.ValidateTarget(
                    typeof(ResolverFixture).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)), name);
        }

        [TestMethod]
        public void ChangedMissBranchIsRefusedEvenWhenTheOldPrologueStillMatches()
        {
            MethodInfo target = typeof(GameAIManagerAPI).GetMethod("TryGetVideoAndAudio", BindingFlags.NonPublic | BindingFlags.Instance);
            using (var copy = new DynamicMethodDefinition(target))
            {
                var miss = copy.Definition.Body.Instructions.First(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "lord not found ["));
                copy.Definition.Body.GetILProcessor().InsertBefore(miss, Instruction.Create(OpCodes.Ldc_I4_0));
                Assert.ThrowsExactly<InvalidOperationException>(() => LordMediaResolverRuntime.ValidateBody(copy.Definition));
            }
            using (var copy = new DynamicMethodDefinition(target))
            {
                copy.Definition.Body.Instructions.First(i => i.OpCode == OpCodes.Brtrue_S || i.OpCode == OpCodes.Brtrue).OpCode = OpCodes.Brfalse;
                Assert.ThrowsExactly<InvalidOperationException>(() => LordMediaResolverRuntime.ValidateBody(copy.Definition));
            }
        }

        [TestMethod]
        public void NormalMediaFallbacksProduceNoRegistrationWarnings()
        {
            var rows = new[] {
                new LordMediaSlot(1, "", "Wazir", "C:\\Lords\\Wazir", false),
                new LordMediaSlot(2, "plain", "Plain Lord", "C:\\Lords\\plain", false),
                new LordMediaSlot(3, "registered", "Registered Lord", "C:\\Lords\\registered", true)
            };
            var warnings = new List<string>();
            new LordMediaRegistrationReport().Report(1, () => 1, () => rows,
                path => !path.Contains("plain"), warnings.Add, error => Assert.Fail(error.ToString()));
            Assert.AreEqual(0, warnings.Count);
        }

        [TestMethod]
        public void MissingRegistrationNamesEveryAffectedLordAndAllItsPlayersOncePerSession()
        {
            var rows = new[] {
                new LordMediaSlot(6, "nox", "Lord Nox", "C:\\Lords\\nox", false),
                new LordMediaSlot(3, "nox", "Lord Nox", "C:\\Lords\\nox", false),
                new LordMediaSlot(4, "other", "Other Lord", "C:\\Lords\\other", false)
            };
            var warnings = new List<string>();
            var report = new LordMediaRegistrationReport();
            long current = 1;
            void Check() => report.Report(current, () => current, () => rows, path => true,
                warnings.Add, error => Assert.Fail(error.ToString()));
            Check(); Check();
            Assert.AreEqual(2, warnings.Count);
            StringAssert.Contains(warnings[0], "Lord Nox");
            StringAssert.Contains(warnings[0], "nox");
            StringAssert.Contains(warnings[0], "3, 6");
            StringAssert.Contains(warnings[0], "C:\\Lords\\nox");
            StringAssert.Contains(warnings[1], "Other Lord");
            current = 2; // A restored save or a replacement map is a new lifecycle session.
            Check();
            Assert.AreEqual(4, warnings.Count);
            Assert.AreNotEqual(warnings[0], warnings[2], "A new session must not be hidden by RuntimeHealth's cross-call warning throttle.");
            report.End(1); // Delayed End of the previous map must not reset session 2.
            Check();
            Assert.AreEqual(4, warnings.Count);
        }

        [TestMethod]
        public void MissingInfoHasAConcreteCauseAndAnUnknownCauseIsNotInvented()
        {
            var row = new LordMediaSlot(2, "nox", "Lord Nox", "C:\\Lords\\nox", false);
            var warnings = new List<string>();
            var report = new LordMediaRegistrationReport();
            report.Report(1, () => 1, () => new[] { row }, path => Path.GetFileName(path) == "lordmeta.json",
                warnings.Add, error => Assert.Fail(error.ToString()));
            StringAssert.Contains(warnings.Single(), "info.json is missing");
            StringAssert.Contains(warnings.Single(), "original game media paths are retained");
            report.Report(2, () => 2, () => new[] { row }, path => true, warnings.Add, error => Assert.Fail(error.ToString()));
            StringAssert.Contains(warnings[1], "did not register");
            Assert.IsFalse(warnings[1].Contains("info.json is missing"));
        }

        [TestMethod]
        public void StaleOrFailedDiagnosticsCannotAttributeMissingMetadata()
        {
            var report = new LordMediaRegistrationReport();
            var row = new LordMediaSlot(2, "nox", "Lord Nox", "C:\\Lords\\nox", false);
            var warnings = new List<string>();
            var failures = new List<Exception>();
            report.Report(1, () => 2, () => throw new AssertFailedException("Stale capture"),
                path => true, warnings.Add, failures.Add);
            Assert.AreEqual(0, failures.Count);
            report.Report(2, () => 2, () => new[] { row }, path => throw new UnauthorizedAccessException(), warnings.Add, failures.Add);
            Assert.AreEqual(0, warnings.Count);
            Assert.AreEqual(1, failures.Count);
            long current = 2;
            report.Report(2, () => current, () => new[] { row }, path => { current = 3; return true; }, warnings.Add, failures.Add);
            Assert.AreEqual(0, warnings.Count);
        }

        [TestMethod]
        public void CaptureUsesOnlyOccupiedUnambiguouslyNamedUnregisteredSlots()
        {
            CustomisationFileManager.CustomLord Lord(string name) => new CustomisationFileManager.CustomLord
                { lordName = name, lordDisplayName = "Display " + name, customPath = "C:\\Lords\\" + name };
            var catalog = new[] { Lord("nox"), Lord("duplicate"), Lord("duplicate"), Lord("registered") };
            var names = new Dictionary<int, string> { [1] = "nox", [2] = "duplicate", [3] = "registered", [4] = "unknown", [5] = "", [6] = "nox" };
            var slots = LordMediaRegistrationRuntime.Capture(catalog, id => id <= 5, id => names[id], name => name == "registered").ToArray();
            Assert.AreEqual(1, slots.Length);
            Assert.AreEqual(1, slots[0].PlayerId);
            Assert.AreEqual("nox", slots[0].Name);
        }

        [TestMethod]
        public void ActualVanillaCatalogAdmissionDoesNotRequireExtenderRegistration()
        {
            var manager = (CustomisationFileManager)FormatterServices.GetUninitializedObject(typeof(CustomisationFileManager));
            typeof(CustomisationFileManager).GetField("customLords", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(manager, new Dictionary<string, CustomisationFileManager.CustomLord>());
            MethodInfo build = typeof(CustomisationFileManager).GetMethod("BuildCustomLordDirectory", BindingFlags.NonPublic | BindingFlags.Instance);
            using (var copy = new DynamicMethodDefinition(build))
            {
                // Execute the installed Vanilla admission code. Only the filesystem and
                // Unity's JSON decoder boundary are substituted; the admission decision is real.
                int files = 0, parses = 0;
                foreach (var instruction in copy.Definition.Body.Instructions)
                {
                    if (!(instruction.Operand is MethodReference method)) continue;
                    if (method.DeclaringType.FullName == typeof(Directory).FullName && method.Name == "GetFiles")
                    { instruction.Operand = copy.Definition.Module.ImportReference(FixtureMethod(nameof(FixtureFiles))); files++; }
                    if (method.DeclaringType.FullName == typeof(CustomisationFileManager).FullName && method.Name == "ProcessExtendedLordFile")
                    { instruction.Operand = copy.Definition.Module.ImportReference(FixtureMethod(nameof(FixtureParse))); parses++; }
                }
                Assert.AreEqual(1, files);
                Assert.AreEqual(1, parses);
                var run = (Action<CustomisationFileManager, string, bool>)copy.Generate().CreateDelegate(typeof(Action<CustomisationFileManager, string, bool>));
                run(manager, Path.Combine(Path.GetTempPath(), "LordMediaReachability"), false);
            }
            var lord = manager.GetCustomLords().Single();
            Assert.AreEqual("LordMediaReachability", lord.lordName);
            Assert.AreEqual(1, lord.aivs.Count);
            Assert.AreEqual(1, lord.configs.Count);
            var warnings = new List<string>();
            new LordMediaRegistrationReport().Report(1, () => 1,
                () => LordMediaRegistrationRuntime.Capture(manager.GetCustomLords(), id => id == 1, id => lord.lordName, name => false),
                path => Path.GetFileName(path) == "lordmeta.json", warnings.Add, error => Assert.Fail(error.ToString()));
            Assert.AreEqual(1, warnings.Count, "A Vanilla-accepted package with lordmeta but no info reaches the warning condition.");
        }

        [TestMethod]
        public void ActualExtenderRegistrationCanRejectInfoWithoutRegisteringTheVanillaLord()
        {
            var ai = FakeAi();
            MethodInfo process = typeof(GameAIManagerAPI).GetMethod("ProcessCustomLord", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var copy = new DynamicMethodDefinition(process))
            {
                int replaced = 0;
                foreach (var instruction in copy.Definition.Body.Instructions)
                {
                    if (!(instruction.Operand is MethodReference method)) continue;
                    if (method is GenericInstanceMethod generic && generic.Name == "TryDeserializeJson" &&
                        generic.GenericArguments.Single().FullName == typeof(ModInfo).FullName)
                    { instruction.Operand = copy.Definition.Module.ImportReference(FixtureMethod(nameof(RejectInfo))); replaced++; }
                    else if (method.DeclaringType.FullName == "SHCDESE.Logging.LogHelper" && method.Parameters.Count == 3 &&
                        method.Parameters.All(p => p.ParameterType.FullName == "System.String"))
                        instruction.Operand = copy.Definition.Module.ImportReference(FixtureMethod(nameof(IgnoreExternalLog)));
                }
                Assert.AreEqual(1, replaced);
                // A missing/invalid info.json returns false at the installed parser boundary.
                // The actual SE method must return before asset loading or adding LordDataDict.
                var run = (Action<GameAIManagerAPI, string, CustomisationFileManager.CustomLord>)copy.Generate()
                    .CreateDelegate(typeof(Action<GameAIManagerAPI, string, CustomisationFileManager.CustomLord>));
                run(ai, "C:\\Lords\\unregistered", new CustomisationFileManager.CustomLord { lordName = "unregistered" });
            }
            Assert.AreEqual(0, ai.LordDataDict.Count);
        }

        private static MethodInfo FixtureMethod(string name) => typeof(LordMediaResolverTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        private static string[] FixtureFiles(string path, string pattern) => new[] { "lord.lordjson", "castle.aivjson", "lordmeta.json" };
        private static void FixtureParse(CustomisationFileManager self, string file, string real, CustomisationFileManager.CustomLord lord, bool workshop)
        {
            if (file.EndsWith(".lordjson", StringComparison.Ordinal)) lord.configs.Add(new CustomisationFileManager.CustomLordConfig());
            if (file.EndsWith(".aivjson", StringComparison.Ordinal)) lord.aivs.Add(new CustomisationFileManager.CustomAIV());
        }
        private static bool RejectInfo(string path, string name, out ModInfo result) { result = null; return false; }
        private static void IgnoreExternalLog(string message, string member, string file) { }

        private abstract class ResolverFixture
        {
            protected int WrongResult(string name, AILordMessageType type, out string video, out string audio)
            { video = audio = ""; return 0; }
            protected static bool Static(string name, AILordMessageType type, out string video, out string audio)
            { video = audio = ""; return false; }
            protected bool ChangedEntry(string name, AILordMessageType type, out string video, out string audio)
            { video = "changed"; audio = ""; return false; }
            protected abstract bool NoBody(string name, AILordMessageType type, out string video, out string audio);
        }
    }
}
