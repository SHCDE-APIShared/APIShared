using System;
using System.Collections.Generic;
using System.IO;
using APIShared;
using APIShared.Savegames;
using BepInEx.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public class SaveLoadDialogTests
    {
        [TestMethod]
        public void ClosingDuringPreparationCleansUpBeforeClosedObservers()
        {
            var calls = new List<string>();
            SaveLoadDialogRequest request = null;
            request = Create(new SaveLoadDialogCallbacks
            {
                Prepare = _ => { calls.Add("prepare"); request.Close(SaveLoadDialogOutcome.Replaced); },
                AttemptFinished = _ => calls.Add("cleanup"),
                Closed = (_, outcome) => { Assert.AreEqual(SaveLoadDialogOutcome.Replaced, outcome); calls.Add("closed"); }
            });
            Assert.IsFalse(request.Confirm("a", null, () => true, () => Assert.Fail("replaced original"), () => calls.Add("rollback")));
            CollectionAssert.AreEqual(new[] { "prepare", "cleanup", "rollback", "closed" }, calls);
        }

        [TestMethod]
        public void AcceptedAndThrowingOriginalsNotifyOnlyAfterAttemptCleanup()
        {
            foreach (bool fail in new[] { false, true })
            {
                var calls = new List<string>();
                var request = Create(new SaveLoadDialogCallbacks
                {
                    AttemptFinished = _ => calls.Add("cleanup"),
                    Closed = (_, outcome) => calls.Add("closed:" + outcome)
                });
                Action confirm = () => request.Confirm("a", null, () => true,
                    () => { calls.Add("original"); if (fail) throw new InvalidOperationException(); },
                    () => calls.Add("rollback"));
                if (fail) Assert.ThrowsExactly<InvalidOperationException>(confirm); else confirm();
                CollectionAssert.AreEqual(fail ? new[] { "original", "cleanup", "rollback", "closed:Failed" }
                    : new[] { "original", "cleanup", "closed:Confirmed" }, calls);
            }
        }

        [TestMethod]
        public void ReplacementOpensWaitForCleanupAndVanillaBookkeeping()
        {
            var calls = new List<string>();
            var dispatch = new SaveLoadDialogDispatch(error => Assert.Fail(error.ToString()));
            dispatch.Run(() =>
            {
                dispatch.Open(() => { calls.Add("replacement"); dispatch.Open(() => calls.Add("nested")); calls.Add("opened"); });
                dispatch.Run(() => { calls.Add("cleanup"); dispatch.Open(() => calls.Add("observer-open")); });
                calls.Add("vanilla-bookkeeping");
            });
            CollectionAssert.AreEqual(new[] { "cleanup", "vanilla-bookkeeping", "replacement", "opened", "observer-open", "nested" }, calls);
        }

        [TestMethod]
        public void DeferredReplacementImmediatelyInvalidatesAnUnacceptedRequest()
        {
            var calls = new List<string>();
            var dispatch = new SaveLoadDialogDispatch(error => Assert.Fail(error.ToString()));
            SaveLoadDialogRequest request = null;
            request = Create(new SaveLoadDialogCallbacks
            {
                Prepare = _ => dispatch.Open(() => calls.Add("replacement"), () => request.Close(SaveLoadDialogOutcome.Replaced)),
                AttemptFinished = _ => calls.Add("cleanup"),
                Closed = (_, outcome) => { Assert.AreEqual(SaveLoadDialogOutcome.Replaced, outcome); calls.Add("closed"); }
            });
            dispatch.Run(() =>
            {
                Assert.IsFalse(request.Confirm("old", null, () => true, () => Assert.Fail("replaced action executed"), () => calls.Add("rollback")));
                calls.Add("bookkeeping");
            });
            CollectionAssert.AreEqual(new[] { "cleanup", "rollback", "closed", "bookkeeping", "replacement" }, calls);
        }

        [TestMethod]
        public void DeferredOpenErrorsDoNotMaskOriginalExceptionsOrSkipLaterOpens()
        {
            int errors = 0, opened = 0;
            var dispatch = new SaveLoadDialogDispatch(_ => errors++);
            var expected = new InvalidOperationException("original");
            var actual = Assert.ThrowsExactly<InvalidOperationException>(() => dispatch.Run(() =>
            {
                dispatch.Open(() => throw new Exception("replacement"));
                dispatch.Open(() => opened++);
                throw expected;
            }));
            Assert.AreSame(expected, actual); Assert.AreEqual(1, errors); Assert.AreEqual(1, opened);
        }

        private static SaveLoadDialogRequest Create(params SaveLoadDialogCallbacks[] callbacks)
        {
            var entries = new List<SaveLoadDialogRequest.Entry>();
            foreach (var callback in callbacks) entries.Add(new SaveLoadDialogRequest.Entry(callback));
            return new SaveLoadDialogRequest(7, Enums.RequesterTypes.LoadSinglePlayerGame, entries, _ => { });
        }

        [TestMethod]
        public void GatesRunBeforePreparationAndRejectedRequestsCanBeRetried()
        {
            var calls = new List<string>();
            bool ready = false;
            var request = Create(new SaveLoadDialogCallbacks { Prepare = _ => calls.Add("prepare") },
                new SaveLoadDialogCallbacks { Validate = _ => ready });
            Assert.IsFalse(request.Confirm("a", null, () => true, () => calls.Add("original"), () => calls.Add("rollback")));
            CollectionAssert.AreEqual(new[] { "rollback" }, calls);
            Assert.IsFalse(request.IsClosed);
            ready = true;
            Assert.IsTrue(request.Confirm("b", null, () => true, () => calls.Add("original"), () => calls.Add("rollback")));
            CollectionAssert.AreEqual(new[] { "rollback", "prepare", "original" }, calls);
        }

        [TestMethod]
        public void ThrowingPreparationCleansUpItselfAndEarlierParticipantsInReverseOrder()
        {
            var calls = new List<string>();
            var request = Create(new SaveLoadDialogCallbacks { Prepare = _ => calls.Add("a"), AttemptFinished = _ => calls.Add("undo-a") },
                new SaveLoadDialogCallbacks { Prepare = _ => { calls.Add("b"); throw new Exception(); }, AttemptFinished = _ => calls.Add("undo-b") });
            Assert.IsFalse(request.Confirm("a", null, () => true, () => Assert.Fail("original ran"), () => calls.Add("rollback")));
            CollectionAssert.AreEqual(new[] { "a", "b", "undo-b", "undo-a", "rollback" }, calls);
        }

        [TestMethod]
        public void OriginalRunsOnceAndReentrantOrLateConfirmationsCannotRepeatIt()
        {
            int count = 0, closed = 0;
            var request = Create(new SaveLoadDialogCallbacks { Closed = (_, outcome) => { Assert.AreEqual(SaveLoadDialogOutcome.Confirmed, outcome); closed++; } });
            Assert.IsTrue(request.Confirm("a", null, () => true, () =>
            {
                count++;
                Assert.IsFalse(request.Confirm("a", null, () => true, () => count++, () => { }));
            }, () => Assert.Fail("accepted request rolled back")));
            Assert.IsFalse(request.Confirm("a", null, () => true, () => count++, () => { }));
            request.Close(SaveLoadDialogOutcome.Cancelled);
            Assert.AreEqual(1, count); Assert.AreEqual(1, closed);
        }

        [TestMethod]
        public void OriginalExceptionHasFailedOutcomeAndRollsBackPreparation()
        {
            int cleanup = 0, rollback = 0;
            SaveLoadDialogOutcome? outcome = null;
            var request = Create(new SaveLoadDialogCallbacks { AttemptFinished = _ => cleanup++, Closed = (_, value) => outcome = value });
            Assert.ThrowsExactly<InvalidOperationException>(() => request.Confirm("a", null, () => true,
                () => throw new InvalidOperationException("original"), () => rollback++));
            Assert.AreEqual(SaveLoadDialogOutcome.Failed, outcome.Value);
            Assert.AreEqual(1, rollback); Assert.AreEqual(1, cleanup);
        }

        [TestMethod]
        public void FailedGateAndFailedOpeningBlockButObserverFailuresDoNotSkipOtherObservers()
        {
            var request = Create(new SaveLoadDialogCallbacks { Opened = _ => throw new Exception() });
            request.Open();
            Assert.IsFalse(request.Confirm("a", null, () => true, () => Assert.Fail("original"), () => { }));
            int observations = 0;
            request = Create(new SaveLoadDialogCallbacks { Closed = (_, __) => throw new Exception() },
                new SaveLoadDialogCallbacks { Closed = (_, __) => observations++ });
            request.Close(SaveLoadDialogOutcome.Replaced);
            request.Close(SaveLoadDialogOutcome.Cancelled);
            Assert.AreEqual(1, observations);
            Assert.IsFalse(request.Confirm("a", null, () => true, () => Assert.Fail("stale request"), () => { }));
        }

        [TestMethod]
        public void ContextStateIsPrivateAndSelectionIsRefreshedForEachAttempt()
        {
            SaveLoadDialogContext first = null, second = null;
            var request = Create(new SaveLoadDialogCallbacks { Opened = context => { first = context; context.State = "mine"; }, Validate = _ => true },
                new SaveLoadDialogCallbacks { Opened = context => second = context, Validate = _ => false });
            request.Open();
            request.Confirm("new-save", null, () => true, () => { }, () => { });
            Assert.AreNotSame(first, second); Assert.IsNull(second.State);
            Assert.AreEqual("mine", first.State); Assert.AreEqual("new-save", second.FileName);
        }

        [TestMethod]
        public void MissingChoiceUsesCurrentButPermissionsAndExplicitFalseArePreserved()
        {
            Assert.IsTrue(SavegameLoadDecision.UseCurrent(SavegameLoadChoiceState.Selectable, null));
            Assert.IsFalse(SavegameLoadDecision.UseCurrent(SavegameLoadChoiceState.Selectable, false));
            Assert.IsTrue(SavegameLoadDecision.UseCurrent(SavegameLoadChoiceState.Selectable, true));
            foreach (bool? choice in new bool?[] { null, false, true })
            {
                Assert.IsFalse(SavegameLoadDecision.UseCurrent(SavegameLoadChoiceState.SavedOnly, choice));
                Assert.IsTrue(SavegameLoadDecision.UseCurrent(SavegameLoadChoiceState.Legacy, choice));
                Assert.IsTrue(SavegameLoadDecision.UseCurrent(SavegameLoadChoiceState.Invalid, choice));
            }
        }

        [TestMethod]
        public void PreferenceSurvivesConfigReloadAndForcedStatesNeverOverwriteIt()
        {
            string folder = Path.Combine(Path.GetTempPath(), "APIShared-dialog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "settings.cfg");
                var preference = new SavegameLoadPreference(new ConfigFile(path, false), error => Assert.Fail(error.ToString()));
                Assert.IsFalse(preference.Value);
                var options = new SavegameLoadOptions(() => preference.Value, preference.Set);
                options.Open(); options.Select(SavegameLoadChoiceState.Selectable);
                Assert.IsFalse(options.UseCurrentSavegameSettings);
                options.UseCurrentSavegameSettings = true;
                options.Select(SavegameLoadChoiceState.SavedOnly);
                Assert.IsFalse(options.UseCurrentSavegameSettings);
                options.Select(SavegameLoadChoiceState.Legacy);
                options.Close(); options.Open(); options.Select(SavegameLoadChoiceState.Selectable);
                Assert.IsTrue(options.UseCurrentSavegameSettings);
                var restarted = new SavegameLoadPreference(new ConfigFile(path, false), error => Assert.Fail(error.ToString()));
                Assert.IsTrue(restarted.Value);
                restarted.Set(false);
                Assert.IsFalse(new SavegameLoadPreference(new ConfigFile(path, false), _ => { }).Value);
                Assert.AreEqual("only by APIShared supported modsettings", options.UseCurrentSavegameSettingsHelp);
            }
            finally { Directory.Delete(folder, true); }
        }

        [TestMethod]
        public void RejectedCorePreparationCleansUpAndRequestReplacementCannotExecuteOriginal()
        {
            int cleanup = 0, rollback = 0;
            var request = Create(new SaveLoadDialogCallbacks { AttemptFinished = _ => cleanup++ });
            Assert.IsFalse(request.Confirm("a", null, () => false, () => Assert.Fail("original ran"), () => rollback++));
            Assert.IsFalse(request.IsClosed);
            Assert.AreEqual(1, cleanup); Assert.AreEqual(1, rollback);
            request.Close(SaveLoadDialogOutcome.Replaced);
            Assert.IsFalse(request.Confirm("a", null, () => true, () => Assert.Fail("replaced original ran"), () => rollback++));
            Assert.AreEqual(1, rollback);
        }

        [TestMethod]
        public void RegistrationCopiesDelegatesAndSupportsAllSaveLoadCategories()
        {
            bool called = false;
            var callbacks = new SaveLoadDialogCallbacks { Validate = _ => false };
            var request = Create(callbacks);
            callbacks.Validate = _ => true;
            Assert.IsFalse(request.Confirm("a", null, () => true, () => called = true, () => { }));
            Assert.IsFalse(called);
            foreach (var type in new[] { Enums.RequesterTypes.LoadSinglePlayerGame, Enums.RequesterTypes.LoadSinglePlayerCoopGame,
                Enums.RequesterTypes.LoadMultiplayerGame, Enums.RequesterTypes.LoadMultiplayerCoopGame })
                Assert.IsTrue(SaveLoadDialogEvents.IsSaveLoad(type));
            Assert.IsFalse(SaveLoadDialogEvents.IsSaveLoad(Enums.RequesterTypes.SaveEditorMap));
        }

        [TestMethod]
        public void PreferenceWriteFailureKeepsTheProcessChoice()
        {
            string folder = Path.Combine(Path.GetTempPath(), "APIShared-dialog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "settings.cfg");
                int failures = 0;
                var preference = new SavegameLoadPreference(new ConfigFile(path, false) { SaveOnConfigSet = false }, _ => failures++);
                // A directory where the config file should be is a deterministic publication failure.
                Directory.CreateDirectory(path);
                preference.Set(true);
                Assert.IsTrue(preference.Value); Assert.AreEqual(1, failures);
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
