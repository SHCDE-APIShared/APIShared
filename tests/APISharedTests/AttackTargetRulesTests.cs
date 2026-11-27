using System;
using System.Runtime.InteropServices;
using APIShared;
using APIShared.GameModes;
using APIShared.Simulation;
using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using APIShared.NativeSites;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass]
    public sealed class AttackTargetRulesTests
    {
        [TestMethod]
        public void OwnersCombineDirectedNeutralAndExactIdentityRulesWithoutLeakingAcrossWorlds()
        {
            var world = new MissionLifecycleState.NativeWorldState(1, 2);
            var replacement = new MissionLifecycleState.NativeWorldState(2, 2);
            var first = new AttackTargetRuleRegistration {
                World = world,
                MissionRules = new[] { new AttackTargetRule(
                    new AttackTargetSelector(3, eChimps.CHIMP_TYPE_ARCHER, 120),
                    new AttackTargetSelector(2), bidirectional: true) }
            };
            var second = new AttackTargetRuleRegistration {
                Configured = new[] { new AttackTargetRule(
                    new AttackTargetSelector(0, eChimps.CHIMP_TYPE_LION),
                    new AttackTargetSelector(2), condition: AttackTargetCondition.WhilePeaceTimeActive) }
            };
            var snapshot = new AttackTargetRuleSnapshot(new[] { first, second });
            var archer = new AttackTargetUnit(3, eChimps.CHIMP_TYPE_ARCHER, 120);
            var reused = new AttackTargetUnit(3, eChimps.CHIMP_TYPE_ARCHER, 121);
            var target = new AttackTargetUnit(2, eChimps.CHIMP_TYPE_ARCHER, 130);
            var lion = new AttackTargetUnit(0, eChimps.CHIMP_TYPE_LION, 140);
            Assert.IsTrue(snapshot.Excludes(archer, target, false, world));
            Assert.IsTrue(snapshot.Excludes(target, archer, false, world));
            Assert.IsFalse(snapshot.Excludes(reused, target, false, world));
            Assert.IsFalse(snapshot.Excludes(archer, target, false, replacement));
            Assert.IsFalse(snapshot.Excludes(lion, target, false, world));
            Assert.IsFalse(snapshot.Excludes(target, lion, true, world));
            Assert.IsTrue(snapshot.Excludes(lion, target, true, replacement));
            first.MissionRules = Array.Empty<AttackTargetRule>();
            Assert.IsTrue(snapshot.Excludes(archer, target, false, world));
            Assert.IsFalse(new AttackTargetRuleSnapshot(new[] { first, second }).Excludes(archer, target, false, world));
        }

        [TestMethod]
        public void MultiplayerPublicationRequiresAuthenticatedScopeAndRejectsOldRevisionsOnEachPeer()
        {
            var mode = new GameModeSnapshot(true, false, false, false, false, false, true, true, true,
                true, true, 2, 2, 0, 2, 2, 0, 0, 0, 0, default, default, 0, 0, 0, 0, 0, false);
            foreach (long session in new[] { 7L, 11L })
            {
                var mission = new MissionContext(session, MissionStartKind.NewGame, mode, "map", "map", false);
                var world = new MissionLifecycleState.NativeWorldState(session, session);
                var clock = new MissionLifecycleState.NativeClockState(mission, world);
                using (SimulationMutationScope.EnterTick())
                    Assert.IsFalse(AttackTargetRules.CanReplace(mission, clock, world, 1, 2,
                        SimulationMutationScope.IsAuthenticatedChore));
                using (SimulationMutationScope.EnterAuthenticatedChore())
                {
                    Assert.IsTrue(AttackTargetRules.CanReplace(mission, clock, world, 1, 2,
                        SimulationMutationScope.IsAuthenticatedChore));
                    Assert.IsFalse(AttackTargetRules.CanReplace(mission, clock, world, 2, 2, true));
                    Assert.IsFalse(AttackTargetRules.CanReplace(mission.With(session + 1, mode), clock, world, 2, 3, true));
                    Assert.IsTrue(AttackTargetRules.CanReplace(mission, clock, null, 200, 1, true));
                }
                Assert.IsFalse(SimulationMutationScope.IsAuthenticatedChore);
            }
            var unresolved = new MissionContext(12, MissionStartKind.NewGame, default, "map", "map", false);
            var unresolvedClock = new MissionLifecycleState.NativeClockState(unresolved,
                new MissionLifecycleState.NativeWorldState(12, 12));
            Assert.IsFalse(AttackTargetRules.CanReplace(unresolved, unresolvedClock, null, 0, 1, false));
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativePair(IntPtr manager, int attacker, int target, byte mode);

        [TestMethod]
        public void ActualIndirectBackendDisplacesEightBytesAndKeepsTheNativeResultOnNonmatch()
        {
            using (var target = ExecutableFunction.Create(a =>
            {
                a.db(AttackTargetRuleNative.Entry);
                a.mov(rbx, __qword_ptr[rsp + 8]);
                a.inc(__dword_ptr[rcx]);
                a.mov(eax, edx); a.add(eax, r8d); a.ret();
            }))
            {
                IntPtr calls = Marshal.AllocHGlobal(sizeof(int));
                NativeDetour<NativePair> hook = null;
                var callback = new NativePair((manager, attacker, victim, mode) =>
                    mode == 1 ? 1 : hook.Original(manager, attacker, victim, mode));
                try
                {
                    Marshal.WriteInt32(calls, 0);
                    var request = new DetourRequest<NativePair> {
                        Name = "Private attack target rule fixture", Callback = callback,
                        TargetAddress = Address(target.EntryPoint)
                    };
                    var backend = new NativeDetourBackend(AttackTargetRuleNative.Backend.Options with
                        { SuspendThreadsDuringPatch = false });
                    hook = (NativeDetour<NativePair>)backend.CreateDetour(in request);
                    NativeIndirectEntry.Validate(hook, target.EntryPoint, AttackTargetRuleNative.Entry,
                        AttackTargetRuleNative.DisplacedLength, false);
                    hook.Enable();
                    NativeIndirectEntry.Validate(hook, target.EntryPoint, AttackTargetRuleNative.Entry,
                        AttackTargetRuleNative.DisplacedLength, true);
                    var invoke = Marshal.GetDelegateForFunctionPointer<NativePair>(target.EntryPoint);
                    Assert.AreEqual(15, invoke(calls, 7, 8, 0));
                    Assert.AreEqual(1, invoke(calls, 7, 8, 1));
                    Assert.AreEqual(1, Marshal.ReadInt32(calls));
                    GC.KeepAlive(callback);
                }
                finally { hook?.Dispose(); Marshal.FreeHGlobal(calls); }
            }
        }

        [TestMethod]
        public void ExecutableDemandGatePreservesVanillaArgumentsAndBypassesManagedRulesWhenIdle()
        {
            IntPtr demand = Marshal.AllocHGlobal(sizeof(int));
            IntPtr peace = Marshal.AllocHGlobal(1);
            try
            {
                using (var original = ExecutableFunction.Create(a => { a.mov(eax, edx); a.add(eax, r8d); a.ret(); }))
                using (var callback = ExecutableFunction.Create(a => { a.movzx(eax, r9b); a.ret(); }))
                using (var gate = ExecutableFunction.Create(a => AttackTargetRuleNative.GenerateGate(a,
                    Address(demand), Address(peace), Address(original.EntryPoint), Address(callback.EntryPoint))))
                {
                    var invoke = Marshal.GetDelegateForFunctionPointer<NativePair>(gate.EntryPoint);
                    Marshal.WriteByte(peace, 1); Marshal.WriteInt32(demand, 0);
                    Assert.AreEqual(15, invoke(IntPtr.Zero, 7, 8, 4));
                    Marshal.WriteInt32(demand, 2);
                    Assert.AreEqual(4, invoke(IntPtr.Zero, 7, 8, 4));
                    Marshal.WriteByte(peace, 0);
                    Assert.AreEqual(15, invoke(IntPtr.Zero, 7, 8, 4));
                    Marshal.WriteInt32(demand, 1);
                    Assert.AreEqual(4, invoke(IntPtr.Zero, 7, 8, 4));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(peace);
                Marshal.FreeHGlobal(demand);
            }
        }

        private static ulong Address(IntPtr pointer) => unchecked((ulong)pointer.ToInt64());
    }
}
