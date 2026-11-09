using System;
using System.IO;
using System.Reflection;
using APIShared.Units.AiMilitary.Engineering;
using APIShared.Units.AiMilitary.Orders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.Detours;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class AiMilitaryBrokerTests
    {
        private ResolveEventHandler resolver;
        [TestInitialize]
        public void ResolveInstalledDependencies()
        {
            string game = GameDirectory();
            resolver = (sender, args) => {
                string name = new AssemblyName(args.Name).Name + ".dll";
                foreach (string folder in new[] { "000shcdese", "fixes" })
                { string path = Path.Combine(game, "BepInEx", "plugins", folder, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
        }
        [TestCleanup]
        public void ReleaseResolver() => AppDomain.CurrentDomain.AssemblyResolve -= resolver;
        [TestMethod]
        public void ActualExtenderEffectiveOriginalTransformsAllCompile()
        {
            Check(typeof(BulkTribeDetours), "c_game_tribe_issueorder_movehere_hook_impl", AiMilitaryOrderBroker.TransformMove);
            Check(typeof(BulkTribeDetours), "c_game_tribe_issueorder_withtarget_hook_impl", AiMilitaryOrderBroker.TransformTarget);
            Check(typeof(BulkUnitDetours), "c_game_unit_issueorder_movehere_hook_impl", AiMilitaryOrderBroker.TransformUnitMove);
            Check(typeof(BulkUnitDetours), "c_game_unit_spawn_ex_hook_impl", AiMilitaryOrderBroker.TransformCreate);
            Check(typeof(BulkTribeDetours), "c_game_unit_assign_tribe_hook_impl", AiMilitaryOrderBroker.TransformAssign);
            Check(typeof(BulkTribeDetours), "c_game_create_new_tribe_hook_impl", AiMilitaryOrderBroker.TransformTribeCreate);
        }
        [TestMethod]
        public void InstalledFixesReplacementScopeMatchesActualPrivateOperation()
        {
            string path = Path.Combine(GameDirectory(), "BepInEx", "plugins", "fixes", "fixes.dll");
            if (!File.Exists(path)) { Assert.Inconclusive("Optional Fixes plugin is not installed."); return; }
            Assembly assembly = Assembly.LoadFrom(path);
            MethodInfo method = assembly.GetType("Fixes.Events.FixesAIEvents", true).GetMethod("MoveSiegeUnit", BindingFlags.NonPublic | BindingFlags.Static);
            AiFixesReplacementObserver.Validate(method);
        }
        private static void Check(Type owner, string name, ILContext.Manipulator transform)
        {
            MethodInfo method = owner.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(transform);
                Assert.IsNotNull(definition.Generate(), "Actual installed Original wrapper must accept the production transformation.");
            }
        }
        private static string GameDirectory() => (Environment.GetEnvironmentVariable("SHCDE_GAME_DIR") ??
            File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt"))).Trim().Trim('\uFEFF');
    }
}
