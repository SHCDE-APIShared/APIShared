using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using APIShared;
using APIShared.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API;

namespace APISharedTests
{
    // Exercise the production generated delegates against actual installed SE signatures. No native
    // target, game manager or process-wide hook is touched by these dormant-wrapper fixtures.
    [TestClass, DoNotParallelize]
    public sealed class ConstructionWrapperTests
    {
        private static readonly string[][] Sites = {
            new[] { "Build", "BulkBuildingDetours", "c_game_player_build_structure_delegate", "c_game_player_build_structure_hook_impl" },
            new[] { "SpawnBuilding", "BulkBuildingDetours", "c_game_building_spawn_delegate", "c_game_building_spawn_hook_impl" },
            new[] { "SpawnUnit", "BulkUnitDetours", "c_game_unit_spawn_ex_delegate", "c_game_unit_spawn_ex_hook_impl" },
            new[] { "SpawnDecoration", "BulkProjectileDetours", "c_game_projectile_spawn_delegate", "c_game_projectile_spawn_hook_impl" },
            new[] { "Refund", "BulkBuildingDetours", "c_game_building_refund_delegate", "c_game_building_refund_hook_impl" },
            new[] { "MarkBuildingForRemoval", BuildingRemovalNativeContracts.ExtenderAdapter.OwnerTypeName, BuildingRemovalNativeContracts.ExtenderAdapter.MarkBuildingForRemovalDelegateName, BuildingRemovalNativeContracts.ExtenderAdapter.MarkBuildingForRemovalCallbackName },
            new[] { "AddResource", "BulkPlayerDetours", "c_game_player_add_resources_delegate", "c_game_player_add_resources_hook_impl" },
            new[] { "ReleaseBuildingSlot", BuildingRemovalNativeContracts.ExtenderAdapter.OwnerTypeName, BuildingRemovalNativeContracts.ExtenderAdapter.ReleaseBuildingSlotDelegateName, BuildingRemovalNativeContracts.ExtenderAdapter.ReleaseBuildingSlotCallbackName },
            new[] { "RetireUnit", "BulkUnitDetours", "c_game_unit_delete_hook_delegate", "c_game_unit_delete_hook_impl" }
        };

        [TestMethod]
        public void ProductionPhasesFollowActualExtenderAobsInsteadOfMisleadingDeleteNames()
        {
            byte[] file = File.ReadAllBytes(Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"));
            using (var hash = SHA256.Create())
                Assert.AreEqual(ApiSharedRuntime.SupportedHash, BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""));
            // Read both compiled assemblies: the selected production Operation determines
            // the SE callback, whose actual constructor AddDetour AOB determines the native
            // function. The two SE names/comments are reversed relative to their AOBs.
            using (var api = AssemblyDefinition.ReadAssembly(typeof(HumanConstructionRuntime).Assembly.Location))
            using (var extender = AssemblyDefinition.ReadAssembly(typeof(GameBuildingManagerAPI).Assembly.Location))
            {
                var initialize = api.MainModule.GetType(typeof(HumanConstructionRuntime).FullName).Methods.Single(method => method.Name == "Initialize");
                var constructor = extender.MainModule.GetType("SHCDESE.Detours.BulkBuildingDetours").Methods.Single(method => method.IsConstructor && !method.IsStatic);
                Assert.AreEqual(BuildingRemovalNativeContracts.MarkBuildingForRemovalRva, ResolveProductionPhase(initialize, constructor, file, "MarkBuildingForRemoval"),
                    "Refund commit requires the actual mark-state3 boundary, before terminal clearing.");
                Assert.AreEqual(BuildingRemovalNativeContracts.ReleaseBuildingSlotRva, ResolveProductionPhase(initialize, constructor, file, "ReleaseBuildingSlot"),
                    "Unclaimed history must retire only at the actual terminal record-clearing boundary.");
            }
        }

        private static int ResolveProductionPhase(MethodDefinition initialize, MethodDefinition constructor, byte[] file, string operation)
        {
            Type kind = typeof(HumanConstructionRuntime).GetNestedType("Operation", BindingFlags.NonPublic);
            int expected = Convert.ToInt32(Enum.Parse(kind, operation));
            var installs = initialize.Body.Instructions.Where(instruction =>
                instruction.Operand is MethodReference method && method.Name == "Install" &&
                method.DeclaringType.FullName == typeof(HumanConstructionRuntime).FullName &&
                ReadInteger(instruction.Previous) == expected).ToArray();
            Assert.AreEqual(1, installs.Length, "Expected one production installation for " + operation);
            var methodName = installs[0].Previous.Previous.Previous;
            Assert.AreEqual(Code.Ldstr, methodName.OpCode.Code);
            string callbackName = (string)methodName.Operand;
            var callbacks = constructor.Body.Instructions.Where(instruction => instruction.OpCode.Code == Code.Ldftn &&
                instruction.Operand is MethodReference callback && callback.Name == callbackName).ToArray();
            Assert.AreEqual(1, callbacks.Length, "Expected one actual SE native callback binding for " + callbackName);
            var pattern = callbacks[0].Previous;
            while (pattern != null && pattern.OpCode.Code != Code.Ldstr) pattern = pattern.Previous;
            Assert.IsNotNull(pattern, "Missing actual SE constructor AOB");
            var add = callbacks[0].Next;
            while (add != null && add.OpCode.Code != Code.Call && add.OpCode.Code != Code.Callvirt) add = add.Next;
            Assert.IsNotNull(add);
            Assert.AreEqual("AddDetour", ((MethodReference)add.Operand).Name, "The extracted AOB must belong to this callback's real AddDetour.");
            return FindUniqueExecutableRva(file, CompiledBytePattern.Parse((string)pattern.Operand));
        }

        private static int ReadInteger(Instruction instruction)
        {
            if (instruction.OpCode.Code >= Code.Ldc_I4_0 && instruction.OpCode.Code <= Code.Ldc_I4_8)
                return instruction.OpCode.Code - Code.Ldc_I4_0;
            if (instruction.OpCode.Code == Code.Ldc_I4 || instruction.OpCode.Code == Code.Ldc_I4_S)
                return Convert.ToInt32(instruction.Operand);
            return -1;
        }

        private static int FindUniqueExecutableRva(byte[] file, CompiledBytePattern pattern)
        {
            int pe = BitConverter.ToInt32(file, 0x3C), found = -1;
            int sections = pe + 24 + BitConverter.ToUInt16(file, pe + 20);
            for (int i = 0; i < BitConverter.ToUInt16(file, pe + 6); i++)
            {
                int section = sections + i * 40;
                if ((BitConverter.ToUInt32(file, section + 36) & 0x20000000) == 0) continue;
                int raw = BitConverter.ToInt32(file, section + 20), length = BitConverter.ToInt32(file, section + 16);
                int offset = pattern.FindUnique(new ReadOnlySpan<byte>(file, raw, length));
                Assert.AreNotEqual(-2, offset, "Actual SE AOB is ambiguous within an executable section.");
                if (offset < 0) continue;
                Assert.AreEqual(-1, found, "Actual SE AOB is ambiguous across executable sections.");
                found = BitConverter.ToInt32(file, section + 12) + offset;
            }
            Assert.IsTrue(found >= 0, "Actual SE AOB does not match the canonical executable image.");
            return found;
        }

        [TestMethod]
        public void ActualExtenderBodiesAndMirrorClosureAcceptProductionIlTransformation()
        {
            foreach (var site in Sites)
            {
                Type owner = typeof(GameBuildingManagerAPI).Assembly.GetType("SHCDESE.Detours." + site[1], true);
                MethodInfo method = owner.GetMethod(site[3], BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.IsNotNull(method, site[1] + "." + site[3]);
                VerifyTransformedCopy(site, method);
                if (site[0] != "Build") continue;
                // The installed SE has an extra Original invocation in its generated MirrorTool closure.
                // This is part of the wrapper coverage contract, not a prescribed repository layout.
                var mirrors = owner.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                    .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                    .Where(candidate => candidate.Name.Contains("<" + site[3] + ">")).ToArray();
                Assert.IsTrue(mirrors.Length != 0, "Installed mirror construction path must be included in authoritative charging.");
                foreach (var mirror in mirrors) VerifyTransformedCopy(site, mirror);
            }
        }

        private static void VerifyTransformedCopy(string[] site, MethodInfo method)
        {
            object adapter = NewSite(site[0], DelegateType(site));
            MethodInfo transform = adapter.GetType().GetMethod("Transform", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
            {
                transform.Invoke(adapter, new object[] { context });
                // Generate the actual transformed installed body. This validates stack/delegate/closure
                // references without running the SE method or installing any process-wide hook.
                MethodInfo generated=definition.Generate();
                Assert.IsNotNull(generated, method.DeclaringType.FullName + "." + method.Name);
                Type signature=Expression.GetDelegateType(generated.GetParameters().Select(p=>p.ParameterType).Concat(new[]{generated.ReturnType}).ToArray());
                RuntimeHelpers.PrepareDelegate(generated.CreateDelegate(signature));
            }
        }

        [TestMethod]
        public void ActualExtenderDelegateSignaturesCompileAndDormantCallsPreserveOriginalResult()
        {
            var ready = typeof(HumanConstructionRuntime).GetField("ready", BindingFlags.Static | BindingFlags.NonPublic);
            bool previous = (bool)ready.GetValue(null);
            ready.SetValue(null, false);
            try
            {
                foreach (var site in Sites)
                {
                    Type delegateType = DelegateType(site);
                    var invoke = delegateType.GetMethod("Invoke");
                    var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                    var body = invoke.ReturnType == typeof(void) ? (Expression)Expression.Empty() : Expression.Constant(127L);
                    Delegate original = Expression.Lambda(delegateType, body, parameters).Compile();
                    object owner = NewSite(site[0], delegateType);
                    var wrap = owner.GetType().GetMethod("Wrap", BindingFlags.Instance | BindingFlags.NonPublic);
                    var wrapped = (Delegate)wrap.Invoke(owner, new object[] { original });
                    Assert.AreSame(wrapped, wrap.Invoke(owner, new object[] { original }), "A repeated Original must reuse the generated delegate.");
                    var call = Expression.Invoke(Expression.Constant(wrapped, delegateType), parameters.Select(p => Expression.Default(p.Type)));
                    if (invoke.ReturnType == typeof(void)) Expression.Lambda<Action>(call).Compile()();
                    else Assert.AreEqual(127L, Expression.Lambda<Func<long>>(call).Compile()(), site[0]);
                }
            }
            finally { ready.SetValue(null, previous); }
        }

        [TestMethod]
        public void DormantOriginalExceptionPassesThroughWithoutRetryOrReplacement()
        {
            var ready = typeof(HumanConstructionRuntime).GetField("ready", BindingFlags.Static | BindingFlags.NonPublic);
            bool previous = (bool)ready.GetValue(null);
            ready.SetValue(null, false);
            try
            {
                var sentinel = new InvalidOperationException("original fixture");
                string[] site = Sites[0]; Type delegateType = DelegateType(site);
                var parameters = delegateType.GetMethod("Invoke").GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                Delegate original = Expression.Lambda(delegateType, Expression.Throw(Expression.Constant(sentinel), typeof(long)), parameters).Compile();
                object owner = NewSite(site[0], delegateType);
                Delegate wrapped = (Delegate)owner.GetType().GetMethod("Wrap", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(owner, new object[] { original });
                var run = Expression.Lambda<Func<long>>(Expression.Invoke(Expression.Constant(wrapped, delegateType), parameters.Select(p => Expression.Default(p.Type)))).Compile();
                try { run(); Assert.Fail("The Original exception was swallowed."); }
                catch (InvalidOperationException error) { Assert.AreSame(sentinel, error); }
            }
            finally { ready.SetValue(null, previous); }
        }

        private static Type DelegateType(string[] site) => typeof(GameBuildingManagerAPI).Assembly
            .GetType("SHCDESE.Detours." + site[1], true).GetNestedType(site[2], BindingFlags.Public | BindingFlags.NonPublic);

        private static object NewSite(string operation, Type delegateType)
        {
            Type runtime = typeof(HumanConstructionRuntime);
            Type kind = runtime.GetNestedType("Operation", BindingFlags.NonPublic);
            Type site = runtime.GetNestedType("OriginalSite", BindingFlags.NonPublic);
            return Activator.CreateInstance(site, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { Enum.Parse(kind, operation), (object)delegateType }, null);
        }
    }
}
