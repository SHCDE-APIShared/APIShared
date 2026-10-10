using System;
using System.Reflection;
using APIShared.Commands;
using APIShared.Events;
using APIShared.Presentation;
using APIShared.Recruitment;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod]
        public void RecruitmentCtrlCeilingBecomesConcreteEvenWhenAssignedTheSameAmount()
        {
            var args = new GameActionPreEventArgs(Enums.GameActionCommand.MakeTroop, 1000, 22, 0);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(args.InterpretCtrlSentinel);
            var events = new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>();
            events.TryRegister("a", "costs", a => a.StructureId = 1000, null, out _);
            events.TryRegister("b", "limits", a => {
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(a.InterpretCtrlSentinel);
                var decision = RecruitmentRequestPolicy.ApplyMaximum(a.StructureId, 7, 20, a.InterpretCtrlSentinel);
                a.StructureId = decision.AmountToForward;
            }, null, out _);
            var invocation = events.Begin(args);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(20, invocation.Pre.StructureId);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(invocation.Pre.HasConcreteRecruitmentAmount);
        }

        [TestMethod]
        public void FailingRecruitmentObserverRestoresTheSentinelAndAllArguments()
        {
            var events = new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>();
            events.TryRegister("a", "bad", a => {
                a.StructureId = 1000; a.ActionState = 55; a.Value2 = 5; a.SkipOriginalFunction = true; throw new Exception();
            }, null, out _);
            var invocation = events.Begin(new GameActionPreEventArgs(Enums.GameActionCommand.MakeTroop, 1000, 22, 0));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(invocation.Pre.InterpretCtrlSentinel);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(invocation.Pre.HasConcreteRecruitmentAmount);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(invocation.Pre.SkipOriginalFunction);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(22, invocation.Pre.ActionState);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(0, invocation.Pre.Value2);
        }

        [TestMethod]
        public void ReservationUsesFinalAmountAndVetoIsNotARecruitmentAcknowledgement()
        {
            var events = new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>();
            int reservation = -1;
            events.TryRegister("a", "limit", a => a.State = 8, a => reservation = a.WasSkipped ? 0 :
                RecruitmentRequestPolicy.ReconcilePendingAmount((int)a.State, a.StructureId, a.HasConcreteRecruitmentAmount), out _);
            events.TryRegister("b", "cost", a => a.StructureId = 3, null, out _);
            var invocation = events.Begin(new GameActionPreEventArgs(Enums.GameActionCommand.MakeTroop, 1000, 22, 0));
            invocation.Complete(s => new GameActionPostEventArgs(invocation.Pre, 0, s));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(3, reservation);
            events.TryRegister("c", "veto", a => a.SkipOriginalFunction = true, null, out _);
            invocation = events.Begin(new GameActionPreEventArgs(Enums.GameActionCommand.MakeTroop, 1000, 22, 0));
            invocation.Complete(s => new GameActionPostEventArgs(invocation.Pre, 0, s));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(0, reservation);
        }

        [TestMethod]
        public void PresentationPreservesFinalParameterAndReportsOriginalErrorsForCleanup()
        {
            var events = new InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs>();
            var state = new object();
            var error = new Exception("original failed");
            bool cleaned = false;
            events.TryRegister("a", "scope", a => { a.Parameter = "GuardStanceButton"; a.State = state; }, a => {
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreSame(state, a.State); Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreSame(error, a.OriginalException);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(a.OriginalCompleted); Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual("GuardStanceButton", a.Parameter); cleaned = true;
            }, out _);
            var invocation = events.Begin(new PresentationPreEventArgs(PresentationOperation.TroopPanelEnter, null, "StanceButton"));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual("StanceButton", invocation.Pre.OriginalParameter);
            invocation.Complete(s => new PresentationPostEventArgs(invocation.Pre, s, error));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(cleaned);
        }

        [TestMethod]
        public void ReplacementRunsAfterAllPreWithFinalParameterAndLaterVetoSuppressesEveryEffect()
        {
            foreach (bool veto in new[] { false, true })
            {
                int replacements = 0, originals = 0;
                PresentationPostEventArgs completed = null;
                var events = new InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs>();
                events.TryRegister("a", "replacement", a => a.Replacement = p => {
                    Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual("final", p); replacements++;
                }, a => completed = a, out _);
                events.TryRegister("b", "broken", a => { a.Replacement = p => throw new Exception(); throw new Exception(); }, null, out _);
                events.TryRegister("c", "last", a => { a.Parameter = "final"; a.SkipOriginalFunction = veto; }, null, out _);
                PresentationEvents.Execute(events, new PresentationPreEventArgs(PresentationOperation.RechargeSiegeAmmo, null, "initial"), p => originals++);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(veto ? 0 : 1, replacements);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(0, originals);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(veto, completed.WasSkipped);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(!veto, completed.WasReplaced);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(completed.OriginalCompleted);
            }
        }

        [TestMethod]
        public void ReplacementFailureStillCleansUpPostAndCannotRetryVanilla()
        {
            int originals = 0;
            var error = new InvalidOperationException("replacement");
            PresentationPostEventArgs completed = null;
            var events = new InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs>();
            events.TryRegister("a", "replacement", a => a.Replacement = p => throw error, a => completed = a, out _);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<InvalidOperationException>(() => PresentationEvents.Execute(events,
                new PresentationPreEventArgs(PresentationOperation.RechargeSiegeAmmo, null, null), p => originals++));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(0, originals);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreSame(error, completed.CompletionException);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNull(completed.OriginalException);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(completed.WasReplaced);
        }

        [TestMethod]        public void InstalledManagedTargetsAndFullRecruitmentMaterialIlContractAreValid()
        {
            MethodInfo gui = typeof(FatControler).GetMethod("NoesisGUIUpdateChecksInGame", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(gui);
            RecruitmentMaterialUiIlContract.Validate(gui);
            foreach (string name in new[] { "ButtonEnterCreateTroop", "ButtonLeaveCreateTroop", "ButtonTroopPanelMouseEnter", "ButtonTroopPanelMouseLeave", "ButtonUnitRechargeRock" })
            {
                var method = typeof(CrusaderDE.MainViewModel).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(object) }, null);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(method, name); Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(typeof(void), method.ReturnType);
            }
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(typeof(EngineInterface).GetMethod("GameAction", new[] { typeof(Enums.GameActionCommand), typeof(int), typeof(int), typeof(int) }));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(typeof(CrusaderDE.HUD_Main).GetMethod("UpdateRollover", Type.EmptyTypes));
        }
    }
}
