using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using APIShared;
using BepInEx.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Issue #7: each capability checks at creation the Script Extender and game members its own code binds.
    [TestClass]
    [DoNotParallelize]
    public sealed class ManagedMemberContractTests
    {
        // Capability ID, the type whose code (with its nested types and the UnitAccess methods it calls)
        // the contract must cover, and the method that creates the capability.
        private static readonly (string Id, string Type, string Creator)[] Capabilities =
        {
            (NativeCapabilityIds.MissionLifecycle, "APIShared.MissionLifecycleService", "TryCreate"),
            (NativeCapabilityIds.LobbyState, "APIShared.LobbyStateService", "TryCreate"),
            (NativeCapabilityIds.PlayerDefeat, "APIShared.PlayerDefeatService", "TryCreate"),
            (NativeCapabilityIds.BriefingGoldPresentation, "APIShared.BriefingGoldPresentationService", "TryCreate"),
            (NativeCapabilityIds.HudExtrasButtons, "APIShared.HudExtrasButtonsService", null),
            (NativeCapabilityIds.BuildingRepair, "APIShared.BuildingRepairService", "TryCreate"),
            (NativeCapabilityIds.UnitHudPresentation, "APIShared.UnitHudPresentationService", "TryCreate"),
            (NativeCapabilityIds.BuildingPanelExtensions, "APIShared.BuildingPanelExtensionsService", "TryCreate"),
        };
        private static readonly string[] Helpers = { "APIShared.UnitAccess" };

        [TestMethod]
        public void EveryContractResolvesAndListsExactlyTheCompiledMembers()
        {
            CollectionAssert.AreEquivalent(Capabilities.Select(c => c.Id).ToArray(), ManagedMemberContracts.ByCapability.Keys.ToArray());
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(ApiSharedRuntime).Assembly.Location))
            {
                Mono.Cecil.ModuleDefinition module = assembly.MainModule;
                foreach (var capability in Capabilities)
                {
                    var resolved = new List<MemberInfo>();
                    string mismatch = ManagedMemberContracts.ByCapability[capability.Id]().FindMismatch(resolved);
                    Assert.IsNull(mismatch, capability.Id + " would be unavailable although the installed members resolve: " + mismatch);
                    var listed = new SortedSet<string>(resolved.Select(member => Positional(member, member is FieldInfo field
                        ? module.ImportReference(field).FullName : module.ImportReference((MethodBase)member).FullName)), StringComparer.Ordinal);
                    SortedSet<string> compiled = CompiledMembers(module, capability.Type);
                    string missing = string.Join("; ", compiled.Except(listed).ToArray());
                    string extra = string.Join("; ", listed.Except(compiled).ToArray());
                    Assert.IsTrue(missing.Length == 0 && extra.Length == 0,
                        capability.Id + " member list differs from its code. Not listed: " + missing + ". Listed but unused: " + extra);
                }
            }
        }

        [TestMethod]
        public void ARenamedOrRetypedMemberDisablesOnlyItsCapability()
        {
            var saved = new Dictionary<string, Func<ManagedMemberContract>>(ManagedMemberContracts.ByCapability);
            var errors = new List<string>();
            var log = new ManualLogSource("ManagedMemberContractFixture");
            log.LogEvent += (_, entry) => { if (entry.Level == LogLevel.Error) errors.Add(entry.Data?.ToString()); };
            try
            {
                // Renamed: the compiled code still names r_GlobalId, which this list stands in for.
                ManagedMemberContracts.ByCapability[NativeCapabilityIds.PlayerDefeat] = () => PlayerDefeatService.MemberContract()
                    .Field(typeof(SHCDESE.Interop.GameUnit), "r_GlobalIdRenamed", typeof(uint));
                Assert.IsFalse(PlayerDefeatService.TryCreate(log, null, out PlayerDefeatService defeat, out NativeCapabilityDiagnostic defeatDiagnostic));
                Assert.IsNull(defeat);
                AssertUnavailable(defeatDiagnostic, NativeCapabilityIds.PlayerDefeat, "r_GlobalIdRenamed");
                AssertOthersValidate(NativeCapabilityIds.PlayerDefeat);
                ManagedMemberContracts.ByCapability[NativeCapabilityIds.PlayerDefeat] = saved[NativeCapabilityIds.PlayerDefeat];

                // Retyped: SkirmishMember is a bool in the installed game.
                ManagedMemberContracts.ByCapability[NativeCapabilityIds.LobbyState] = () => LobbyStateService.MemberContract()
                    .Field(typeof(Platform_Multiplayer.MPLobbyMember), "SkirmishMember", typeof(int));
                Assert.IsFalse(LobbyStateService.TryCreate(log, out LobbyStateService lobby, out NativeCapabilityDiagnostic lobbyDiagnostic));
                Assert.IsNull(lobby);
                AssertUnavailable(lobbyDiagnostic, NativeCapabilityIds.LobbyState, "SkirmishMember");
                AssertOthersValidate(NativeCapabilityIds.LobbyState);

                Assert.AreEqual(2, errors.Count, "Each failed check logs exactly once.");
                StringAssert.Contains(errors[0], "capability=player-defeat");
                StringAssert.Contains(errors[1], "capability=lobby-state");
            }
            finally
            {
                foreach (var entry in saved) ManagedMemberContracts.ByCapability[entry.Key] = entry.Value;
            }
        }

        [TestMethod]
        public void EveryCapabilityChecksItsMembersBeforeTouchingThem()
        {
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(ApiSharedRuntime).Assembly.Location))
            {
                Mono.Cecil.ModuleDefinition module = assembly.MainModule;
                foreach (var capability in Capabilities)
                {
                    Mono.Cecil.TypeDefinition type = module.GetType(capability.Type);
                    IEnumerable<Mono.Cecil.MethodDefinition> creators = capability.Creator == null
                        ? module.GetType("APIShared.ApiSharedRuntime").Methods.Where(m => m.Name == "InitializeManaged")
                        : type.Methods.Where(m => m.Name == capability.Creator);
                    int checkedCreators = 0;
                    foreach (Mono.Cecil.MethodDefinition creator in creators)
                    {
                        var code = creator.Body.Instructions.ToList();
                        // An overload that forwards to another creator of the same type is checked there.
                        if (capability.Creator != null && code.Any(i => i.Operand is Mono.Cecil.MethodReference m &&
                            m.Name == capability.Creator && m.DeclaringType.FullName == capability.Type) &&
                            !code.Any(i => IsCheck(i))) continue;
                        int check = code.FindIndex(i => IsCheck(i));
                        Assert.IsTrue(check > 0, creator.FullName + " does not check its members.");
                        Assert.IsTrue(code.Take(check).Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && (string)i.Operand == capability.Id),
                            creator.FullName + " checks another capability's members.");
                        int touch = code.FindIndex(i => Touches(i, capability.Type));
                        Assert.IsTrue(touch < 0 || check < touch, creator.FullName + " touches game members before its check.");
                        checkedCreators++;
                    }
                    Assert.AreEqual(1, checkedCreators, capability.Type + " has no checked creator.");
                }
            }
        }

        [TestMethod]
        public void RepeatedPlayerDefeatObservationFailuresAreThrottled()
        {
            var errors = new List<string>();
            var log = new ManualLogSource("PlayerDefeatThrottleFixture");
            log.LogEvent += (_, entry) => { if (entry.Level == LogLevel.Error) errors.Add(entry.Data?.ToString()); };
            PlayerDefeatService service = PlayerDefeatService.TestCreate(() => 7,
                () => throw new MissingFieldException("SHCDESE.Interop.GameUnit", "r_GlobalId"), log);
            Assert.IsTrue(service.Bind("owner").TryRegisterObserver("defeats", null, _ => { }, out _));
            for (int tick = 0; tick < 2048; tick++) service.TestTick(tick);
            Assert.AreEqual(3, errors.Count, "Expected the 1st, 1024th and 2048th failure only.");
            StringAssert.Contains(errors[2], "2048 failures");
        }

        // Compiled references to members of a constructed generic type write its type parameters by
        // position (!0); Cecil's import of the reflected member writes their names. Use positions.
        private static string Positional(MemberInfo member, string fullName)
        {
            if (member.DeclaringType == null || !member.DeclaringType.IsGenericType) return fullName;
            Type[] parameters = member.DeclaringType.GetGenericTypeDefinition().GetGenericArguments();
            for (int index = 0; index < parameters.Length; index++)
                fullName = System.Text.RegularExpressions.Regex.Replace(fullName,
                    @"(?<=[<,( ])" + parameters[index].Name + @"(?=[>,)&*\[ ])", "!" + index);
            return fullName;
        }

        private static bool IsCheck(Mono.Cecil.Cil.Instruction instruction) =>
            instruction.Operand is Mono.Cecil.MethodReference m && m.Name == "TryValidate" &&
            m.DeclaringType.FullName == "APIShared.ManagedMemberContracts";

        // Creating the service, or any call or field access into the Script Extender or the game.
        private static bool Touches(Mono.Cecil.Cil.Instruction instruction, string serviceType)
        {
            if (instruction.OpCode.Code == Mono.Cecil.Cil.Code.Newobj && instruction.Operand is Mono.Cecil.MethodReference created &&
                created.DeclaringType.FullName == serviceType) return true;
            return instruction.Operand is Mono.Cecil.MemberReference member && !(member is Mono.Cecil.TypeReference) &&
                (member.DeclaringType.Scope.Name == "SHCDESE" || member.DeclaringType.Scope.Name == "Assembly-CSharp");
        }

        private static void AssertUnavailable(NativeCapabilityDiagnostic diagnostic, string id, string member)
        {
            Assert.AreEqual(id, diagnostic.CapabilityId);
            Assert.AreEqual(NativeCapabilityState.ValidationFailed, diagnostic.State);
            StringAssert.Contains(diagnostic.Reason, member);
        }

        private static void AssertOthersValidate(string failing)
        {
            foreach (var capability in Capabilities.Where(c => c.Id != failing))
                Assert.IsTrue(ManagedMemberContracts.TryValidate(capability.Id, null, out _), capability.Id + " became unavailable as well.");
        }

        // Field and method references into SHCDESE and Assembly-CSharp made by the type, its nested types
        // and the helper methods it reaches in Helpers.
        private static SortedSet<string> CompiledMembers(Mono.Cecil.ModuleDefinition module, string typeName)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<Mono.Cecil.MethodDefinition>();
            var types = new Queue<Mono.Cecil.TypeDefinition>(new[] { module.GetType(typeName) });
            while (types.Count > 0)
            {
                Mono.Cecil.TypeDefinition type = types.Dequeue();
                foreach (Mono.Cecil.MethodDefinition method in type.Methods) pending.Enqueue(method);
                foreach (Mono.Cecil.TypeDefinition nested in type.NestedTypes) types.Enqueue(nested);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                Mono.Cecil.MethodDefinition method = pending.Dequeue();
                if (!seen.Add(method.FullName) || !method.HasBody) continue;
                foreach (Mono.Cecil.Cil.Instruction instruction in method.Body.Instructions)
                {
                    if (!(instruction.Operand is Mono.Cecil.MemberReference member) ||
                        !(member is Mono.Cecil.FieldReference || member is Mono.Cecil.MethodReference)) continue;
                    string scope = member.DeclaringType.Scope.Name;
                    if (scope == "SHCDESE" || scope == "Assembly-CSharp") result.Add(member.FullName);
                    else if (member is Mono.Cecil.MethodReference called && called.DeclaringType.Scope == module &&
                        Helpers.Contains(called.DeclaringType.FullName))
                    {
                        Mono.Cecil.MethodDefinition helper = called.Resolve();
                        if (helper != null) pending.Enqueue(helper);
                    }
                }
            }
            return result;
        }
    }
}
