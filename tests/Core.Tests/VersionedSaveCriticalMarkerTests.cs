using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Core.Tests;

public partial class VersionedSaveDataTests
{
    [TestMethod]
    public void CriticalMarkerComesFromTheRegistryOutcomeAndWaitsForTheCompleteLoad()
    {
        var extender = new FakeExtender();
        VersionedSavePreparedSource prepared = null;
        VersionedSaveCriticalSource enrollment = null;
        VersionedSavePreparedOrigin origin = null;
        bool entered = false, gotOrigin = false, activeRead = false, activePresent = false;
        bool secondRead = false, secondPresent = false, joinedActive = false, cleanupJoined = true;
        bool foreignRead = true; Exception foreignError = null;
        byte[] activeMarker = null, secondMarker = null;
        int callerId = 0, foreignId = 0;
        Thread thread = null;
        var registration = extender.Registration("Critical.MarkerSource", 3, 1, new[] { "value", "cutover" }, Writes(new()));
        registration.Scope = VersionedSaveScope.SaveGamesAndMaps; registration.SimulationRelevant = true;
        registration.Prepared = load =>
        {
            entered = true; gotOrigin = prepared.TryGetCurrent(load, out origin, out _);
            if (!gotOrigin) return;
            // This is the consumer-owned copy. It must not alter confirmation.
            load.Entries["cutover"][0] = 99;
            activeRead = extender.Registry.TryReadCriticalPreparedMarker(enrollment, origin, out activeMarker, out activePresent, out _);
            if (activeMarker != null && activeMarker.Length != 0) activeMarker[0] = 44;
            secondRead = extender.Registry.TryReadCriticalPreparedMarker(enrollment, origin, out secondMarker, out secondPresent, out _);
            callerId = Environment.CurrentManagedThreadId;
            thread = new Thread(() =>
            {
                foreignId = Environment.CurrentManagedThreadId;
                try { foreignRead = extender.Registry.TryReadCriticalPreparedMarker(enrollment, origin, out _, out _, out _); }
                catch (Exception error) { foreignError = error; }
            }) { IsBackground = true };
            thread.Start(); joinedActive = thread.Join(1500);
        };
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out prepared, out _));
        Assert.IsTrue(extender.Registry.TryEnrollCriticalSource(prepared, "cutover", 3, out enrollment, out _));
        byte[] record = SaveWith(3, 3, new[] { "value", "cutover" }, new Dictionary<string, byte[]>
            { ["value"] = new byte[] { 1 }, ["cutover"] = new byte[] { 5 } });
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "marker.sav"); mod.Load(record);
        try { extender.Registry.PrepareLoad(); }
        finally { if (thread != null && thread.IsAlive) cleanupJoined = thread.Join(500); }
        Assert.IsTrue(entered && gotOrigin && activeRead && activePresent && secondRead && secondPresent);
        CollectionAssert.AreEqual(new byte[] { 44 }, activeMarker);
        CollectionAssert.AreEqual(new byte[] { 5 }, secondMarker, "Each read must clone the private original marker.");
        Assert.IsTrue(joinedActive && cleanupJoined); Assert.IsNull(foreignError);
        Assert.IsTrue(callerId > 0 && foreignId > 0); Assert.AreNotEqual(callerId, foreignId); Assert.IsFalse(foreignRead);
        Assert.IsFalse(extender.Registry.TryReadCriticalPreparedMarker(enrollment, origin, out _, out _, out _),
            "Completed Prepared alone is not the completed Post/load window.");
        extender.Registry.EndLoad();
        Assert.IsTrue(extender.Registry.TryReadCriticalPreparedMarker(enrollment, origin, out var finalMarker, out bool finalPresent, out _));
        Assert.IsTrue(finalPresent); CollectionAssert.AreEqual(new byte[] { 5 }, finalMarker);
        finalMarker[0] = 11;
        Assert.IsTrue(extender.Registry.TryReadCriticalPreparedMarker(enrollment, origin, out var again, out _, out _));
        CollectionAssert.AreEqual(new byte[] { 5 }, again);
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
    }

    [TestMethod]
    public void CriticalMarkerReadIgnoresMigratedMarkersAndRefusesUnreadableOrStaleOrigins()
    {
        var extender = new FakeExtender();
        VersionedSavePreparedSource prepared = null;
        VersionedSaveCriticalSource enrollment = null;
        VersionedSavePreparedOrigin current = null;
        var observed = new List<(VersionedSaveLoadStatus Status, bool Read, bool Present, byte[] Marker)>();
        var registration = extender.Registration("Critical.MarkerStatuses", 3, 1, new[] { "value", "cutover" }, Writes(new()));
        registration.Scope = VersionedSaveScope.SaveGamesAndMaps; registration.SimulationRelevant = true;
        registration.AddMigration(1, entries => entries.Set("cutover", new byte[] { 88 }));
        registration.LegacyReader = (byte[] raw, VersionedSaveEntries entries, out int version) =>
        {
            version = 1; entries.Set("value", new byte[] { 1 });
            entries.Set("cutover", new byte[] { 99 }); return true;
        };
        registration.Prepared = load =>
        {
            // Internal tests deliberately pass the unreadable receiver token too;
            // a private caller cannot bypass the marker reader's status checks.
            current = load.PreparedOrigin;
            bool read = extender.Registry.TryReadCriticalPreparedMarker(enrollment, current, out var marker, out bool present, out _);
            observed.Add((load.Status, read, present, marker));
        };
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out prepared, out _));
        Assert.IsTrue(extender.Registry.TryEnrollCriticalSource(prepared, "cutover", 3, out enrollment, out _));
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "blank.sav");
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.AreEqual(1, observed.Count); Assert.AreEqual(VersionedSaveLoadStatus.NoRecord, observed[0].Status);
        Assert.IsTrue(observed[0].Read); Assert.IsFalse(observed[0].Present); Assert.IsNull(observed[0].Marker);
        var prior = current;
        Assert.IsTrue(extender.Registry.TryReadCriticalPreparedMarker(enrollment, current, out var absent, out bool absentPresent, out _));
        Assert.IsNull(absent); Assert.IsFalse(absentPresent);
        byte[] oldVersion = SaveWith(1, 1, new[] { "value" }, new Dictionary<string, byte[]> { ["value"] = new byte[] { 1 } });
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "migrated.sav"); mod.Load(oldVersion);
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.AreEqual(2, observed.Count); Assert.AreEqual(VersionedSaveLoadStatus.Restored, observed[1].Status);
        Assert.IsTrue(observed[1].Read); Assert.IsFalse(observed[1].Present); Assert.IsNull(observed[1].Marker,
            "A migration-written marker is not evidence that the original stored record had a cutover marker.");
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "legacy.sav"); mod.Load(new byte[] { 1, 2 });
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.AreEqual(3, observed.Count); Assert.AreEqual(VersionedSaveLoadStatus.Restored, observed[2].Status);
        Assert.IsTrue(observed[2].Read); Assert.IsFalse(observed[2].Present); Assert.IsNull(observed[2].Marker,
            "A legacy reader cannot fabricate a marker from the original non-versioned bytes.");
        byte[] oldMarkerEnvelope = SaveWith(2, 1, new[] { "cutover" }, new Dictionary<string, byte[]> { ["cutover"] = new byte[] { 66 } });
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "old-marker-envelope.sav"); mod.Load(oldMarkerEnvelope);
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.AreEqual(4, observed.Count); Assert.AreEqual(VersionedSaveLoadStatus.Restored, observed[3].Status);
        Assert.IsFalse(observed[3].Read); Assert.IsFalse(observed[3].Present); Assert.IsNull(observed[3].Marker,
            "A reserved marker beneath the required cutover reader must refuse rather than be treated as unmarked legacy data.");
        byte[] future = SaveWith(4, 4, new[] { "cutover" }, new Dictionary<string, byte[]> { ["cutover"] = new byte[] { 99 } });
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "future.sav"); mod.Load(future);
        Assert.IsFalse(extender.Registry.TryReadCriticalPreparedMarker(enrollment, prior, out _, out _, out _));
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.AreEqual(5, observed.Count); Assert.AreEqual(VersionedSaveLoadStatus.TooNew, observed[4].Status);
        Assert.IsFalse(observed[4].Read); Assert.IsFalse(observed[4].Present); Assert.IsNull(observed[4].Marker);
        Assert.IsFalse(extender.Registry.TryReadCriticalPreparedMarker(enrollment, current, out _, out _, out _));
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
    }
}
