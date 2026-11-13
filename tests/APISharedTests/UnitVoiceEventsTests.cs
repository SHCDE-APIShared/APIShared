using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIShared;
using APIShared.Audio;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class UnitVoiceEventsTests
    {
        private readonly List<UnitVoiceRegistration> handles = new List<UnitVoiceRegistration>();
        private static UnitVoiceCue Cue => UnitVoiceCatalog.Cues.First(cue => UnitVoiceCatalog.SpeechFolder(cue) != null);
        private static UnitVoiceContext Context(int? requester = 1, int? owner = 1,
            UnitVoiceSituation situation = UnitVoiceSituation.Selection, string source = "Troop.Dispatcher") =>
            new UnitVoiceContext(new UnitVoiceSource(requester, owner, (int)eChimps.CHIMP_TYPE_ARCHER,
                Cue.VoiceBankKey, Cue.Key, situation, UnitVoiceEvidence.NativeAudited, 7, source),
                "fx\\speech\\" + Cue.FileName, 11, 1, 100f, false, true, "de-DE", "EnglishSpeech",
                new MissionContext(99, MissionStartKind.EditorCreated, default, null, "test", false));
        // Vanilla drops intents whose file has no managed speech route; the context reports that silence.
        [TestMethod]
        public void TheContextReportsWhetherVanillaWouldBeSilent()
        {
            Assert.IsFalse(Context().OriginalIsSilent);
            var silent = UnitVoiceCatalog.Cues.First(cue => UnitVoiceCatalog.SpeechFolder(cue) == null);
            var context = new UnitVoiceContext(new UnitVoiceSource(1, 1, (int)eChimps.CHIMP_TYPE_KNIGHT, silent.VoiceBankKey, silent.Key,
                UnitVoiceSituation.AttackStrong, UnitVoiceEvidence.Inferred, 7, "Troop.Dispatcher"), "fx\\speech\\" + silent.FileName, 12, 1, 100f);
            Assert.IsTrue(context.OriginalIsSilent);
            Assert.AreEqual("troops", UnitVoiceCatalog.BankSpeechFolder(silent), "A replacement plays through its bank's folder.");
        }

        private UnitVoiceRegistration Register(InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs> registry,
            string owner, Action<UnitVoicePreEventArgs> pre, Action<UnitVoicePostEventArgs> post,
            int order = 0, UnitVoiceSelection sources = null, string id = "rule")
        {
            Assert.IsTrue(UnitVoiceEvents.Register(owner, id, pre, post, out var handle, out string reason, order, sources, registry), reason);
            handles.Add(handle); return handle;
        }
        [TestCleanup]
        public void DisableOwnRegistrations()
        { foreach (var handle in handles) handle.SetEnabled(false); }

        [TestMethod]
        public void SourceSelectionsAndDimensionsUseAndWhileEachDimensionUsesOr()
        {
            var cueKeys = new List<string> { Cue.Key };
            Assert.IsTrue(UnitVoiceSelection.TryCreate(out var selection, out string reason,
                sourceKeys: new[] { "Troop.Dispatcher", "Civilian.Selection" }, cueKeys: cueKeys,
                voiceBankKeys: new[] { Cue.VoiceBankKey }, situations: new[] { UnitVoiceSituation.Selection, UnitVoiceSituation.Recruitment },
                requestingPlayerIds: new[] { 1, 2 }, ownerPlayerIds: new[] { 3 },
                requestedUnitTypes: new[] { (int)eChimps.CHIMP_TYPE_ARCHER }), reason);
            cueKeys.Clear();
            Assert.IsTrue(selection.Matches(Context(2, 3, UnitVoiceSituation.Recruitment).Source));
            Assert.IsTrue(selection.Matches(Context(1, 3, source: "Civilian.Selection").Source));
            Assert.IsFalse(selection.Matches(Context(1, 3, source: "Engineer.SpecialActions").Source));
            Assert.IsFalse(selection.Matches(Context(1, 2).Source));
            Assert.IsFalse(selection.Matches(Context(null, 3).Source));
            Assert.IsFalse(selection.Matches(Context(1, null).Source));
            Assert.IsFalse(selection.Matches(Context(1, 3, UnitVoiceSituation.Move).Source));
            Assert.AreEqual(2, selection.SourceKeys.Count);
        }

        [TestMethod]
        public void SelectionRejectsUnknownAndEmptyDimensionsWithoutInventingNullableFacts()
        {
            Assert.IsFalse(UnitVoiceSelection.TryCreate(out _, out _, sourceKeys: new[] { "not-a-source" }));
            Assert.IsFalse(UnitVoiceSelection.TryCreate(out _, out _, cueKeys: new[] { "not-a-cue" }));
            Assert.IsFalse(UnitVoiceSelection.TryCreate(out _, out _, voiceBankKeys: new[] { "not-a-bank" }));
            Assert.IsFalse(UnitVoiceSelection.TryCreate(out _, out _, sourceKeys: Array.Empty<string>()));
            Assert.IsFalse(UnitVoiceSelection.TryCreate(out _, out _, ownerPlayerIds: new[] { 0 }));
            Assert.IsFalse(UnitVoiceSelection.TryCreate(out _, out _, requestingPlayerIds: new[] { 9 }));
            Assert.IsFalse(UnitVoiceSelection.TryCreate(out _, out _, requestedUnitTypes: new[] { (int)eChimps.CHIMP_NUM_TYPES }));
            Assert.IsFalse(UnitVoiceSelection.TryCreate(out _, out _, situations: new[] { (UnitVoiceSituation)int.MaxValue }));
            Assert.IsTrue(UnitVoiceSelection.TryCreate(out var unrestricted, out _));
            Assert.AreSame(UnitVoiceSelection.All, unrestricted);
            Assert.IsTrue(unrestricted.Matches(default));
        }

        [TestMethod]
        public void FilteringOccursPerInvocationAndFreezesMatchingPrePostPairs()
        {
            var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
            Assert.IsTrue(UnitVoiceSelection.TryCreate(out var selection, out _, requestingPlayerIds: new[] { 2 }));
            int pres = 0, posts = 0, plays = 0;
            UnitVoiceRegistration selected = null;
            selected = Register(registry, "selected", pre => { pres++; pre.State = "own"; selected.SetEnabled(false); },
                post => { posts++; Assert.AreEqual("own", post.State); }, sources: selection);
            UnitVoiceEvents.Dispatch(Context(1), _ => plays++, registry);
            Assert.AreEqual(0, pres); Assert.AreEqual(0, posts);
            UnitVoiceEvents.Dispatch(Context(2), _ => plays++, registry);
            Assert.AreEqual(1, pres); Assert.AreEqual(1, posts);
            selected.SetEnabled(true);
            UnitVoiceEvents.Dispatch(Context(1), _ => plays++, registry);
            Assert.AreEqual(1, pres); Assert.AreEqual(1, posts); Assert.AreEqual(3, plays);
        }

        [TestMethod]
        public void OrderedFailureRollsBackTargetGainVetoAndPrivateState()
        {
            var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
            var order = new List<string>();
            Register(registry, "a", pre => { order.Add("a"); pre.TryReplaceVanillaCue(Cue); pre.MultiplyVolume(0.5f); pre.State = "a-private"; },
                post => Assert.AreEqual("a-private", post.State));
            Register(registry, "b", pre => {
                order.Add("b"); pre.MultiplyVolume(0f); pre.SkipOriginalFunction = true; pre.State = "bad";
                throw new InvalidOperationException("consumer failure");
            }, post => Assert.IsNull(post.State));
            Register(registry, "c", pre => {
                order.Add("c"); Assert.IsNull(pre.State); Assert.AreEqual(0.5f, pre.VolumeFactor);
                Assert.AreSame(Cue, pre.Target.Cue); Assert.IsFalse(pre.SkipOriginalFunction); pre.MultiplyVolume(4f);
            }, post => { Assert.AreEqual(2f, post.VolumeFactor); Assert.IsTrue(post.WasSubmitted); });
            UnitVoiceEvents.Dispatch(Context(), pre => {
                Assert.AreEqual(2f, pre.VolumeFactor); Assert.AreSame(Cue, pre.Target.Cue);
            }, registry);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, order);
            Assert.AreEqual(1L, registry.CallbackFailures);
        }

        [TestMethod]
        public void SuccessfulVetoIsStickyAndPostOnlyObserverStillReceivesSuppression()
        {
            var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
            int posts = 0;
            Register(registry, "first", pre => pre.SkipOriginalFunction = true, null);
            Register(registry, "second", pre => pre.SkipOriginalFunction = false, null);
            Register(registry, "observer", null, post => {
                posts++; Assert.IsTrue(post.WasSuppressed); Assert.IsFalse(post.WasSubmitted); Assert.IsNull(post.State);
            });
            UnitVoiceEvents.Dispatch(Context(), _ => Assert.Fail("Veto must prevent playback."), registry);
            Assert.AreEqual(1, posts);
        }

        [TestMethod]
        public void UseOriginalResetsOnlyTheTargetAfterAnEarlierReplacement()
        {
            var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
            Register(registry, "first", pre => {
                Assert.IsTrue(pre.TryReplaceVanillaCue(Cue)); pre.MultiplyVolume(0.25f); pre.SkipOriginalFunction = true;
            }, null);
            Register(registry, "reset", pre => {
                pre.State = "private-reset"; pre.UseOriginal();
                Assert.AreEqual(UnitVoiceTargetKind.Original, pre.Target.Kind);
                Assert.AreEqual(0.25f, pre.VolumeFactor); Assert.IsTrue(pre.SkipOriginalFunction);
                Assert.AreEqual("private-reset", pre.State);
            }, post => {
                Assert.AreEqual(UnitVoiceTargetKind.Original, post.Target.Kind); Assert.AreEqual(0.25f, post.VolumeFactor);
                Assert.IsTrue(post.WasSuppressed); Assert.AreEqual("private-reset", post.State);
            });
            UnitVoiceEvents.Dispatch(Context(), _ => Assert.Fail("Reset must retain the earlier veto."), registry);
        }

        [TestMethod]
        public void ThrowingResetRollsBackToTheEarlierTargetAndGain()
        {
            var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
            Register(registry, "first", pre => {
                Assert.IsTrue(pre.TryReplaceVanillaCue(Cue)); pre.MultiplyVolume(0.25f); pre.SkipOriginalFunction = true;
            }, null);
            Register(registry, "reset", pre => { pre.UseOriginal(); throw new InvalidOperationException("failed reset rule"); },
                post => {
                    Assert.AreEqual(UnitVoiceTargetKind.Vanilla, post.Target.Kind); Assert.AreSame(Cue, post.Target.Cue);
                    Assert.AreEqual(0.25f, post.VolumeFactor); Assert.IsTrue(post.WasSuppressed); Assert.IsNull(post.State);
                });
            UnitVoiceEvents.Dispatch(Context(), _ => Assert.Fail("Earlier veto must survive rollback."), registry);
            Assert.AreEqual(1L, registry.CallbackFailures);
        }

        [TestMethod]
        public void PublicRegistrationOverloadsAcceptOrderAndExplicitSelectionWithoutAmbiguity()
        {
            string owner = "UnitVoiceEventsTests." + Guid.NewGuid().ToString("N");
            Assert.IsTrue(UnitVoiceEvents.TryRegister(owner, "all", _ => { }, null, out var all, out string reason, 12), reason);
            handles.Add(all); Assert.AreSame(UnitVoiceSelection.All, all.Sources);
            Assert.IsTrue(UnitVoiceSelection.TryCreate(out var sources, out reason, requestingPlayerIds: new[] { 2 }), reason);
            Assert.IsTrue(UnitVoiceEvents.TryRegister(owner, "selected", sources, _ => { }, null,
                out var selected, out reason, order: 4), reason);
            handles.Add(selected); Assert.AreSame(sources, selected.Sources);
            Assert.IsFalse(UnitVoiceEvents.TryRegister(owner, "null", (UnitVoiceSelection)null, _ => { }, null, out _, out _));
        }

        [TestMethod]
        public void CatalogDescriptionsAndBankGroupsCoverDiscoveryWithoutRuntimeAccess()
        {
            CollectionAssert.AreEquivalent(UnitVoiceCatalog.SourceKeys.ToArray(), UnitVoiceCatalog.Sources.Select(value => value.Key).ToArray());
            foreach (var source in UnitVoiceCatalog.Sources)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(source.Description));
                Assert.IsTrue(source.Evidence == UnitVoiceEvidence.NativeAudited || source.Evidence == UnitVoiceEvidence.InGameConfirmed);
            }
            CollectionAssert.AreEquivalent(UnitVoiceCatalog.Cues.ToArray(), UnitVoiceCatalog.VoiceBanks.SelectMany(value => value.Cues).ToArray());
            foreach (var bank in UnitVoiceCatalog.VoiceBanks)
            {
                Assert.IsTrue(bank.Cues.Count > 0);
                Assert.IsTrue(bank.Cues.All(cue => cue.VoiceBankKey == bank.Key));
                Assert.IsTrue(((ICollection<UnitVoiceCue>)bank.Cues).IsReadOnly);
            }
        }

        [TestMethod]
        public void RecursivePreAndPostPlaybackBypassNotificationsAndKeepNeutralGain()
        {
            var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
            int pres = 0, posts = 0, nested = 0;
            Action<UnitVoicePreEventArgs> nestedPlayback = args => {
                nested++; Assert.AreEqual(1f, args.VolumeFactor); Assert.AreEqual(UnitVoiceTargetKind.Original, args.Target.Kind);
                Assert.ThrowsExactly<InvalidOperationException>(() => args.MultiplyVolume(2));
            };
            Register(registry, "recursive", pre => {
                pres++; pre.MultiplyVolume(0.25f); UnitVoiceEvents.Dispatch(Context(2), nestedPlayback, registry);
            }, post => { posts++; UnitVoiceEvents.Dispatch(Context(3), nestedPlayback, registry); });
            UnitVoiceEvents.Dispatch(Context(), args => Assert.AreEqual(0.25f, args.VolumeFactor), registry);
            Assert.AreEqual(1, pres); Assert.AreEqual(1, posts); Assert.AreEqual(2, nested);
        }

        [TestMethod]
        public void InvalidGainRollsBackWhileZeroAndGainAboveOneRemainValid()
        {
            foreach (float invalid in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
                Register(registry, "invalid", pre => { pre.MultiplyVolume(2f); pre.MultiplyVolume(invalid); }, null);
                UnitVoiceEvents.Dispatch(Context(), pre => Assert.AreEqual(1f, pre.VolumeFactor), registry);
                Assert.AreEqual(1L, registry.CallbackFailures);
            }
            var good = new UnitVoicePreEventArgs(Context());
            good.MultiplyVolume(float.MaxValue);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => good.MultiplyVolume(2f));
            Assert.AreEqual(float.MaxValue, good.VolumeFactor);
            good.MultiplyVolume(0f); Assert.AreEqual(0f, good.VolumeFactor);
        }

        [TestMethod]
        public void PlaybackFailureHasNoPostAndMetadataSurvivesFallback()
        {
            var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
            int posts = 0;
            Register(registry, "rule", pre => Assert.IsTrue(pre.TryReplaceVanillaCue(Cue)), post => {
                posts++; Assert.AreEqual(UnitVoiceTargetKind.Original, post.Target.Kind);
                Assert.AreEqual("resource lost", post.FallbackReason); Assert.IsFalse(post.WasSubmitted);
                Assert.AreEqual("failed to submit", post.Diagnostic);
                Assert.AreEqual("de-DE", post.Context.UiLocale); Assert.AreEqual("EnglishSpeech", post.Context.SpeechAssetBranch);
                Assert.AreEqual(MissionStartKind.EditorCreated, post.Context.MissionContext.StartKind);
                Assert.AreEqual(99L, post.Context.MissionContext.SessionId); Assert.AreEqual(7L, post.Context.Source.SessionId);
            });
            Assert.ThrowsExactly<ApplicationException>(() => UnitVoiceEvents.Dispatch(Context(), _ => throw new ApplicationException(), registry));
            Assert.AreEqual(0, posts);
            UnitVoiceEvents.Dispatch(Context(), args => {
                args.FallBackToOriginal("resource lost"); args.SetPlaybackOutcome(false, "failed to submit");
            }, registry);
            Assert.AreEqual(1, posts);
        }

        [TestMethod]
        public void FrozenAndForeignThreadMutationsFailWithoutChangingTheRequest()
        {
            var args = new UnitVoicePreEventArgs(Context());
            Task.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => args.MultiplyVolume(2))).GetAwaiter().GetResult();
            Assert.AreEqual(1f, args.VolumeFactor);
            args.Freeze();
            Assert.ThrowsExactly<InvalidOperationException>(() => args.UseOriginal());
            Assert.ThrowsExactly<InvalidOperationException>(() => args.TryReplaceVanillaCue(Cue));
            Assert.ThrowsExactly<InvalidOperationException>(() => args.TryReplaceResource(null));
            Assert.ThrowsExactly<InvalidOperationException>(() => args.State = "late");
            Assert.ThrowsExactly<InvalidOperationException>(() => args.SkipOriginalFunction = true);
        }

        [TestMethod]
        public void NativeIntentCueWithoutManagedSpeechRouteCannotBecomeReplacement()
        {
            var args = new UnitVoicePreEventArgs(Context());
            Assert.IsTrue(args.TryReplaceVanillaCue(Cue));
            var target = args.Target;
            foreach (var unmapped in UnitVoiceCatalog.Cues.Where(cue => UnitVoiceCatalog.SpeechFolder(cue) == null))
            {
                Assert.IsFalse(args.TryReplaceVanillaCue(unmapped)); Assert.AreSame(target, args.Target);
            }
        }

        [TestMethod]
        public void AnyThreadRegistrationActivationAndDuplicateIdentityRespectDemand()
        {
            var registry = new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>();
            UnitVoiceRegistration handle = null;
            Task.Run(() => {
                Assert.IsTrue(UnitVoiceEvents.Register("early", "rule", _ => { }, null, out handle, out string reason, registry: registry), reason);
            }).GetAwaiter().GetResult();
            handles.Add(handle);
            Assert.IsTrue(handle.Enabled); Assert.IsTrue(UnitVoiceEvents.Demand);
            Assert.IsFalse(UnitVoiceEvents.Register("early", "rule", _ => { }, null, out _, out _, registry: registry));
            Task.Run(() => { handle.SetEnabled(false); handle.SetEnabled(false); handle.SetEnabled(true); }).GetAwaiter().GetResult();
            Assert.IsTrue(handle.Enabled); Assert.IsTrue(UnitVoiceEvents.Demand);
            handle.SetEnabled(false); Assert.IsFalse(handle.Enabled);
        }

        [TestMethod]
        public void ResourceGainOverflowAndInvalidatedMapHandleLeaveSelectionsUnchanged()
        {
            var store = new UnitVoiceResourceStore();
            Assert.IsTrue(store.TryPrepare("owner", "large", "a.wav", false, float.MaxValue,
                () => new UnitVoicePcmData(1, 1, 44100, new[] { 0f }), action => action(), null,
                out var huge, out string reason), reason);
            var args = new UnitVoicePreEventArgs(Context());
            args.MultiplyVolume(2f);
            Assert.IsFalse(args.TryReplaceResource(huge));
            Assert.AreEqual(UnitVoiceTargetKind.Original, args.Target.Kind);
            var small = new UnitVoicePreEventArgs(Context());
            Assert.IsTrue(small.TryReplaceResource(huge));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => small.MultiplyVolume(2f));
            Assert.AreEqual(1f, small.VolumeFactor); Assert.AreSame(huge, small.Target.Resource);
            Assert.IsTrue(store.TryPrepare("owner", "map", "map.wav", true, 1f,
                () => new UnitVoicePcmData(1, 1, 44100, new[] { 0f }), action => action(), null,
                out var map, out reason), reason);
            store.InvalidateMapResources();
            Assert.IsFalse(map.IsReady);
            Assert.IsFalse(small.TryReplaceResource(map)); Assert.AreSame(huge, small.Target.Resource);
        }
    }
}
