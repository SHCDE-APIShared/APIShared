using System;
using System.Collections.Generic;
using APIShared.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class UnitVoiceSpeechClipLeaseTests
    {
        private sealed class Source { internal bool Playing, Destroyed; }
        [TestMethod]
        public void LateOriginalLoadOverwritingTheVanillaClipFieldCannotOrphanTheOwnClip()
        {
            var manager = new object(); var source = new Source { Playing = true }; var released = new List<object>();
            var leases = new UnitVoiceClipLeases<object, Source, object>(value => !value.Playing, released.Add);
            object ownB = new object(), vanillaField = ownB;
            leases.Created(ownB); leases.Played(ownB, source, manager);
            vanillaField = new object(); // Earlier asynchronous A completes after B and replaces speechClip1.
            Assert.AreNotSame(ownB, vanillaField);
            leases.Sweep(manager, false); Assert.AreEqual(0, released.Count, "B is still audible and must survive the field overwrite.");
            source.Playing = false; leases.Sweep(manager, false);
            CollectionAssert.AreEqual(new[] { ownB }, released); Assert.AreEqual(0, leases.OwnedCount);
            leases.Release(ownB); leases.Stopped(source); leases.Sweep(manager, false); Assert.AreEqual(1, released.Count);
        }

        [TestMethod]
        public void PauseAndOtherManagersCannotRetirePlayingLeasesOrTheNewUnplayedClip()
        {
            var manager = new object(); var other = new object(); var source = new Source { Playing = false };
            var released = new List<object>();
            var leases = new UnitVoiceClipLeases<object, Source, object>(value => !value.Playing, released.Add);
            var played = new object(); var unplayed = new object();
            leases.Created(played); leases.Played(played, source, manager); leases.Created(unplayed);
            leases.Sweep(other, false); leases.Sweep(manager, true); Assert.AreEqual(0, released.Count);
            leases.Stopped(source); CollectionAssert.AreEqual(new[] { played }, released);
            Assert.AreEqual(1, leases.OwnedCount); // C has been created before the forced stop, but has not played.
            leases.ReleaseUnplayed(unplayed); Assert.AreEqual(2, released.Count); Assert.AreEqual(0, leases.OwnedCount);
        }

        [TestMethod]
        public void AThrowingOriginalStopIsNotRetriedAndDoesNotReleaseItsPlayingClip()
        {
            var manager = new object(); var source = new Source { Playing = true }; var released = new List<object>(); int stops = 0;
            var leases = new UnitVoiceClipLeases<object, Source, object>(value => !value.Playing, released.Add);
            var clip = new object(); leases.Created(clip); leases.Played(clip, source, manager);
            Assert.ThrowsExactly<InvalidOperationException>(() => leases.Stop(source, value => { stops++; throw new InvalidOperationException(); }));
            Assert.AreEqual(1, stops); Assert.AreEqual(0, released.Count); Assert.AreEqual(1, leases.OwnedCount);
            leases.Stop(source, value => { stops++; value.Playing = false; });
            Assert.AreEqual(2, stops); CollectionAssert.AreEqual(new[] { clip }, released);
        }

        [TestMethod]
        public void FailedPlaybackReleasesOnlyItsUnplayedClipAndUnloadIsIdempotent()
        {
            var manager = new object(); var source = new Source { Playing = true }; var released = new List<object>();
            var leases = new UnitVoiceClipLeases<object, Source, object>(value => !value.Playing, released.Add);
            var active = new object(); var failed = new object();
            leases.Created(active); leases.Played(active, source, manager); leases.Created(failed);
            leases.ReleaseUnplayed(active); Assert.AreEqual(0, released.Count);
            leases.ReleaseUnplayed(failed); CollectionAssert.AreEqual(new[] { failed }, released);
            leases.Release(active); leases.Stop(source, value => value.Playing = false); leases.Sweep(manager, false);
            Assert.AreEqual(2, released.Count); Assert.AreEqual(0, leases.OwnedCount);
        }

        [TestMethod]
        public void StopAllRetiresPausedOrphansEvenWhenVanillaNeverCallsSourceStop()
        {
            var manager = new object(); var other = new object(); var source = new Source { Playing = false }; var released = new List<object>(); int stops = 0;
            var leases = new UnitVoiceClipLeases<object, Source, object>(value => !value.Playing, released.Add);
            var orphan = new object(); var unplayed = new object();
            leases.Created(orphan); leases.Played(orphan, source, manager); leases.Created(unplayed);
            leases.Sweep(manager, true); Assert.AreEqual(0, released.Count);
            leases.StopManager(other, value => { stops++; value.Playing = false; }, error => Assert.Fail(error.Message)); Assert.AreEqual(0, stops);
            leases.StopManager(manager, value => { stops++; value.Playing = false; }, error => Assert.Fail(error.Message));
            Assert.AreEqual(1, stops); CollectionAssert.AreEqual(new[] { orphan }, released); Assert.AreEqual(1, leases.OwnedCount);
            leases.ReleaseUnplayed(unplayed);
        }

        [TestMethod]
        public void StopAllDoesNotRetryAFailedOriginalStopButCanRetireADestroyedSource()
        {
            var manager = new object(); var source = new Source { Playing = true }; var released = new List<object>(); int stops = 0;
            var leases = new UnitVoiceClipLeases<object, Source, object>(value => !value.Playing, released.Add, value => value.Destroyed);
            var clip = new object(); leases.Created(clip); leases.Played(clip, source, manager);
            Assert.ThrowsExactly<InvalidOperationException>(() => leases.Stop(source, value => { stops++; throw new InvalidOperationException(); }));
            leases.StopManager(manager, value => { stops++; value.Playing = false; }, error => Assert.Fail(error.Message));
            Assert.AreEqual(1, stops); Assert.AreEqual(0, released.Count);
            source.Destroyed = true;
            leases.StopManager(manager, value => { if (!value.Destroyed) { stops++; value.Playing = false; } }, error => Assert.Fail(error.Message));
            Assert.AreEqual(1, stops); CollectionAssert.AreEqual(new[] { clip }, released);
        }
    }
}
