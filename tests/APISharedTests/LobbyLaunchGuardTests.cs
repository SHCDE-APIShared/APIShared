using System;
using System.Collections.Generic;
using System.Linq;
using APIShared;
using APIShared.Lobby;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class LobbyLaunchGuardTests
    {
        [TestMethod]
        public void OrderedRejectionsNeverRunLaterGuardsAndDemandRemainsDynamic()
        {
            var seen = new List<string>();
            bool required = false;
            var registry = new LobbyLaunchGuardRegistry(() => { }, () => "failed");
            Assert.IsTrue(registry.TryRegister("z", "last", _ => { seen.Add("z"); return "late"; }, out _));
            Assert.IsTrue(registry.TryRegister("a", "first", _ => { seen.Add("a"); return required ? "not synchronized" : null; }, out _));
            var context = new LobbyLaunchGuardContext(null, "Play", LobbyLaunchOperation.Start);
            Assert.AreEqual("late", registry.Evaluate(context).Reason);
            CollectionAssert.AreEqual(new[] { "a", "z" }, seen);
            seen.Clear(); required = true;
            var blocked = registry.Evaluate(context);
            Assert.AreEqual("a", blocked.Owner);
            Assert.AreEqual("not synchronized", blocked.Reason);
            CollectionAssert.AreEqual(new[] { "a" }, seen);
        }

        [TestMethod]
        public void ErrorsFailClosedAndFailedInstallationDoesNotRegister()
        {
            int attempts = 0;
            var registry = new LobbyLaunchGuardRegistry(() => {
                attempts++; if (attempts == 1) throw new MissingMethodException("changed game method");
            }, () => "guard failed");
            Assert.IsFalse(registry.TryRegister("owner", "id", _ => null, out string failure));
            StringAssert.Contains(failure, "changed game method");
            Assert.IsFalse(registry.Evaluate(new LobbyLaunchGuardContext(null, "Ready", LobbyLaunchOperation.Ready)).Blocked);
            Assert.IsTrue(registry.TryRegister("owner", "id", _ => throw new InvalidOperationException("bad callback"), out _));
            Assert.IsFalse(registry.TryRegister("owner", "id", _ => null, out _));
            var decision = registry.Evaluate(new LobbyLaunchGuardContext(null, "Ready", LobbyLaunchOperation.Ready));
            Assert.IsTrue(decision.Blocked);
            Assert.AreEqual("guard failed", decision.Reason);
            StringAssert.Contains(decision.Detail, "bad callback");
        }

        [TestMethod]
        public void RegistrationWritesDoNotInvalidateAnExecutingSnapshot()
        {
            var registry = new LobbyLaunchGuardRegistry(() => { }, () => "failed");
            bool added = false;
            Assert.IsTrue(registry.TryRegister("a", "first", contextArg => {
                if (!added) { added = true; Assert.IsTrue(registry.TryRegister("b", "next", __ => "later invocation", out _)); }
                return null;
            }, out _));
            var context = new LobbyLaunchGuardContext(null, "Play", LobbyLaunchOperation.Start);
            Assert.IsFalse(registry.Evaluate(context).Blocked);
            Assert.AreEqual("later invocation", registry.Evaluate(context).Reason);
        }

        [TestMethod]
        public void OnlyActualReadyAndStartCommandsAreClassified()
        {
            foreach (string command in new[] { "Ready", "ReadyLock", "Play", "COOP_START" })
                Assert.IsTrue(LobbyLaunchGuardRegistry.TryGetOperation(command, out _));
            foreach (string command in new[] { null, "", "Leave", "Chat", "play", "Unready" })
                Assert.IsFalse(LobbyLaunchGuardRegistry.TryGetOperation(command, out _));
            Assert.IsFalse(LobbyLaunchGuardRegistry.ShouldCheck("Ready", true, false, false));
            Assert.IsFalse(LobbyLaunchGuardRegistry.ShouldCheck("ReadyLock", true, true, false));
            Assert.IsFalse(LobbyLaunchGuardRegistry.ShouldCheck("Play", true, true, true));
            Assert.IsTrue(LobbyLaunchGuardRegistry.ShouldCheck("Ready", false, false, false));
            Assert.IsTrue(LobbyLaunchGuardRegistry.ShouldCheck("Play", true, true, false));
        }

        [TestMethod]
        public void OnlyTheExactVanillaLocalCommandBranchBypassesPeerAgreement()
        {
            Assert.IsFalse(LobbyLaunchGuardRegistry.RequiresPeerAgreement("Play", true, true, false));
            Assert.IsFalse(LobbyLaunchGuardRegistry.RequiresPeerAgreement("COOP_START", true, false, true));
            Assert.IsTrue(LobbyLaunchGuardRegistry.RequiresPeerAgreement("Play", true, false, true),
                "A stale Coop flag cannot bypass the network Play branch.");
            Assert.IsTrue(LobbyLaunchGuardRegistry.RequiresPeerAgreement("COOP_START", true, true, false),
                "A stale skirmish flag cannot bypass the network Coop branch.");
            foreach (string command in new[] { "Ready", "ReadyLock", "unknown", null })
                Assert.IsTrue(LobbyLaunchGuardRegistry.RequiresPeerAgreement(command, true, true, true));
            foreach (string command in new[] { "Ready", "ReadyLock", "Play", "COOP_START" })
                Assert.IsTrue(LobbyLaunchGuardRegistry.RequiresPeerAgreement(command, false, true, true));
            Assert.IsTrue(new LobbyLaunchGuardContext(null, "Play", LobbyLaunchOperation.Start).RequiresPeerAgreement);
            // No MPGameActive/Director flag is an input: both are still false in a
            // fresh network lobby before Ready/Play commits the actual game start.
        }

        [TestMethod]
        public void PreparedSaveUsesItsValidatedHumanRosterAndCurrentGuardDecision()
        {
            var solo = LobbyLaunchGuardRegistry.PreparedMultiplayerLoadContext(1);
            var peers = LobbyLaunchGuardRegistry.PreparedMultiplayerLoadContext(2);
            Assert.IsNull(peers.Lobby);
            Assert.AreEqual("HostLoadGame", peers.Command);
            Assert.AreEqual(LobbyLaunchOperation.Start, peers.Operation);
            Assert.IsFalse(solo.RequiresPeerAgreement, "The existing solo-host save path needs no network echo or second report.");
            Assert.IsTrue(peers.RequiresPeerAgreement);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LobbyLaunchGuardRegistry.PreparedMultiplayerLoadContext(0));
            bool matchingHistory = false;
            var registry = new LobbyLaunchGuardRegistry(() => { }, () => "guard failed");
            Assert.IsFalse(registry.Evaluate(peers).Blocked, "No active consumer must not block ordinary saves.");
            Assert.IsTrue(registry.TryRegister("external.mod", "history", context =>
                context.RequiresPeerAgreement && !matchingHistory ? "native history protection differs" : null, out _));
            Assert.IsFalse(registry.Evaluate(solo).Blocked);
            Assert.IsTrue(registry.Evaluate(peers).Blocked, "A neutral percent does not make saved native history irrelevant.");
            matchingHistory = true;
            Assert.IsFalse(registry.Evaluate(peers).Blocked, "Each acknowledgement/release/start reevaluates current reports.");
            matchingHistory = false;
            Assert.IsTrue(registry.Evaluate(peers).Blocked, "A previous successful decision is not a reusable admission ticket.");
        }

        [TestMethod]
        public void ButtonAndPreparedSaveShareTheSameRecursionBoundaryWithoutLeakingIt()
        {
            var registry = new LobbyLaunchGuardRegistry(() => { }, () => "recursive launch refused");
            bool nest = true;
            Assert.IsTrue(registry.TryRegister("external.mod", "recursive", context => {
                if (!nest) return null;
                var nested = registry.Evaluate(LobbyLaunchGuardRegistry.PreparedMultiplayerLoadContext(2));
                Assert.IsTrue(nested.Blocked);
                return nested.Reason;
            }, out _));
            var button = new LobbyLaunchGuardContext(null, "Play", LobbyLaunchOperation.Start);
            Assert.IsTrue(registry.Evaluate(button).Blocked);
            nest = false;
            Assert.IsFalse(registry.Evaluate(button).Blocked, "The recursion flag clears before a later admitted action.");
            Assert.IsFalse(registry.Evaluate(LobbyLaunchGuardRegistry.PreparedMultiplayerLoadContext(2)).Blocked);
        }

        [TestMethod]
        public void CompiledPreparedSaveBoundariesCannotBypassCentralGuards()
        {
            // This checks the actual compiled coordinator, not a simulated Steam or
            // Unity environment. Registry decisions/context/recursion run above. These
            // three call bindings are the safety contract that a Ready-button-only test
            // missed: preparation may select different settings or saved native history.
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(SavegameLoadCoordinator).Assembly.Location))
            {
                var coordinator = assembly.MainModule.GetType(typeof(SavegameLoadCoordinator).FullName);
                var tick = coordinator.Methods.Single(method => method.Name == "Tick").Body.Instructions.ToList();
                var startBody = coordinator.Methods.Single(method => method.Name == "ValidateStart").Body;
                var start = startBody.Instructions.ToList();
                bool Call(Mono.Cecil.Cil.Instruction instruction, string owner, string name) =>
                    instruction.Operand is Mono.Cecil.MethodReference method && method.DeclaringType.FullName == owner && method.Name == name;
                var tickGuards = tick.Select((instruction, index) => new { instruction, index })
                    .Where(item => Call(item.instruction, coordinator.FullName, "RequirePreparedLaunchGuards"))
                    .Select(item => item.index).ToArray();
                Assert.AreEqual(2, tickGuards.Length, "Both host release and client acknowledgement need central admission.");
                int release = tick.FindIndex(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Stsfld &&
                    instruction.Operand is Mono.Cecil.FieldReference field && field.Name == "released");
                int original = tick.FindIndex(instruction => instruction.Operand is Mono.Cecil.MethodReference method &&
                    method.Name == "Invoke" && method.DeclaringType.FullName == coordinator.FullName + "/HostLoad");
                int accept = tick.FindIndex(instruction => Call(instruction, typeof(SavegameModSettings).FullName, "AcceptPreparedClient"));
                int acknowledge = tick.FindIndex(instruction => instruction.Operand is Mono.Cecil.MethodReference method && method.Name == "SendToSteamPeer");
                Assert.IsTrue(release > tickGuards[0] && original > release, "A rejected host must not release or call Vanilla.");
                Assert.IsTrue(tickGuards[1] > original && accept > tickGuards[1] && acknowledge > accept,
                    "A rejected client must not accept prepared settings or send an acknowledgement.");
                foreach (int guard in tickGuards)
                {
                    int prepare = tick.FindLastIndex(guard, instruction => Call(instruction, typeof(APIShared.ModSettings.ModSettingsApplication).FullName, "PrepareLaunch"));
                    int fingerprint = tick.FindLastIndex(guard, instruction => Call(instruction, coordinator.FullName, "FingerprintMatches"));
                    Assert.IsTrue(prepare >= 0 && fingerprint > prepare && fingerprint < guard &&
                        tick[fingerprint - 1].OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_1,
                        "Admission must follow launch preparation and its fresh decisive fingerprint.");
                }
                int finalGuard = start.FindIndex(instruction => Call(instruction, coordinator.FullName, "RequirePreparedLaunchGuards"));
                int loading = start.FindIndex(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Stsfld &&
                    instruction.Operand is Mono.Cecil.FieldReference field && field.Name == "loading");
                int finalPrepare = start.FindLastIndex(instruction => Call(instruction, typeof(APIShared.ModSettings.ModSettingsApplication).FullName, "PrepareLaunch"));
                int finalFingerprint = start.FindLastIndex(instruction => Call(instruction, typeof(SavegameModSettings).FullName, "CaptureHostFingerprint"));
                Assert.IsTrue(finalPrepare >= 0 && finalFingerprint > finalPrepare && finalGuard > finalFingerprint && loading > finalGuard,
                    "ValidateStart must recheck guards before entering native loading, even after host/client admission.");
                var rejection = startBody.ExceptionHandlers.Single(handler =>
                    handler.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Catch &&
                    start.IndexOf(handler.TryStart) <= finalGuard && start.IndexOf(handler.TryEnd) > finalGuard);
                var cleanup = start.Skip(start.IndexOf(rejection.HandlerStart))
                    .Take(start.IndexOf(rejection.HandlerEnd) - start.IndexOf(rejection.HandlerStart)).ToList();
                Assert.IsTrue(cleanup.Any(instruction => Call(instruction, coordinator.FullName, "Abort")) &&
                    cleanup.Any(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Rethrow),
                    "Final rejection must cancel the released preparation and still unwind before mission state begins.");
                var boundary = coordinator.Methods.Single(method => method.Name == "RequirePreparedLaunchGuards").Body.Instructions;
                Assert.AreEqual(1, boundary.Count(instruction => Call(instruction, typeof(LobbyLaunchGuards).FullName, "EvaluatePreparedMultiplayerLoad")));
                Assert.IsTrue(boundary.Any(instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Throw),
                    "A blocking central decision must unwind the existing readiness/start path.");
            }
        }
    }
}
