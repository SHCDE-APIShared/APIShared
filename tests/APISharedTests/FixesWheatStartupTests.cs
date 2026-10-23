using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using APIShared;
using APIShared.Compatibility;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Iced.Intel;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using Instruction = Iced.Intel.Instruction;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class FixesWheatStartupTests
    {
        private static NativeFlagArray fixtureFlags;

        [TestCleanup]
        public void ClearFixtureAddress() => fixtureFlags = null;

        [TestMethod]
        public void InstalledFixesGeneratorMatchesCompleteAuditedStub()
        {
            string game = Environment.GetEnvironmentVariable("SHCDE_GAME_DIR") ??
                File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim();
            string path = Path.Combine(game, "BepInEx", "plugins", "fixes", "fixes.dll");
            // This is an installed-binary audit, not a claim about arbitrary future Fixes builds.
            if (!File.Exists(path)) { Assert.Inconclusive("Optional Fixes is not installed."); return; }
            var fixes = Assembly.LoadFrom(path);
            var rootProperty = typeof(Paths).GetProperty("GameRootPath");
            var bepinProperty = typeof(Paths).GetProperty("BepInExRootPath");
            object previousRoot = rootProperty.GetValue(null, null), previousBepin = bepinProperty.GetValue(null, null);
            try
            {
                rootProperty.GetSetMethod(true).Invoke(null, new object[] { game });
                bepinProperty.GetSetMethod(true).Invoke(null, new object[] { Path.Combine(game, "BepInEx") });
                FixesWheatStartup.VerifyProvenance(fixes);
            }
            finally
            {
                rootProperty.GetSetMethod(true).Invoke(null, new[] { previousRoot });
                bepinProperty.GetSetMethod(true).Invoke(null, new[] { previousBepin });
            }
            using (var disk = AssemblyDefinition.ReadAssembly(path))
            using (var state = new NativeStateBlock())
            {
                var owner = disk.MainModule.GetType("Fixes.Detours.FixesAIDetours");
                var generators = owner.NestedTypes.SelectMany(t => t.Methods).Where(m => m.HasBody &&
                    m.Parameters.Count == 3 && m.Parameters[0].ParameterType.FullName == typeof(Assembler).FullName &&
                    m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f &&
                        f.DeclaringType.FullName == owner.FullName && f.Name == "wheatSaleCategoryFlagArray")).ToArray();
                Assert.AreEqual(1, generators.Length, "Unique installed wheat generator");
                var method = (MethodInfo)fixes.ManifestModule.ResolveMethod(generators[0].MetadataToken.ToInt32());
                using (var copy = new DynamicMethodDefinition(method))
                {
                    // Replace only the address provider in a private method copy. Never
                    // construct FixesAIDetours, touch its state, or publish a game hook.
                    var field = typeof(FixesWheatStartupTests).GetField(nameof(fixtureFlags), BindingFlags.Static | BindingFlags.NonPublic);
                    fixtureFlags = state.AllocateFlags(9);
                    foreach (var instruction in copy.Definition.Body.Instructions)
                        if (instruction.OpCode == OpCodes.Ldsfld && instruction.Operand is FieldReference reference &&
                            reference.DeclaringType.FullName == owner.FullName && reference.Name == "wheatSaleCategoryFlagArray")
                            instruction.Operand = copy.Definition.Module.ImportReference(field);
                    var target = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(method.DeclaringType);
                    var generate = (InlineHookGenerator)copy.Generate().CreateDelegate(typeof(InlineHookGenerator), target);
                    const ulong site = 0x18003F0C9, stub = 0x180400000;
                    byte[] displaced = FixesWheatTestStub.Original;
                    ulong continuation = site + (uint)displaced.Length;
                    var decoder = Decoder.Create(64, new ByteArrayCodeReader(displaced));
                    decoder.IP = site;
                    var instructions = new List<Instruction>();
                    while (decoder.IP < continuation) instructions.Add(decoder.Decode());
                    var assembler = new Assembler(64);
                    generate(assembler, instructions.ToArray(), continuation);
                    byte[] generated;
                    using (var stream = new MemoryStream())
                    {
                        assembler.Assemble(new StreamCodeWriter(stream), stub);
                        generated = stream.ToArray();
                    }
                    // RedBird appends the absolute continuation jump to the foreign emitter's instructions.
                    byte[] complete = generated.Concat(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 })
                        .Concat(BitConverter.GetBytes(continuation)).ToArray();
                    // Semantic analysis of the actual installed emitter, not a byte template of one Fixes version.
                    var analysis = FixesWheatAnalysis.AnalyzeStub(complete, stub, fixtureFlags.Address, displaced,
                        continuation, 0x68);
                    Assert.AreEqual((byte)0x78, analysis.Required);
                    FixesWheatExecutionFixture.Run(generated, fixtureFlags);
                    int writes = 0;
                    Func<byte[], FixesWheatAnalysis.WheatStub> analyze = bytes => FixesWheatAnalysis.AnalyzeStub(bytes,
                        stub, fixtureFlags.Address, displaced, continuation, 0x68);
                    Assert.AreEqual(!analysis.IsCorrect, FixesWheatAnalysis.Correct(() => (byte[])complete.Clone(),
                        (offset, value) => { complete[offset] = value; writes++; }, analyze));
                    Assert.IsFalse(FixesWheatAnalysis.Correct(() => (byte[])complete.Clone(), (_, __) => writes++, analyze));
                    Assert.AreEqual(analysis.IsCorrect ? 0 : 1, writes);
                }
            }
            fixtureFlags = null;
        }

        [TestMethod]
        public void MissingFixesHasNoCorrectionAndNoSecondAttempt()
        {
            var once = typeof(FixesWheatStartup).GetField("attempted", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = once.GetValue(null);
            bool existed = Chainloader.PluginInfos.TryGetValue("fixes", out var prior);
            var messages = new List<string>();
            using (var logger = new ManualLogSource("FixesWheatAbsentFixture"))
            {
                logger.LogEvent += (_, entry) => messages.Add(entry.Data.ToString());
                try
                {
                    Chainloader.PluginInfos.Remove("fixes");
                    once.SetValue(null, 0);
                    FixesWheatStartup.Initialize(logger);
                    FixesWheatStartup.Initialize(logger);
                    Assert.AreEqual(1, messages.Count);
                    StringAssert.Contains(messages[0], "NOT-NEEDED: Fixes absent");
                }
                finally
                {
                    once.SetValue(null, previous);
                    if (existed) Chainloader.PluginInfos["fixes"] = prior;
                }
            }
        }

        [TestMethod]
        public void CompiledDependencyOrdersFixesBeforeApiSharedAndAllowsAbsence()
        {
            var dependencies = typeof(APISharedPlugin).GetCustomAttributes(typeof(BepInDependency), false)
                .Cast<BepInDependency>().ToArray();
            var fixes = dependencies.Single(d => d.DependencyGUID == "fixes");
            Assert.AreEqual(BepInDependency.DependencyFlags.SoftDependency, fixes.Flags);
            foreach (bool installed in new[] { false, true })
            {
                var graph = new SortedDictionary<string, IEnumerable<string>>(StringComparer.InvariantCultureIgnoreCase)
                {
                    ["000shcdese"] = Array.Empty<string>(),
                    [APISharedPlugin.PluginGuid] = dependencies.Select(d => d.DependencyGUID),
                    ["foreign.consumer"] = new[] { APISharedPlugin.PluginGuid }
                };
                if (installed) graph.Add("fixes", new[] { "000shcdese" });
                // This is the installed Chainloader's sorting operation, including missing soft nodes.
                var order = Utility.TopologicalSort(graph.Keys,
                    key => graph.TryGetValue(key, out var next) ? next : Array.Empty<string>())
                    .Where(graph.ContainsKey).ToList();
                Assert.IsTrue(order.IndexOf("000shcdese") < order.IndexOf(APISharedPlugin.PluginGuid));
                Assert.IsTrue(order.IndexOf(APISharedPlugin.PluginGuid) < order.IndexOf("foreign.consumer"));
                if (installed) Assert.IsTrue(order.IndexOf("fixes") < order.IndexOf(APISharedPlugin.PluginGuid));
                else Assert.IsFalse(order.Contains("fixes"));
            }
        }
    }
}
