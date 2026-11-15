using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;

namespace APIShared.Core.Tests;

public partial class VersionedSaveDataTests
{
    [TestMethod]
    public void PreparedOriginTrackingRequiresTheExactOwnerHandleAndStartup()
    {
        var extender = new FakeExtender();
        var registration = extender.Registration("Origin.Exact", 1, 1, new[] { "value" }, Writes(new()));
        registration.Prepared = _ => { };
        var mod = extender.Register(registration);
        Assert.IsFalse(extender.Registry.TryTrackPreparedSource("other", mod.Handle, out _, out _));
        Assert.IsFalse(extender.Registry.TryTrackPreparedSource("owner",
            new VersionedSaveDataHandle("owner", mod.Identifier), out _, out _));
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out var source, out string reason), reason);
        Assert.AreSame(mod.Handle, source.Handle);
        Assert.IsFalse(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out _, out _));
        var withoutPrepared = extender.Register("Origin.NoCallback", 1, 1, new[] { "value" }, Writes(new()));
        Assert.IsFalse(extender.Registry.TryTrackPreparedSource("owner", withoutPrepared.Handle, out _, out _));
        var late = extender.Registration("Origin.Late", 1, 1, new[] { "value" }, Writes(new()));
        late.Prepared = _ => { };
        var lateMod = extender.Register(late);
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav");
        Assert.IsFalse(extender.Registry.TryTrackPreparedSource("owner", lateMod.Handle, out _, out _));
    }

    [TestMethod]
    public void PreparedOriginBindsRawBytesExactReceiverAndSuccessfulCompletion()
    {
        var extender = new FakeExtender();
        VersionedSavePreparedSource source = null;
        VersionedSavePreparedOrigin origin = null;
        VersionedSaveLoad receiver = null;
        bool callbackCompleted = false;
        byte[] raw = SaveWith(1, 1, new[] { "value" }, new() { ["value"] = new byte[] { 1 } });
        var registration = extender.Registration("Origin.Bytes", 2, 1, new[] { "value" }, Writes(new()));
        registration.AddMigration(1, entries => entries.Set("value", new byte[] { 2 }));
        registration.Prepared = load =>
        {
            receiver = load;
            Assert.IsTrue(source.TryGetCurrent(load, out origin, out string reason), reason);
            Assert.IsFalse(source.TryValidateCompleted(origin, out _));
            var forgedCopy = new VersionedSaveLoad(load.Status, load.Source, load.FilePath,
                load.RecordVersion, load.MinimumReaderVersion, load.Entries, load.KeptEntryCount, load.Detail, origin);
            Assert.IsFalse(source.TryGetCurrent(forgedCopy, out _, out _));
            CollectionAssert.AreEqual(SHA256.HashData(raw), origin.CopyOriginalRecordDigest());
            byte[] changedDigest = origin.CopyOriginalRecordDigest(); changedDigest[0] ^= 0xff;
            CollectionAssert.AreEqual(SHA256.HashData(raw), origin.CopyOriginalRecordDigest());
            load.Entries["value"][0] = 99;
            callbackCompleted = true;
        };
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out source, out _));
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav"); mod.Load(raw);
        extender.Registry.PrepareLoad();
        Assert.IsTrue(callbackCompleted);
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
        Assert.IsFalse(source.TryGetCurrent(receiver, out _, out _));
        Assert.IsTrue(source.TryValidateCompleted(origin, out _));
        Assert.IsNull(origin.Receiver, "The token must not retain the callback's copied entry arrays.");
        extender.Registry.EndLoad();
        Assert.IsNull(mod.Loads.Single().PreparedOrigin, "Post is not another staging delivery.");
        CollectionAssert.AreEqual(new byte[] { 2 }, mod.Loads.Single().Entries["value"]);
    }

    [TestMethod]
    public void PreparedOriginCannotBeBorrowedByForeignThreadsOrNestedCapture()
    {
        var extender = new FakeExtender();
        var other = new FakeExtender();
        VersionedSavePreparedSource source = null;
        VersionedSaveLoad current = null;
        bool captureWasMasked = false;
        bool callbackCompleted = false;
        int callerThreadId = 0;
        int foreignThreadId = 0;
        bool? foreignResult = null;
        Exception foreignException = null;
        System.Threading.Thread foreignThread = null;
        bool foreignJoinedDuringPrepared = false;
        bool foreignCleanupJoined = true;
        var otherMod = other.Register("Origin.OtherCapture", 1, 1, new[] { "value" }, writer =>
        {
            captureWasMasked = !source.TryGetCurrent(current, out _, out _);
            writer.Write("value", new byte[] { 1 }); return true;
        });
        var registration = extender.Registration("Origin.Thread", 1, 1, new[] { "value" }, Writes(new()));
        registration.Prepared = load =>
        {
            current = load;
            Assert.IsTrue(source.TryGetCurrent(load, out _, out _));
            callerThreadId = Environment.CurrentManagedThreadId;
            foreignThread = new System.Threading.Thread(() =>
            {
                foreignThreadId = Environment.CurrentManagedThreadId;
                try { foreignResult = source.TryGetCurrent(load, out _, out _); }
                catch (Exception error) { foreignException = error; }
            }) { IsBackground = true };
            foreignThread.Start();
            foreignJoinedDuringPrepared = foreignThread.Join(1500);
            if (!foreignJoinedDuringPrepared) return;
            Assert.IsNotNull(other.Save(otherMod));
            Assert.IsTrue(captureWasMasked);
            Assert.IsTrue(source.TryGetCurrent(load, out _, out _), "The original delivery resumes after the nested callback.");
            callbackCompleted = true;
        };
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out source, out _));
        try
        {
            extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav");
            extender.Registry.PrepareLoad();
        }
        finally
        {
            // A blocked regression receives at most two seconds total joining.
            // Cleanup cannot turn a late result into an active-delivery proof.
            if (foreignThread != null && foreignThread.IsAlive)
                foreignCleanupJoined = foreignThread.Join(500);
        }
        Assert.IsTrue(foreignCleanupJoined, "Foreign worker did not terminate within the bounded joins.");
        Assert.IsTrue(foreignJoinedDuringPrepared, "Foreign call must finish while Prepared is still active.");
        Assert.IsNull(foreignException, foreignException?.ToString());
        Assert.IsTrue(callerThreadId > 0 && foreignThreadId > 0, "Both actual managed thread IDs must be observed.");
        Assert.AreEqual(Environment.CurrentManagedThreadId, callerThreadId);
        Assert.AreNotEqual(callerThreadId, foreignThreadId, "The regression must exercise an actual different BCL thread.");
        Assert.IsTrue(foreignResult.HasValue, "Foreign call must publish its observed result.");
        Assert.IsFalse(foreignResult.Value);
        Assert.IsTrue(callbackCompleted, "Prepared callback assertions must not disappear into framework failure isolation.");
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
        Assert.AreEqual(0L, other.Registry.CallbackFailures);
    }

    [TestMethod]
    public void PreparedOriginMasksNestedLegacyMigrationPreparedAndPostCallbacks()
    {
        var first = new FakeExtender(); var second = new FakeExtender();
        VersionedSavePreparedSource source = null; VersionedSaveLoad active = null;
        int masked = 0;
        bool callbackCompleted = false;
        var nested = second.Registration("Origin.Nested", 2, 1, new[] { "value" }, Writes(new()));
        nested.AddMigration(1, ignoredEntries => { if (!source.TryGetCurrent(active, out _, out _)) masked++; });
        nested.Prepared = ignoredLoad => { if (!source.TryGetCurrent(active, out _, out _)) masked++; };
        // FakeExtender delivers Loaded through its ordinary registration wrapper.
        var nestedRegistration = new VersionedSaveDataRegistration("owner", nested.SaveDataIdentifier, 2, 1,
            nested.KnownEntries, nested.Capture, ignoredLoad => { if (!source.TryGetCurrent(active, out _, out _)) masked++; })
            {
                Prepared = nested.Prepared,
                LegacyReader = (byte[] ignoredData, VersionedSaveEntries entries, out int version) =>
                {
                    if (!source.TryGetCurrent(active, out _, out _)) masked++;
                    version = 1; entries.Set("value", new byte[] { 1 }); return true;
                }
            };
        nestedRegistration.AddMigration(1, nested.Migrations[1]);
        var nestedMod = second.Register(nestedRegistration);
        var registration = first.Registration("Origin.Outer", 1, 1, new[] { "value" }, Writes(new()));
        registration.Prepared = load =>
        {
            active = load;
            second.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "nested.sav");
            nestedMod.Load(new byte[] { 1 });
            second.Registry.PrepareLoad(); second.Registry.EndLoad();
            Assert.AreEqual(4, masked);
            Assert.IsTrue(source.TryGetCurrent(load, out _, out _));
            callbackCompleted = true;
        };
        var mod = first.Register(registration);
        Assert.IsTrue(first.Registry.TryTrackPreparedSource("owner", mod.Handle, out source, out _));
        first.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "outer.sav"); first.Registry.PrepareLoad();
        Assert.IsTrue(callbackCompleted);
        Assert.AreEqual(0L, first.Registry.CallbackFailures);
        Assert.AreEqual(0L, second.Registry.CallbackFailures);
    }

    [TestMethod]
    public void PreparedOriginCallbackFailureRevokesStagingAndKeepsOriginalBytes()
    {
        var extender = new FakeExtender(); VersionedSavePreparedSource source = null;
        VersionedSavePreparedOrigin origin = null;
        byte[] raw = SaveWith(1, 1, new[] { "value" }, new() { ["value"] = new byte[] { 4 } });
        var registration = extender.Registration("Origin.Throw", 1, 1, new[] { "value" }, Writes(new() { ["value"] = new byte[] { 8 } }));
        registration.Prepared = load => { Assert.IsTrue(source.TryGetCurrent(load, out origin, out _)); throw new InvalidOperationException("rejected"); };
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out source, out _));
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav"); mod.Load(raw); extender.Registry.PrepareLoad();
        Assert.IsFalse(source.TryValidateCompleted(origin, out _)); Assert.IsNull(origin.Receiver);
        extender.Registry.EndLoad();
        Assert.IsTrue(mod.Handle.KeepsLoadedRecord); CollectionAssert.AreEqual(raw, extender.Save(mod));
        Assert.AreEqual(1L, extender.Registry.CallbackFailures);
    }

    [TestMethod]
    public void PreparedOriginNestedReplacementAndPrematureEndNeverCompleteOuterDelivery()
    {
        var extender = new FakeExtender(); VersionedSavePreparedSource source = null;
        VersionedSavePreparedOrigin old = null, replacement = null; bool replaced = false;
        int completedDeliveries = 0;
        var registration = extender.Registration("Origin.Replace", 1, 1, new[] { "value" }, Writes(new()));
        registration.Prepared = load =>
        {
            Assert.IsTrue(source.TryGetCurrent(load, out var token, out _));
            if (!replaced)
            {
                old = token; replaced = true;
                extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "replacement.sav");
                Assert.IsFalse(source.TryGetCurrent(load, out _, out _));
            }
            else replacement = token;
            completedDeliveries++;
        };
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out source, out _));
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "old.sav"); extender.Registry.PrepareLoad();
        Assert.AreEqual(1, completedDeliveries);
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
        Assert.IsFalse(source.TryValidateCompleted(old, out _));
        extender.Registry.PrepareLoad(); Assert.IsTrue(source.TryValidateCompleted(replacement, out _));
        Assert.AreEqual(2, completedDeliveries);
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
        extender.Registry.EndLoad(); extender.Registry.Unload(); Assert.IsFalse(source.TryValidateCompleted(replacement, out _));

        var premature = new FakeExtender(); VersionedSavePreparedSource endSource = null; VersionedSavePreparedOrigin ended = null;
        bool endingCompleted = false;
        var ending = premature.Registration("Origin.End", 1, 1, new[] { "value" }, Writes(new()));
        ending.Prepared = load =>
        {
            Assert.IsTrue(endSource.TryGetCurrent(load, out ended, out _));
            premature.Registry.EndLoad(); endingCompleted = true;
        };
        var endMod = premature.Register(ending);
        Assert.IsTrue(premature.Registry.TryTrackPreparedSource("owner", endMod.Handle, out endSource, out _));
        premature.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "early.sav"); premature.Registry.PrepareLoad();
        Assert.IsTrue(endingCompleted);
        Assert.AreEqual(0L, premature.Registry.CallbackFailures);
        Assert.IsFalse(endSource.TryValidateCompleted(ended, out _));
    }

    [TestMethod]
    public void PreparedOriginDistinguishesNoRecordTooNewAndOutOfScope()
    {
        var extender = new FakeExtender(); VersionedSavePreparedSource source = null;
        VersionedSavePreparedOrigin origin = null; VersionedSaveLoadStatus expected = VersionedSaveLoadStatus.NoRecord;
        var observedStatuses = new List<VersionedSaveLoadStatus>();
        var observedReadability = new List<bool>();
        int completedDeliveries = 0;
        var registration = extender.Registration("Origin.Status", 1, 1, new[] { "value" }, Writes(new()));
        registration.Prepared = load =>
        {
            bool readable = source.TryGetCurrent(load, out var valid, out _);
            observedStatuses.Add(load.Status); observedReadability.Add(readable);
            Assert.AreEqual(expected, load.Status);
            Assert.AreEqual(expected == VersionedSaveLoadStatus.NoRecord, readable);
            origin = load.PreparedOrigin;
            if (readable) { Assert.IsFalse(valid.HasRecord); CollectionAssert.AreEqual(SHA256.HashData(Array.Empty<byte>()), valid.CopyOriginalRecordDigest()); }
            completedDeliveries++;
        };
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out source, out _));
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "empty.sav"); extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.IsTrue(source.TryValidateCompleted(origin, out _));
        expected = VersionedSaveLoadStatus.TooNew;
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "future.sav");
        mod.Load(SaveWith(2, 2, new[] { "value" }, new() { ["value"] = new byte[] { 1 } }));
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.IsFalse(source.TryValidateCompleted(origin, out _)); Assert.IsTrue(mod.Handle.KeepsLoadedRecord);
        expected = VersionedSaveLoadStatus.OutOfScope;
        extender.Registry.BeginLoad(VersionedSaveLoadSource.MapToPlay, "map.map"); extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.IsFalse(source.TryValidateCompleted(origin, out _));
        Assert.AreEqual(3, completedDeliveries);
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
        CollectionAssert.AreEqual(new[] { VersionedSaveLoadStatus.NoRecord, VersionedSaveLoadStatus.TooNew,
            VersionedSaveLoadStatus.OutOfScope }, observedStatuses);
        CollectionAssert.AreEqual(new[] { true, false, false }, observedReadability);
    }

    [TestMethod]
    public void PreparedOriginPostFailureRevokesCompletedTokenAndRetainsRecord()
    {
        var extender = new FakeExtender(); VersionedSavePreparedSource source = null; VersionedSavePreparedOrigin origin = null;
        byte[] raw = SaveWith(1, 1, new[] { "value" }, new() { ["value"] = new byte[] { 1 } });
        var registration = new VersionedSaveDataRegistration("owner", "Origin.PostThrow", 1, 1,
            new[] { "value" }, Writes(new()), _ => throw new InvalidOperationException("Post rejected"))
            { Prepared = load => Assert.IsTrue(source.TryGetCurrent(load, out origin, out _)) };
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out source, out _));
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav"); mod.Load(raw); extender.Registry.PrepareLoad();
        Assert.IsTrue(source.TryValidateCompleted(origin, out _));
        extender.Registry.EndLoad(); Assert.IsFalse(source.TryValidateCompleted(origin, out _));
        Assert.AreEqual(1L, extender.Registry.CallbackFailures);
        Assert.IsTrue(mod.Handle.KeepsLoadedRecord); CollectionAssert.AreEqual(raw, extender.Save(mod));
    }

    [TestMethod]
    public void PreparedOriginTrackingHasABoundedProcessLifetimeCatalogue()
    {
        var extender = new FakeExtender();
        for (int i = 0; i < 129; i++)
        {
            var registration = extender.Registration("Origin.Bounded" + i, 1, 1, new[] { "value" }, Writes(new()));
            registration.Prepared = _ => { };
            var mod = extender.Register(registration);
            Assert.AreEqual(i < 128, extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out _, out _));
        }
    }
}
