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
        public void InstalledExtenderPassesTheCleanupInstallerGate()
        {
            // Runs every check Initialize performs before publishing its ILHook (audited hash,
            // signature, dry transformation and compilation) against the installed binary.
            WithExtenderResolver(() => {
                Assembly assembly = typeof(MapArchive).Assembly;
                string hash = WorkshopInventory.Hash(assembly.Location);
                Assert.IsTrue(WorkshopCleanupRuntime.IsAuditedExtender(hash), "Unaudited installed Script Extender " + hash + "; see UpdateToNewDLL.md.");
                Assert.AreEqual("TryStageModUpdate", WorkshopCleanupRuntime.ValidateExtender(assembly).Name);
                Console.WriteLine("Workshop cleanup gate passed for SHCDESE.dll " + hash + " (" +
                    System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion + ").");
            });
        }

        [TestMethod]
        public void UnauditedExtenderHashesAreRefused()
        {
            Assert.IsFalse(WorkshopCleanupRuntime.IsAuditedExtender(new string('0', 64)));
            Assert.IsFalse(WorkshopCleanupRuntime.IsAuditedExtender("B170F163E69F7E8F64BDA5874D282F4EB5AE5D9D1852E985ED6BEF194C80D780"), "Hashes are compared as lowercase hex.");
            Assert.IsFalse(WorkshopCleanupRuntime.IsAuditedExtender(null));
            Assert.IsTrue(WorkshopCleanupRuntime.IsAuditedExtender("b170f163e69f7e8f64bda5874d282f4eb5ae5d9d1852e985ed6bef194c80d780"));
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
