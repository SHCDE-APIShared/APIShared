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
            string game = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim().Trim('\uFEFF');
            string extender = Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            ResolveEventHandler resolve = (sender, args) => {
                string path = Path.Combine(extender, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try {
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
            }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        }
    }
}
