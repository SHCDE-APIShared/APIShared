using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API.Components.Archive;
using System;
using System.Linq;
using System.IO;
using System.Reflection;

namespace APISharedTests
{
    [TestClass]
    public class WorkshopHookTests
    {
        [TestMethod]
        public void InstalledStagingMethodCanBeTransformedWithoutExecutingInstaller()
        {
            WithExtenderResolver(() => {
                var type = typeof(MapArchive).Assembly.GetType("SHCDESE.API.Components.Archive.MapModManager", true);
                var method = type.GetMethod("TryStageModUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                using (var definition = new DynamicMethodDefinition(method))
                using (var context = new ILContext(definition.Definition))
                {
                    int returns = context.Body.Instructions.Count(x => x.OpCode == OpCodes.Ret);
                    WorkshopCleanupRuntime.Modify(context);
                    Assert.AreEqual(returns, context.Body.Instructions.Count(x => x.OpCode == OpCodes.Ret));
                    foreach (var ret in context.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret))
                    {
                        Assert.AreEqual(OpCodes.Call, ret.Previous.OpCode);
                        Assert.IsFalse(context.Body.Instructions.Any(x => ReferenceEquals(x.Operand, ret)), "A branch bypasses the cleanup callback.");
                    }
                    Assert.IsNotNull(WorkshopCleanupRuntime.CompileCopy(definition, method));
                }
            });
        }

        [TestMethod]
        public void RejectionDiagnosticIdentifiesTheLoadedBinaryAndFailedStage()
        {
            string diagnostic = WorkshopCleanupRuntime.DescribeRejection(typeof(MapArchive).Assembly,
                "validate-staging-contract", new InvalidOperationException("fixture contract mismatch"));
            StringAssert.Contains(diagnostic, "stage=validate-staging-contract");
            StringAssert.Contains(diagnostic, "sha256=" + WorkshopInventory.Hash(typeof(MapArchive).Assembly.Location));
            StringAssert.Contains(diagnostic, "errorType=System.InvalidOperationException");
            StringAssert.Contains(diagnostic, "fixture contract mismatch");
            StringAssert.Contains(diagnostic, "original Extender staging remains unchanged");
        }

        [TestMethod]
        public void InstalledExtenderPassesTheCleanupInstallerGate()
        {
            // Runs every check Initialize performs before publishing its ILHook:
            // signature, managed body, dry transformation and compilation.
            WithExtenderResolver(() => {
                Assembly assembly = typeof(MapArchive).Assembly;
                string hash = WorkshopInventory.Hash(assembly.Location);
                Assert.AreEqual("TryStageModUpdate", WorkshopCleanupRuntime.ValidateExtender(assembly).Name);
                Console.WriteLine("Workshop cleanup gate passed for SHCDESE.dll " + hash + " (" +
                    System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion + ").");
            });
        }

        [TestMethod]
        public void CompatibleMethodOutsideTheExtenderAssemblyPassesWithoutBinaryApproval()
        {
            WithExtenderResolver(() => WorkshopCleanupRuntime.ValidateStagingMethod(FixtureMethod("Compatible")));
        }

        [TestMethod]
        public void IncompatibleSignaturesAndMissingBodiesAreRefusedBeforePublication()
        {
            WithExtenderResolver(() => {
                Assert.ThrowsExactly<MissingMethodException>(() => WorkshopCleanupRuntime.ValidateStagingMethod(null));
                foreach (string name in new[] { "WrongResult", "WrongArgument", "Static", "Generic", "NoBody" })
                    Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopCleanupRuntime.ValidateStagingMethod(FixtureMethod(name)), name);
            });
        }

        [TestMethod]
        public void MissingReturnsAndInvalidIlAreRefusedBeforePublication()
        {
            WithExtenderResolver(() => {
                var method = FixtureMethod("Compatible");
                using (var definition = new DynamicMethodDefinition(method))
                using (var context = new ILContext(definition.Definition))
                {
                    context.Body.Instructions.Clear();
                    Assert.ThrowsExactly<InvalidOperationException>(() => WorkshopCleanupRuntime.Modify(context));
                    // A bool-returning method without a value on the evaluation stack cannot compile.
                    context.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    WorkshopCleanupRuntime.Modify(context);
                    Assert.Throws<Exception>(() => WorkshopCleanupRuntime.CompileCopy(definition, method));
                }
            });
        }

        private static MethodInfo FixtureMethod(string name) => typeof(StagingFixture).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        private abstract class StagingFixture
        {
            protected bool Compatible(string path, MapArchive archive) => archive != null;
            protected int WrongResult(string path, MapArchive archive) => 1;
            protected bool WrongArgument(int path, MapArchive archive) => true;
            protected static bool Static(string path, MapArchive archive) => true;
            protected bool Generic<T>(string path, MapArchive archive) => true;
            protected abstract bool NoBody(string path, MapArchive archive);
        }

        private static void WithExtenderResolver(Action test)
        {
            string game = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim().Trim('\uFEFF');
            string extender = Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            ResolveEventHandler resolve = (sender, args) => {
                string path = Path.Combine(extender, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try { test(); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        }
    }
}
