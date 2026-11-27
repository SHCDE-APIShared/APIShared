using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Core.Tests;

public partial class VersionedSaveDataTests
{
    private static readonly VersionedSaveContext CriticalLogicalContext = new(true, false, "logical-destination.sav");

    private sealed class CriticalSourceFixture
    {
        internal readonly FakeExtender Extender;
        internal readonly FakeExtender.Mod Mod;
        internal readonly VersionedSavePreparedSource PreparedSource;
        internal readonly VersionedSaveCriticalSource Enrollment;
        internal VersionedSavePreparedOrigin Origin;
        internal int Captures;
        internal VersionedSaveCapture DuringCapture;
        internal byte Value = 1;
        internal string OriginError;

        internal CriticalSourceFixture(string identifier, FakeExtender extender = null)
        {
            Extender = extender ?? new FakeExtender();
            var registration = Extender.Registration(identifier, 3, 1, new[] { "value", "cutover" }, writer =>
            {
                Captures++;
                if (DuringCapture != null) return DuringCapture(writer);
                writer.Write("value", new byte[] { Value }); return true;
            });
            registration.Scope = VersionedSaveScope.SaveGamesAndMaps;
            registration.SimulationRelevant = true;
            registration.Prepared = load =>
            {
                if (!PreparedSource.TryGetCurrent(load, out Origin, out string reason)) OriginError = reason;
            };
            Mod = Extender.Register(registration);
            Assert.IsTrue(Extender.Registry.TryTrackPreparedSource("owner", Mod.Handle, out var prepared, out string detail), detail);
            PreparedSource = prepared;
            Assert.IsTrue(Extender.Registry.TryEnrollCriticalSource(PreparedSource, "cutover", 3, out var enrollment, out detail), detail);
            Enrollment = enrollment;
        }

        internal void Load(byte[] bytes = null)
        {
            Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "loaded-source.sav");
            if (bytes != null) Mod.Load(bytes);
            Extender.Registry.PrepareLoad(); Extender.Registry.EndLoad();
            Assert.IsNull(OriginError);
            Assert.IsNotNull(Origin);
            Assert.IsTrue(PreparedSource.TryValidateCompleted(Origin, out string reason), reason);
        }

        internal VersionedSaveCriticalRecord Record(byte[] marker = null) =>
            new(Enrollment, Origin, marker ?? new byte[] { 7 });

        internal bool Freeze(out VersionedSaveCriticalCapture capture, out string reason, int maximumBytes = 4096) =>
            Extender.Registry.TryFreezeCriticalRecords(new[] { Record() }, CriticalLogicalContext, maximumBytes, out capture, out reason);
    }

    [TestMethod]
    public void CriticalEnrollmentRequiresAnExactStartupSourceAndDeclaredScope()
    {
        var fixture = new CriticalSourceFixture("Critical.Enrollment");
        Assert.IsFalse(fixture.Extender.Registry.TryEnrollCriticalSource(fixture.PreparedSource, "cutover", 3, out _, out _));
        var other = new CriticalSourceFixture("Critical.Foreign");
        Assert.IsFalse(fixture.Extender.Registry.TryEnrollCriticalSource(other.PreparedSource, "cutover", 3, out _, out _));
        var extender = new FakeExtender();
        var registration = extender.Registration("Critical.SaveOnly", 3, 1, new[] { "value", "cutover" }, Writes(new()));
        registration.Prepared = ignored => { };
        registration.SimulationRelevant = true;
        var mod = extender.Register(registration);
        Assert.IsTrue(extender.Registry.TryTrackPreparedSource("owner", mod.Handle, out var source, out _));
        Assert.IsFalse(extender.Registry.TryEnrollCriticalSource(source, "cutover", 3, out _, out _));
        var declarations = new FakeExtender();
        var plain = declarations.Registration("Critical.Declared", 3, 1, new[] { "value", "cutover" }, Writes(new()));
        plain.Scope = VersionedSaveScope.SaveGamesAndMaps; plain.SimulationRelevant = true; plain.Prepared = ignored => { };
        var declared = declarations.Register(plain);
        Assert.IsTrue(declarations.Registry.TryTrackPreparedSource("owner", declared.Handle, out var declaredSource, out _));
        Assert.IsFalse(declarations.Registry.TryEnrollCriticalSource(declaredSource, "not-known", 3, out _, out _));
        Assert.IsFalse(declarations.Registry.TryEnrollCriticalSource(declaredSource, "cutover", 0, out _, out _));
        Assert.IsFalse(declarations.Registry.TryEnrollCriticalSource(declaredSource, "cutover", 4, out _, out _));
        Assert.IsTrue(declarations.Registry.TryEnrollCriticalSource(declaredSource, "cutover", 3, out _, out _));
        var irrelevant = declarations.Registration("Critical.NotRelevant", 3, 1, new[] { "value", "cutover" }, Writes(new()));
        irrelevant.Scope = VersionedSaveScope.SaveGamesAndMaps; irrelevant.Prepared = ignored => { };
        var irrelevantMod = declarations.Register(irrelevant);
        Assert.IsTrue(declarations.Registry.TryTrackPreparedSource("owner", irrelevantMod.Handle, out var irrelevantSource, out _));
        Assert.IsFalse(declarations.Registry.TryEnrollCriticalSource(irrelevantSource, "cutover", 3, out _, out _));
        fixture.Load();
        Assert.IsFalse(fixture.Extender.Registry.TryEnrollCriticalSource(fixture.PreparedSource, "other", 3, out _, out _));
    }

    [TestMethod]
    public void CriticalPreparationDoesNotMarkWrittenAndActualTransportCapturesOnlyOnce()
    {
        var fixture = new CriticalSourceFixture("Critical.Once"); fixture.Load();
        byte[] marker = { 7 };
        Assert.IsTrue(fixture.Extender.Registry.TryFreezeCriticalRecords(new[] { fixture.Record(marker) }, CriticalLogicalContext,
            4096, out var capture, out string reason), reason);
        using (capture)
        {
            Assert.AreEqual(1, fixture.Captures);
            Assert.AreEqual(VersionedSaveWriteStatus.NotWritten, fixture.Mod.Handle.LastWriteStatus);
            Assert.IsFalse(capture.AllWritten(out _));
            marker[0] = 99; fixture.Value = 42;
            byte[] bytes = fixture.Mod.SaveCallback(CriticalLogicalContext);
            Assert.AreEqual(1, fixture.Captures, "The real transport must serve frozen bytes, not recapture.");
            Assert.AreEqual(VersionedSaveWriteStatus.Written, fixture.Mod.Handle.LastWriteStatus);
            var decoded = VersionedSaveRecordCodec.Decode(bytes, 3);
            Assert.AreEqual(3, decoded.MinimumReaderVersion);
            Assert.IsTrue(decoded.Entries.TryGet("value", out var value)); CollectionAssert.AreEqual(new byte[] { 1 }, value);
            Assert.IsTrue(decoded.Entries.TryGet("cutover", out var savedMarker)); CollectionAssert.AreEqual(new byte[] { 7 }, savedMarker);
            Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.TooNew, VersionedSaveRecordCodec.Decode(bytes, 2).Status);
            Assert.IsTrue(capture.AllWritten(out reason), reason);
            Assert.IsTrue(capture.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
            bytes[bytes.Length - 1] ^= 1;
            Assert.IsFalse(capture.MatchesRecord(fixture.Enrollment, bytes, out _), "The transport's returned array must not mutate the frozen record.");
        }
        Assert.AreEqual(0L, fixture.Extender.Registry.CallbackFailures);
    }

    [TestMethod]
    public void CriticalCaptureUsesLogicalSaveContextRatherThanNativeTemporaryPath()
    {
        var fixture = new CriticalSourceFixture("Critical.LogicalContext"); fixture.Load();
        Assert.IsTrue(fixture.Freeze(out var capture, out string reason), reason);
        using (capture)
        {
            Assert.IsNull(fixture.Mod.SaveCallback(new VersionedSaveContext(true, false, ".native-temporary.tmp")));
            Assert.IsFalse(capture.AllWritten(out _));
            Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, fixture.Mod.Handle.LastWriteStatus);
        }
        // A normalized synchronized context legitimately has a null logical path.
        var normalized = new VersionedSaveContext(true, false, null);
        Assert.IsTrue(fixture.Extender.Registry.TryFreezeCriticalRecords(new[] { fixture.Record() }, normalized,
            4096, out capture, out reason), reason);
        using (capture)
        {
            byte[] bytes = fixture.Mod.SaveCallback(normalized);
            Assert.IsNotNull(bytes);
            Assert.IsTrue(capture.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
        }
    }

    [TestMethod]
    public void EnrolledOrdinaryCaptureCannotForgeTheReservedMarkerButUnenrolledCaptureIsUnchanged()
    {
        var fixture = new CriticalSourceFixture("Critical.Reserved"); fixture.Load();
        fixture.DuringCapture = writer => { writer.Write("cutover", new byte[] { 123 }); return true; };
        Assert.IsNull(fixture.Extender.Save(fixture.Mod));
        Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, fixture.Mod.Handle.LastWriteStatus);
        Assert.AreEqual(1L, fixture.Extender.Registry.CallbackFailures);
        var ordinary = new FakeExtender();
        var mod = ordinary.Register("Critical.NotEnrolled", 3, 1, new[] { "cutover" },
            Writes(new Dictionary<string, byte[]> { ["cutover"] = new byte[] { 123 } }));
        var decoded = VersionedSaveRecordCodec.Decode(ordinary.Save(mod), 3);
        Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.Valid, decoded.Status);
        Assert.IsTrue(decoded.Entries.TryGet("cutover", out var bytes)); CollectionAssert.AreEqual(new byte[] { 123 }, bytes);
        Assert.AreEqual(0L, ordinary.Registry.CallbackFailures);
    }

    [TestMethod]
    public void CriticalCaptureReservesEverySourceBeforeInvokingAnyConsumer()
    {
        var extender = new FakeExtender();
        var first = new CriticalSourceFixture("Critical.A", extender);
        var second = new CriticalSourceFixture("Critical.B", extender);
        first.Load();
        Assert.IsTrue(second.PreparedSource.TryValidateCompleted(second.Origin, out _));
        byte[] nestedResult = new byte[] { 99 };
        first.DuringCapture = writer =>
        {
            nestedResult = second.Mod.SaveCallback(CriticalLogicalContext);
            writer.Write("value", new byte[] { 1 }); return true;
        };
        Assert.IsFalse(extender.Registry.TryFreezeCriticalRecords(new[] { first.Record(), second.Record() },
            CriticalLogicalContext, 4096, out var capture, out _));
        Assert.IsNull(capture); Assert.IsNull(nestedResult);
        Assert.AreEqual(1, first.Captures); Assert.AreEqual(0, second.Captures);
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
        // Failed preparation must release its reservation instead of leaving the
        // next independently requested save trapped in an abandoned transaction.
        Assert.IsNotNull(second.Mod.SaveCallback(CriticalLogicalContext));
        Assert.AreEqual(1, second.Captures);
    }

    [TestMethod]
    public void CriticalCaptureRefusesDuplicateTransportAndWrongActualThread()
    {
        var fixture = new CriticalSourceFixture("Critical.Duplicate"); fixture.Load();
        Assert.IsTrue(fixture.Freeze(out var capture, out string reason), reason);
        using (capture)
        {
            Assert.IsNotNull(fixture.Mod.SaveCallback(CriticalLogicalContext));
            Assert.IsNull(fixture.Mod.SaveCallback(CriticalLogicalContext));
            Assert.IsFalse(capture.AllWritten(out _));
        }
        Assert.IsTrue(fixture.Freeze(out capture, out reason), reason);
        using (capture)
        {
            byte[] foreignResult = new byte[] { 99 }; Exception foreignError = null;
            int caller = Environment.CurrentManagedThreadId, worker = 0;
            var thread = new Thread(() =>
            {
                worker = Environment.CurrentManagedThreadId;
                try { foreignResult = fixture.Mod.SaveCallback(CriticalLogicalContext); }
                catch (Exception error) { foreignError = error; }
            }) { IsBackground = true };
            thread.Start(); Assert.IsTrue(thread.Join(2000), "Foreign save call must finish within the bounded join.");
            Assert.AreNotEqual(caller, worker); Assert.IsTrue(worker > 0);
            Assert.IsNull(foreignError); Assert.IsNull(foreignResult); Assert.IsFalse(capture.AllWritten(out _));
        }
    }

    [TestMethod]
    public void CriticalCaptureInvalidatesOnNestedLoadAndUnloadWithoutServingStaleBytes()
    {
        var fixture = new CriticalSourceFixture("Critical.Generation"); fixture.Load();
        fixture.DuringCapture = writer =>
        {
            fixture.Extender.Registry.Unload(); writer.Write("value", new byte[] { 1 }); return true;
        };
        Assert.IsFalse(fixture.Freeze(out var capture, out _)); Assert.IsNull(capture);
        fixture.DuringCapture = null; fixture.Load();
        Assert.IsTrue(fixture.Freeze(out capture, out string reason), reason);
        using (capture)
        {
            fixture.Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "replacement.sav");
            Assert.IsNull(fixture.Mod.SaveCallback(CriticalLogicalContext));
            Assert.IsFalse(capture.AllWritten(out _));
            fixture.Extender.Registry.EndLoad();
        }
        Assert.AreEqual(0L, fixture.Extender.Registry.CallbackFailures);
    }

    [TestMethod]
    public void CriticalCaptureRetainsUnknownEntriesAndRefusesUnreadableOrigins()
    {
        var fixture = new CriticalSourceFixture("Critical.Unknown");
        byte[] loaded = SaveWith(3, 1, new[] { "value", "future" }, new Dictionary<string, byte[]>
            { ["value"] = new byte[] { 2 }, ["future"] = new byte[] { 9 } });
        fixture.Load(loaded);
        Assert.IsTrue(fixture.Freeze(out var capture, out string reason), reason);
        using (capture)
        {
            byte[] bytes = fixture.Mod.SaveCallback(CriticalLogicalContext);
            var decoded = VersionedSaveRecordCodec.Decode(bytes, 3);
            Assert.IsTrue(decoded.Entries.TryGet("future", out var unknown)); CollectionAssert.AreEqual(new byte[] { 9 }, unknown);
            Assert.IsTrue(capture.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
        }
        byte[] tooNew = SaveWith(4, 4, new[] { "value" }, WritesState(4));
        fixture.Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "too-new.sav");
        fixture.Mod.Load(tooNew); fixture.Extender.Registry.PrepareLoad(); fixture.Extender.Registry.EndLoad();
        int previous = fixture.Captures;
        Assert.IsFalse(fixture.Freeze(out capture, out _)); Assert.IsNull(capture);
        Assert.AreEqual(previous, fixture.Captures);
        CollectionAssert.AreEqual(tooNew, fixture.Extender.Save(fixture.Mod));
    }

    private static Dictionary<string, byte[]> WritesState(byte value) => new() { ["value"] = new byte[] { value } };

    [TestMethod]
    public void CriticalCaptureRejectsFalseOrFailedCallbacksAndAggregateOverflowWithoutWrittenCredit()
    {
        var fixture = new CriticalSourceFixture("Critical.Failures"); fixture.Load();
        fixture.DuringCapture = writer => false;
        Assert.IsFalse(fixture.Freeze(out var capture, out _)); Assert.IsNull(capture);
        Assert.AreEqual(VersionedSaveWriteStatus.NotWritten, fixture.Mod.Handle.LastWriteStatus);
        fixture.DuringCapture = writer => throw new InvalidOperationException("controlled-capture-failure");
        Assert.IsFalse(fixture.Freeze(out capture, out _)); Assert.IsNull(capture);
        Assert.AreEqual(1L, fixture.Extender.Registry.CallbackFailures);
        Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, fixture.Mod.Handle.LastWriteStatus);
        fixture.DuringCapture = null;
        Assert.IsFalse(fixture.Freeze(out capture, out _, maximumBytes: 1)); Assert.IsNull(capture);
        Assert.AreEqual(VersionedSaveWriteStatus.NotWritten, fixture.Mod.Handle.LastWriteStatus);
        Assert.IsTrue(fixture.Freeze(out capture, out string reason), reason);
        capture.Dispose(); capture.Dispose();
        Assert.IsFalse(capture.AllWritten(out _), "An ended bundle cannot provide later commit evidence.");
    }
}
