using System;
using System.Reflection;
using APIShared.Commands;
using APIShared.Events;
using APIShared.Presentation;
using APIShared.Recruitment;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod]
        public void GameActionValidatorChecksFinalInputsAndOwnStateAfterEqualPriorityPre()
        {
            var events = new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>();
            var ticket = new object();
            GameActionFinalValidationEventArgs inputs = null;
            GameActionPostEventArgs completion = null;
            int accepted = 0, originals = 0;
            TestAssert.IsTrue(GameActionEvents.TryRegisterValidator(events, "a", "guard", a => a.State = ticket,
                a => completion = a, a => { inputs = a; return a.Value2 == a.OriginalValue2; },
                out _, order: int.MaxValue, accepted: _ => accepted++));
            TestAssert.IsFalse(events.TryRegister("a", "guard", _ => { }, null, out _));
            TestAssert.IsTrue(events.TryRegister("b", "edit", a =>
            {
                a.StructureId = 4;
                a.ActionState = 5;
                a.Value2 = 6;
                a.State = new object();
            }, null, out _, order: int.MaxValue));

            int result = GameActionEvents.DispatchThrough(events, Enums.GameActionCommand.Ally_Orders, 1, 2, 3,
                (_, _, _, _) => ++originals);
            TestAssert.AreEqual(0, result);
            TestAssert.AreEqual(0, originals);
            TestAssert.AreEqual(0, accepted);
            TestAssert.AreEqual(Enums.GameActionCommand.Ally_Orders, inputs.Command);
            TestAssert.AreEqual((1, 2, 3), (inputs.OriginalStructureId, inputs.OriginalActionState, inputs.OriginalValue2));
            TestAssert.AreEqual((4, 5, 6), (inputs.StructureId, inputs.ActionState, inputs.Value2));
            TestAssert.AreSame(ticket, inputs.State);
            TestAssert.AreSame(ticket, completion.State);
            TestAssert.IsTrue(completion.WasSkipped);
        }

        [TestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(false, false)]
        public void GameActionValidatorHandlesPreVetoFailureAndAcceptance(bool preVeto, bool validatorFails)
        {
            var events = new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>();
            GameActionPostEventArgs completion = null;
            int validations = 0, accepted = 0, originals = 0;
            TestAssert.IsTrue(events.TryRegister("a", "optional-veto", a => a.SkipOriginalFunction = preVeto, null, out _));
            TestAssert.IsTrue(GameActionEvents.TryRegisterValidator(events, "b", "guard", null,
                a => completion = a, _ =>
                {
                    validations++;
                    if (validatorFails) throw new InvalidOperationException("validator failed");
                    return true;
                }, out _, accepted: _ => accepted++));
            int result = GameActionEvents.DispatchThrough(events, Enums.GameActionCommand.Ally_RequestGoods, 1, 2, 3,
                (_, _, _, _) => ++originals);

            bool skipped = preVeto || validatorFails;
            TestAssert.AreEqual(preVeto ? 0 : 1, validations);
            TestAssert.AreEqual(validatorFails ? 1L : 0L, events.CallbackFailures);
            TestAssert.AreEqual(skipped ? 0 : 1, accepted);
            TestAssert.AreEqual(skipped ? 0 : 1, originals);
            TestAssert.AreEqual(skipped ? 0 : 1, result);
            TestAssert.AreEqual(skipped, completion.WasSkipped);
        }

        [TestMethod]
        [DataRow("Pre")]
        [DataRow("FinalValidation")]
        [DataRow("Accepted")]
        [DataRow("Post")]
        public void GameActionBlocksOnlyConfiguredNestedCommandsForTheActiveInvocation(string phase)
        {
            var events = new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>();
            var guarded = Enums.GameActionCommand.Ally_Orders;
            var ordinary = Enums.GameActionCommand.MakeTroop;
            var commandList = new[] { guarded };
            bool active = true;
            int nestedGuarded = -1, nestedOrdinary = -1, originals = 0, notifications = 0;
            GameActionEvents.Call original = (_, _, _, _) => { originals++; return 99; };
            void InvokeNested()
            {
                notifications++;
                // The protection and activation remain paired with the invoking callback.
                active = false;
                nestedGuarded = GameActionEvents.DispatchThrough(events, guarded, 1, 2, 3, original);
                nestedOrdinary = GameActionEvents.DispatchThrough(events, ordinary, 1, 2, 3, original);
            }

            TestAssert.IsTrue(GameActionEvents.TryRegisterValidator(events, "a", "guard", null, null,
                _ => true, out _, strictNestedKinds: commandList, isActive: () => active));
            TestAssert.IsTrue(GameActionEvents.TryRegisterValidator(events, "b", "nested",
                phase == "Pre" ? _ => InvokeNested() : null,
                phase == "Post" ? _ => InvokeNested() : null,
                _ =>
                {
                    if (phase == "FinalValidation") InvokeNested();
                    return true;
                },
                out _, accepted: phase == "Accepted" ? _ => InvokeNested() : null));
            commandList[0] = ordinary;

            TestAssert.AreEqual(99, GameActionEvents.DispatchThrough(events, ordinary, 1, 2, 3, original));
            TestAssert.AreEqual(2, originals);
            TestAssert.AreEqual(0, nestedGuarded);
            TestAssert.AreEqual(99, nestedOrdinary);
            TestAssert.AreEqual(99, GameActionEvents.DispatchThrough(events, ordinary, 1, 2, 3, original));
            TestAssert.AreEqual(5, originals);
            TestAssert.AreEqual(99, nestedGuarded);
            TestAssert.AreEqual(99, nestedOrdinary);
            TestAssert.AreEqual(2, notifications);
        }

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
        [DataRow(PresentationOperation.RechargeSiegeAmmo)]
        [DataRow(PresentationOperation.MarketSelectGoods)]
        [DataRow(PresentationOperation.MarketCycleGoods)]
        [DataRow(PresentationOperation.MarketBuySell)]
        [DataRow(PresentationOperation.MarketChangeTradeType)]
        [DataRow(PresentationOperation.ExtendedFeaturesAction)]
        public void ReplacementRunsAfterAllPreWithFinalParameterAndLaterVetoSuppressesEveryEffect(PresentationOperation operation)
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
                events.TryRegister("d", "cannot-clear-veto", a => a.SkipOriginalFunction = false, null, out _);
                PresentationEvents.Execute(events, new PresentationPreEventArgs(operation, null, "initial"), p => originals++);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(veto ? 0 : 1, replacements);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(0, originals);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(veto, completed.WasSkipped);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(!veto, completed.WasReplaced);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsFalse(completed.OriginalCompleted);
            }
        }

        [TestMethod]
        [DataRow(PresentationOperation.RechargeSiegeAmmo)]
        [DataRow(PresentationOperation.MarketSelectGoods)]
        [DataRow(PresentationOperation.MarketCycleGoods)]
        [DataRow(PresentationOperation.MarketBuySell)]
        [DataRow(PresentationOperation.MarketChangeTradeType)]
        [DataRow(PresentationOperation.ExtendedFeaturesAction)]
        public void ReplacementFailureStillCleansUpPostAndCannotRetryVanilla(PresentationOperation operation)
        {
            int originals = 0;
            var error = new InvalidOperationException("replacement");
            PresentationPostEventArgs completed = null;
            var events = new InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs>();
            events.TryRegister("a", "replacement", a => a.Replacement = p => throw error, a => completed = a, out _);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.ThrowsExactly<InvalidOperationException>(() => PresentationEvents.Execute(events,
                new PresentationPreEventArgs(operation, null, null), p => originals++));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(0, originals);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreSame(error, completed.CompletionException);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNull(completed.OriginalException);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsTrue(completed.WasReplaced);
        }

        [TestMethod]
        [DataRow(PresentationOperation.MarketSelectGoods)]
        [DataRow(PresentationOperation.MarketCycleGoods)]
        [DataRow(PresentationOperation.MarketBuySell)]
        [DataRow(PresentationOperation.MarketChangeTradeType)]
        [DataRow(PresentationOperation.ExtendedFeaturesAction)]
        public void MarketPresentationPreservesReceiverFinalParameterAndOwnerPrivateState(PresentationOperation operation)
        {
            // No game constructors or global hooks: execute the same shared dispatch with an inert receiver.
            var receiver = (CrusaderDE.MainViewModel)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(CrusaderDE.MainViewModel));
            var initial = new object();
            var final = new object();
            var firstState = new object();
            var secondState = new object();
            object stateSeenBySecondPre = firstState;
            CrusaderDE.MainViewModel originalReceiver = null;
            object originalParameter = null;
            PresentationPostEventArgs firstPost = null, secondPost = null;
            var events = new InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs>();
            TestAssert.IsTrue(events.TryRegister("a", "owned-state", a => {
                a.Parameter = final; a.State = firstState;
            }, a => firstPost = a, out _));
            TestAssert.IsTrue(events.TryRegister("b", "owned-state", a => {
                stateSeenBySecondPre = a.State; a.State = secondState;
            }, a => secondPost = a, out _));
            var pre = new PresentationPreEventArgs(operation, receiver, initial);
            PresentationEvents.Execute(events, pre, receiver, (self, parameter) => {
                originalReceiver = self; originalParameter = parameter;
            });

            TestAssert.AreSame(initial, pre.OriginalParameter);
            TestAssert.AreSame(receiver, pre.Instance);
            TestAssert.AreSame(receiver, pre.ViewModel);
            TestAssert.AreSame(receiver, originalReceiver);
            TestAssert.AreSame(final, originalParameter);
            TestAssert.IsNull(stateSeenBySecondPre);
            TestAssert.AreSame(firstState, firstPost.State);
            TestAssert.AreSame(secondState, secondPost.State);
            TestAssert.AreEqual(operation, firstPost.Operation);
            TestAssert.AreSame(receiver, firstPost.Instance);
            TestAssert.AreSame(receiver, firstPost.ViewModel);
            TestAssert.AreSame(final, firstPost.Parameter);
            TestAssert.IsTrue(firstPost.OriginalCompleted);
            TestAssert.IsFalse(firstPost.WasSkipped);
            TestAssert.IsFalse(firstPost.WasReplaced);
            TestAssert.IsNull(firstPost.CompletionException);
            TestAssert.AreEqual(0L, events.CallbackFailures);
        }

        [TestMethod]
        public void InstalledManagedTargetsAndFullRecruitmentMaterialIlContractAreValid()
        {
            MethodInfo gui = typeof(FatControler).GetMethod("NoesisGUIUpdateChecksInGame", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(gui);
            RecruitmentMaterialUiIlContract.Validate(gui);
            foreach (string name in new[] { "ButtonEnterCreateTroop", "ButtonLeaveCreateTroop", "ButtonTroopPanelMouseEnter", "ButtonTroopPanelMouseLeave", "ButtonUnitRechargeRock",
                "ButtonNewTradeGoodsType", "ButtonCycleTradeGoodsType", "ButtonBuySell", "ButtonNewTradeType", "ButtonExtendedFeaturesFunction" })
            {
                var method = typeof(CrusaderDE.MainViewModel).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(object) }, null);
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(method, name); Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(typeof(void), method.ReturnType);
            }
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(typeof(EngineInterface).GetMethod("GameAction", new[] { typeof(Enums.GameActionCommand), typeof(int), typeof(int), typeof(int) }));
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(typeof(CrusaderDE.HUD_Main).GetMethod("UpdateRollover", Type.EmptyTypes));
        }
    }
}
