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
        internal Action<VersionedSaveLoad> DuringPrepared;
        internal Action<VersionedSaveLoad> DuringLoaded;
        internal byte Value = 1;
        internal string OriginError;

        internal CriticalSourceFixture(string identifier, FakeExtender extender = null)
        {
            Extender = extender ?? new FakeExtender();
            var registration = new VersionedSaveDataRegistration("owner", identifier, 3, 1, new[] { "value", "cutover" }, writer =>
            {
                Captures++;
                if (DuringCapture != null) return DuringCapture(writer);
                writer.Write("value", new byte[] { Value }); return true;
            }, load => DuringLoaded?.Invoke(load));
            registration.Scope = VersionedSaveScope.SaveGamesAndMaps;
            registration.SimulationRelevant = true;
            registration.Prepared = load =>
            {
                if (!PreparedSource.TryGetCurrent(load, out Origin, out string reason)) OriginError = reason;
                DuringPrepared?.Invoke(load);
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
    [DataRow(false)]
    [DataRow(true)]
    public void CriticalFreezeWaitsForItsLoadedCallbackAndRefusesAFailedSource(bool rejectLoaded)
    {
        var fixture = new CriticalSourceFixture("Critical.FreezeLoaded");
        bool entered = false, preparedCompletedInside = false, frozeInside = true;
        fixture.DuringLoaded = ignoredLoad =>
        {
            entered = true;
            preparedCompletedInside = fixture.PreparedSource.TryValidateCompleted(fixture.Origin, out _);
            frozeInside = fixture.Freeze(out var unexpected, out _);
            unexpected?.Dispose();
            if (rejectLoaded) throw new InvalidOperationException("controlled-loaded-rejection");
        };
        byte[] record = SaveWith(3, 1, new[] { "value" }, WritesState(1));
        fixture.Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "loaded-freeze.sav"); fixture.Mod.Load(record);
        fixture.Extender.Registry.PrepareLoad(); fixture.Extender.Registry.EndLoad();
        Assert.IsTrue(entered && preparedCompletedInside);
        Assert.IsFalse(frozeInside);
        Assert.AreEqual(0, fixture.Captures, "A running Loaded callback must not prepare any frozen consumer record.");
        bool frozeAfter = fixture.Freeze(out var capture, out string reason);
        Assert.AreEqual(!rejectLoaded, frozeAfter, reason);
        if (rejectLoaded) Assert.IsNull(capture);
        else
        {
            using (capture)
            {
                byte[] bytes = fixture.Mod.SaveCallback(CriticalLogicalContext);
                Assert.IsTrue(capture.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
            }
        }
        Assert.AreEqual(rejectLoaded, fixture.Mod.Handle.KeepsLoadedRecord);
        Assert.AreEqual(rejectLoaded ? 1L : 0L, fixture.Extender.Registry.CallbackFailures);
    }

    [TestMethod]
    public void LaterLoadedCallbackCannotFinalizeEarlierSourceOrOverwriteANestedReplacement()
    {
        var extender = new FakeExtender();
        var first = new CriticalSourceFixture("Critical.A.Finalization", extender);
        bool nested = false, readDuringLater = true, frozeDuringLater = true, readAfterReentry = true;
        var later = new VersionedSaveDataRegistration("owner", "Critical.Z.Finalization", 1, 1,
            new[] { "value" }, Writes(new()), ignoredLoad =>
            {
                if (nested) return;
                readDuringLater = extender.Registry.TryReadCriticalPreparedMarker(first.Enrollment, first.Origin, out _, out _, out _);
                frozeDuringLater = first.Freeze(out var unexpected, out _);
                unexpected?.Dispose();
                extender.Registry.EndLoad(); // A reentrant call cannot finish the outer callback pass.
                readAfterReentry = extender.Registry.TryReadCriticalPreparedMarker(first.Enrollment, first.Origin, out _, out _, out _);
                nested = true;
                extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "replacement.sav");
                extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
            });
        extender.Register(later);
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "outer.sav");
        extender.Registry.PrepareLoad();
        var oldOrigin = first.Origin;
        extender.Registry.EndLoad();
        Assert.IsTrue(nested);
        Assert.IsFalse(readDuringLater); Assert.IsFalse(frozeDuringLater); Assert.IsFalse(readAfterReentry);
        Assert.AreEqual(0, first.Captures);
        Assert.IsFalse(first.PreparedSource.TryValidateCompleted(oldOrigin, out _));
        Assert.AreNotSame(oldOrigin, first.Origin);
        Assert.IsTrue(extender.Registry.TryReadCriticalPreparedMarker(first.Enrollment, first.Origin,
            out var marker, out bool present, out string reason), reason);
        Assert.IsNull(marker); Assert.IsFalse(present);
        Assert.IsTrue(first.Freeze(out var capture, out reason), reason);
        capture.Dispose();
        extender.Registry.Unload();
        Assert.IsFalse(extender.Registry.TryReadCriticalPreparedMarker(first.Enrollment, first.Origin, out _, out _, out _));
        Assert.IsFalse(first.Freeze(out capture, out _)); Assert.IsNull(capture);
        Assert.AreEqual(0L, extender.Registry.CallbackFailures);
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
    private static VersionedSaveCriticalCapture FreezeForFactory(CriticalSourceFixture fixture, VersionedSaveContext context)
    {
        if (!fixture.Extender.Registry.TryFreezeCriticalRecords(new[] { fixture.Record() }, context,
            4096, out var capture, out string reason)) throw new InvalidOperationException(reason);
        return capture;
    }

    [TestMethod]
    public void FactoryFrameRequiresOnePreparedBundleAndItsActualTransportDelivery()
    {
        var fixture = new CriticalSourceFixture("Critical.Factory.Once"); fixture.Load();
        var context = new VersionedSaveContext(true, false, null);
        int preparations = 0;
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
        using (var frame = VersionedSaveFactoryFrame.Begin(value => { preparations++; return FreezeForFactory(fixture, value); }))
        {
            frame.Capture(context);
            Assert.AreEqual(1, preparations); Assert.AreEqual(1, fixture.Captures);
            Assert.AreEqual(VersionedSaveWriteStatus.NotWritten, fixture.Mod.Handle.LastWriteStatus);
            Assert.IsNull(frame.Failure);
            byte[] bytes = fixture.Mod.SaveCallback(context);
            Assert.IsTrue(frame.TryGetWrittenCapture(out var capture, out string reason), reason);
            Assert.IsTrue(capture.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
            frame.Capture(context);
            Assert.AreEqual(1, preparations, "A repeated factory must not recapture consumer state.");
            Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
            Assert.IsNotNull(frame.Failure);
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
        // Disposing the failed frame released its reserved source, not just TLS.
        Assert.IsTrue(fixture.Freeze(out var next, out string detail), detail);
        next.Dispose();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void FactoryFrameMissingFailedOrNullPreparationCannotAuthorizeReplacement(int kind)
    {
        var failure = new InvalidOperationException("controlled-factory-failure");
        int calls = 0;
        using (var frame = VersionedSaveFactoryFrame.Begin(context => { calls++; if (kind == 1) throw failure; return null; }))
        {
            if (kind != 0) frame.Capture(CriticalLogicalContext);
            Assert.IsFalse(frame.TryGetWrittenCapture(out var capture, out string reason));
            Assert.IsNull(capture); Assert.IsFalse(string.IsNullOrEmpty(reason));
            Assert.AreEqual(kind == 0 ? 0 : 1, calls);
            if (kind == 1) Assert.AreSame(failure, frame.Failure, "SE swallowing a callback error must not erase it.");
            Exception first = frame.Failure;
            frame.Capture(CriticalLogicalContext);
            Assert.AreSame(first, frame.Failure); Assert.AreEqual(kind == 0 ? 0 : 1, calls);
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FactoryFrameCannotBorrowAnotherContextOrAnAlreadyServedBundle(bool serveFirst)
    {
        var fixture = new CriticalSourceFixture("Critical.Factory.Borrow"); fixture.Load();
        var original = new VersionedSaveContext(true, false, "actual.sav");
        VersionedSaveCriticalCapture bundle = null;
        byte[] served = null;
        using (var frame = VersionedSaveFactoryFrame.Begin(context => {
            bundle = FreezeForFactory(fixture, original);
            if (serveFirst) served = fixture.Mod.SaveCallback(original);
            return bundle;
        }))
        {
            frame.Capture(serveFirst ? original : new VersionedSaveContext(true, false, "other.sav"));
            Assert.IsNotNull(bundle);
            if (serveFirst) Assert.IsNotNull(served);
            Assert.IsNotNull(frame.Failure);
            Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
        }
        Assert.IsFalse(bundle.AllWritten(out _));
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FactoryFrameRequiresItsCallbackToPrepareTheBundleEvenWhenOldContextMatches(bool freshInCallback)
    {
        var fixture = new CriticalSourceFixture("Critical.Factory.PreparationOwner"); fixture.Load();
        var context = new VersionedSaveContext(true, false, null);
        VersionedSaveCriticalCapture bundle = freshInCallback ? null : FreezeForFactory(fixture, context);
        int preparations = 0;
        using (var frame = VersionedSaveFactoryFrame.Begin(value => {
            preparations++;
            if (freshInCallback) bundle = FreezeForFactory(fixture, value);
            return bundle;
        }))
        {
            frame.Capture(context);
            Assert.AreEqual(1, preparations); Assert.IsNotNull(bundle);
            byte[] bytes = fixture.Mod.SaveCallback(context);
            Assert.IsNotNull(bytes);
            // Its existing standalone delivery contract is independent of
            // whether this frame may claim preparation ownership.
            Assert.IsTrue(bundle.AllWritten(out string reason), reason);
            Assert.IsTrue(bundle.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
            if (freshInCallback)
            {
                Assert.IsNull(frame.Failure);
                Assert.IsTrue(frame.TryGetWrittenCapture(out var written, out reason), reason);
                Assert.AreSame(bundle, written);
            }
            else
            {
                Assert.IsNotNull(frame.Failure,
                    "A pre-Begin bundle must not gain factory ownership just because flags/path match.");
                Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
            }
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    public void FactoryFrameNestedAndOutOfOrderScopesNeverCertifyTheirParent()
    {
        var outer = VersionedSaveFactoryFrame.Begin(context => null);
        var inner = VersionedSaveFactoryFrame.Begin(context => null);
        try
        {
            Assert.AreSame(inner, VersionedSaveFactoryFrame.Current);
            Assert.IsNotNull(outer.Failure); Assert.IsNotNull(inner.Failure);
            Assert.ThrowsExactly<InvalidOperationException>(() => outer.Dispose());
            Assert.AreSame(inner, VersionedSaveFactoryFrame.Current);
            Assert.IsFalse(inner.TryGetWrittenCapture(out _, out _));
        }
        finally { inner.Dispose(); outer.Dispose(); }
        outer.Dispose();
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    public void FactoryFrameForeignThreadCannotBorrowOrCloseItsCreatorScope()
    {
        int preparations = 0; Exception closeFailure = null;
        VersionedSaveFactoryFrame foreignCurrent = null;
        using (var frame = VersionedSaveFactoryFrame.Begin(context => { preparations++; return null; }))
        {
            var thread = new Thread(() => {
                foreignCurrent = VersionedSaveFactoryFrame.Current;
                frame.Capture(CriticalLogicalContext);
                try { frame.Dispose(); } catch (Exception error) { closeFailure = error; }
            });
            thread.Start(); Assert.IsTrue(thread.Join(5000), "Controlled frame-only thread failed to terminate.");
            Assert.IsNull(foreignCurrent); Assert.IsInstanceOfType<InvalidOperationException>(closeFailure);
            Assert.AreSame(frame, VersionedSaveFactoryFrame.Current); Assert.AreEqual(0, preparations);
            Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    public void FactoryFrameReentrantCloseReleasesABundleReturnedAfterItsLifetimeEnded()
    {
        var fixture = new CriticalSourceFixture("Critical.Factory.ClosedDuringCapture"); fixture.Load();
        VersionedSaveFactoryFrame frame = null;
        VersionedSaveCriticalCapture returned = null;
        frame = VersionedSaveFactoryFrame.Begin(context => {
            frame.Dispose();
            returned = FreezeForFactory(fixture, context);
            return returned;
        });
        try
        {
            frame.Capture(CriticalLogicalContext);
            Assert.IsNotNull(returned); Assert.IsNotNull(frame.Failure);
            Assert.IsNull(VersionedSaveFactoryFrame.Current);
            Assert.IsFalse(returned.AllWritten(out _));
            Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
            Assert.IsTrue(fixture.Freeze(out var next, out string reason), reason);
            next.Dispose();
        }
        finally { frame.Dispose(); }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    public void CriticalWriterDemandRequiresAnExplicitCohortJoinAndCapturesStateOnlySources()
    {
        var fixture = new CriticalSourceFixture("Critical.Writer.StateOnly");
        Assert.IsFalse(fixture.Extender.Registry.HasCriticalWriterDemand,
            "Tracking and enrollment alone must retain their dormant standalone behavior.");
        Assert.IsTrue(fixture.Extender.Registry.TryJoinCriticalWriterCohort(fixture.Enrollment, out string reason), reason);
        Assert.IsTrue(fixture.Extender.Registry.HasCriticalWriterDemand);
        Assert.IsFalse(fixture.Extender.Registry.TryJoinCriticalWriterCohort(fixture.Enrollment, out _));
        fixture.Load();
        Assert.IsNull(fixture.Mod.SaveCallback(CriticalLogicalContext), "A joined source cannot capture outside its owned frame.");
        Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, fixture.Mod.Handle.LastWriteStatus);
        Assert.AreEqual(0, fixture.Captures);
        fixture.Extender.Registry.RequireCriticalWriterReady();
        using (var frame = fixture.Extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            Assert.AreEqual(0, fixture.Captures, "Opening the owned frame must not run a source before the factory callback.");
            frame.Capture(CriticalLogicalContext);
            Assert.IsNull(frame.Failure);
            Assert.AreEqual(1, fixture.Captures);
            byte[] bytes = fixture.Mod.SaveCallback(CriticalLogicalContext);
            Assert.IsTrue(frame.TryGetWrittenCapture(out var capture, out reason), reason);
            Assert.IsTrue(capture.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
            var decoded = VersionedSaveRecordCodec.Decode(bytes, 3);
            Assert.AreEqual(3, decoded.RecordVersion); Assert.AreEqual(3, decoded.MinimumReaderVersion);
            Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.TooNew, VersionedSaveRecordCodec.Decode(bytes, 2).Status);
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void UnjoinedStorageStyleCaptureRetainsLegacyVersionsAndUnknownEntryPolicy(int storesSchema)
    {
        var extender = new FakeExtender();
        var registration = extender.Registration("Critical.Writer.Unjoined", 2, 1, new[] { "value" }, writer =>
        {
            writer.UseVersion(storesSchema, storesSchema);
            writer.Write("value", new byte[] { 9 }); return true;
        });
        var mod = extender.Register(registration);
        Assert.IsFalse(extender.Registry.HasCriticalWriterDemand);
        byte[] initial = extender.Save(mod);
        var decoded = VersionedSaveRecordCodec.Decode(initial, 2);
        Assert.AreEqual(storesSchema, decoded.RecordVersion); Assert.AreEqual(storesSchema, decoded.MinimumReaderVersion);
        CollectionAssert.AreEqual(SaveWith(storesSchema, storesSchema, new[] { "value" },
            new Dictionary<string, byte[]> { ["value"] = new byte[] { 9 } }), initial);

        byte[] loaded = SaveWith(storesSchema, storesSchema, new[] { "value", "future" },
            new Dictionary<string, byte[]> { ["value"] = new byte[] { 1 }, ["future"] = new byte[] { 7 } });
        extender.LoadSave(loaded, mod);
        byte[] saved = extender.Save(mod);
        if (storesSchema == 1)
        {
            Assert.IsNull(saved, "An older schema may not relabel retained unknown entries.");
            Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, mod.Handle.LastWriteStatus);
        }
        else
            CollectionAssert.AreEqual(SaveWith(2, 2, new[] { "value", "future" },
                new Dictionary<string, byte[]> { ["value"] = new byte[] { 9 }, ["future"] = new byte[] { 7 } }), saved);
    }

    [TestMethod]
    public void CriticalWriterKeepsProtectedBytesWhenAnOriginalCohortParticipantIsMissing()
    {
        var original = new FakeExtender();
        var first = new CriticalSourceFixture("Critical.Writer.First", original);
        var second = new CriticalSourceFixture("Critical.Writer.Second", original);
        Assert.IsTrue(original.Registry.TryJoinCriticalWriterCohort(first.Enrollment, out string reason), reason);
        Assert.IsTrue(original.Registry.TryJoinCriticalWriterCohort(second.Enrollment, out reason), reason);
        first.Load();
        byte[] savedFirst;
        using (var frame = original.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            savedFirst = first.Mod.SaveCallback(CriticalLogicalContext);
            byte[] savedSecond = second.Mod.SaveCallback(CriticalLogicalContext);
            Assert.IsNotNull(savedFirst); Assert.IsNotNull(savedSecond);
            Assert.IsTrue(frame.TryGetWrittenCapture(out _, out reason), reason);
        }
        var replacement = new CriticalSourceFixture("Critical.Writer.First");
        Assert.IsTrue(replacement.Extender.Registry.TryJoinCriticalWriterCohort(replacement.Enrollment, out reason), reason);
        replacement.Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "protected.sav", 41);
        replacement.Mod.Load(savedFirst);
        replacement.Extender.Registry.PrepareLoad(); replacement.Extender.Registry.EndLoad();
        Assert.AreEqual(VersionedSaveLoadStatus.MigrationFailed, replacement.Mod.Loads[0].Status);
        Assert.IsTrue(replacement.Mod.Handle.KeepsLoadedRecord);
        CollectionAssert.AreEqual(savedFirst, replacement.Mod.SaveCallback(CriticalLogicalContext),
            "A smaller current cohort must not drop the original participant marker or downgrade its floor.");
        Assert.ThrowsExactly<InvalidOperationException>(() => replacement.Extender.Registry.RequireCriticalWriterReady());
        using (var frame = replacement.Extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            Assert.IsNotNull(frame.Failure);
            Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
            Assert.AreEqual(0, replacement.Captures);
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    public void CriticalCreatedWorldCanSaveRepeatedlyWithoutInventingAnArchiveCallbackOrMarker()
    {
        var fixture = new CriticalSourceFixture("Critical.Writer.Created");
        Assert.IsTrue(fixture.Extender.Registry.TryJoinCriticalWriterCohort(fixture.Enrollment, out string reason), reason);
        Assert.IsNull(fixture.Mod.SaveCallback(CriticalLogicalContext), "An unprepared joined source still cannot capture outside its frame.");
        Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, fixture.Mod.Handle.LastWriteStatus);
        Assert.AreEqual(0, fixture.Captures);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Extender.Registry.RequireCriticalWriterReady());
        object world = new object();
        Assert.IsFalse(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, null, 11, out _));
        Assert.IsFalse(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, world, 0, out _));
        Assert.IsTrue(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, world, 11, out reason), reason);
        fixture.Extender.Registry.RequireCriticalWriterReady();
        Assert.IsNull(fixture.Origin); Assert.AreEqual(0, fixture.Mod.Loads.Count);
        Assert.IsFalse(fixture.Extender.Registry.TryReadCriticalPreparedMarker(fixture.Enrollment, fixture.Origin, out _, out _, out _),
            "An owner-authorized created world has no original stored envelope to confirm.");
        for (int pass = 1; pass <= 2; pass++)
        {
            using (var frame = fixture.Extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
            {
                frame.CompleteOwnedPreparation();
                frame.Capture(CriticalLogicalContext);
                byte[] bytes = fixture.Mod.SaveCallback(CriticalLogicalContext);
                Assert.AreEqual(pass, fixture.Captures);
                Assert.IsTrue(frame.TryGetWrittenCapture(out var capture, out reason), reason);
                Assert.IsTrue(capture.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
            }
            fixture.Extender.Registry.RequireCriticalWriterReady();
        }
        Assert.IsNull(fixture.Origin); Assert.AreEqual(0, fixture.Mod.Loads.Count);
        Assert.IsFalse(fixture.PreparedSource.TryValidateCompleted(fixture.Origin, out _));
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CriticalCreatedWorldAuthorityIsRevokedByLoadOrUnloadAndCannotReplaceAnActiveCapture(bool unload)
    {
        var fixture = new CriticalSourceFixture("Critical.Writer.CreatedRevocation");
        Assert.IsTrue(fixture.Extender.Registry.TryJoinCriticalWriterCohort(fixture.Enrollment, out string reason), reason);
        object world = new object();
        Assert.IsTrue(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, world, 21, out reason), reason);
        using (var frame = fixture.Extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            Assert.IsNull(frame.Failure);
            Assert.IsFalse(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, new object(), 22, out _),
                "World replacement cannot revoke bytes underneath an active bundle.");
        }
        if (unload) fixture.Extender.Registry.Unload();
        else fixture.Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "next.sav", 22);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Extender.Registry.RequireCriticalWriterReady());
            if (!unload)
                Assert.IsFalse(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, new object(), 23, out _),
                    "A missing load callback cannot be replaced with created-world authority inside that archive window.");
            using (var frame = fixture.Extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
            {
                frame.CompleteOwnedPreparation();
                frame.Capture(CriticalLogicalContext);
                Assert.IsNotNull(frame.Failure);
                Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
            }
            Assert.AreEqual(1, fixture.Captures, "Revoked authority must fail before recapturing a source.");
        }
        finally { if (!unload) { fixture.Extender.Registry.PrepareLoad(); fixture.Extender.Registry.EndLoad(); } }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    public void CriticalCreatedWorldCannotBorrowAnotherParticipantsArchiveOrigin()
    {
        var extender = new FakeExtender();
        var first = new CriticalSourceFixture("Critical.Writer.MixedFirst", extender);
        var second = new CriticalSourceFixture("Critical.Writer.MixedSecond", extender);
        Assert.IsTrue(extender.Registry.TryJoinCriticalWriterCohort(first.Enrollment, out string reason), reason);
        Assert.IsTrue(extender.Registry.TryJoinCriticalWriterCohort(second.Enrollment, out reason), reason);
        first.Load();
        VersionedSavePreparedOrigin oldFirst = first.Origin;
        object world = new object();
        Assert.IsTrue(extender.Registry.TryAuthorizeCriticalCreatedWorld(first.PreparedSource, world, 31, out reason), reason);
        Assert.IsFalse(first.PreparedSource.TryValidateCompleted(oldFirst, out _));
        Assert.IsFalse(extender.Registry.TryReadCriticalPreparedMarker(first.Enrollment, oldFirst, out _, out _, out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => extender.Registry.RequireCriticalWriterReady());
        using (var frame = extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            Assert.IsNotNull(frame.Failure); Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
            Assert.AreEqual(0, first.Captures); Assert.AreEqual(0, second.Captures);
        }
        Assert.IsTrue(extender.Registry.TryAuthorizeCriticalCreatedWorld(second.PreparedSource, world, 31, out reason), reason);
        extender.Registry.RequireCriticalWriterReady();
        using (var frame = extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            Assert.IsNotNull(first.Mod.SaveCallback(CriticalLogicalContext));
            Assert.IsNotNull(second.Mod.SaveCallback(CriticalLogicalContext));
            Assert.IsTrue(frame.TryGetWrittenCapture(out _, out reason), reason);
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CriticalWriterRejectsAProtectedCohortWithAMissingOrLegacyPartner(bool legacyPartner)
    {
        var original = new FakeExtender();
        var originalFirst = new CriticalSourceFixture("Critical.Writer.MixedProtectedFirst", original);
        var originalSecond = new CriticalSourceFixture("Critical.Writer.MixedProtectedSecond", original);
        Assert.IsTrue(original.Registry.TryJoinCriticalWriterCohort(originalFirst.Enrollment, out string reason), reason);
        Assert.IsTrue(original.Registry.TryJoinCriticalWriterCohort(originalSecond.Enrollment, out reason), reason);
        originalFirst.Load();
        byte[] protectedFirst;
        using (var frame = original.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            protectedFirst = originalFirst.Mod.SaveCallback(CriticalLogicalContext);
            Assert.IsNotNull(originalSecond.Mod.SaveCallback(CriticalLogicalContext));
            Assert.IsTrue(frame.TryGetWrittenCapture(out _, out reason), reason);
        }
        byte[] legacySecond = legacyPartner ? SaveWith(2, 2, new[] { "value" },
            new Dictionary<string, byte[]> { ["value"] = new byte[] { 7 } }) : null;
        var replacement = new FakeExtender();
        var first = new CriticalSourceFixture("Critical.Writer.MixedProtectedFirst", replacement);
        var second = new CriticalSourceFixture("Critical.Writer.MixedProtectedSecond", replacement);
        Assert.IsTrue(replacement.Registry.TryJoinCriticalWriterCohort(first.Enrollment, out reason), reason);
        Assert.IsTrue(replacement.Registry.TryJoinCriticalWriterCohort(second.Enrollment, out reason), reason);
        // Each source can decode separately; only the completed archive pass can
        // reject the protected cohort's missing/unmarked partner without inventing state.
        replacement.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "mixed-protected.sav", 61);
        first.Mod.Load(protectedFirst);
        if (legacyPartner) second.Mod.Load(legacySecond);
        replacement.Registry.PrepareLoad(); replacement.Registry.EndLoad();
        Assert.IsTrue(first.Mod.Handle.KeepsLoadedRecord); Assert.IsTrue(second.Mod.Handle.KeepsLoadedRecord);
        Assert.IsFalse(first.PreparedSource.TryValidateCompleted(first.Origin, out _));
        Assert.IsFalse(second.PreparedSource.TryValidateCompleted(second.Origin, out _));
        CollectionAssert.AreEqual(protectedFirst, first.Mod.SaveCallback(CriticalLogicalContext));
        byte[] secondSaved = second.Mod.SaveCallback(CriticalLogicalContext);
        if (legacyPartner)
        {
            CollectionAssert.AreEqual(legacySecond, secondSaved);
            Assert.AreEqual(VersionedSaveWriteStatus.KeptLoadedRecord, second.Mod.Handle.LastWriteStatus);
        }
        else
        {
            Assert.IsNull(secondSaved);
            Assert.AreEqual(VersionedSaveWriteStatus.NotWritten, second.Mod.Handle.LastWriteStatus);
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => replacement.Registry.RequireCriticalWriterReady());
        using (var frame = replacement.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            Assert.IsNotNull(frame.Failure); Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
            Assert.AreEqual(0, first.Captures); Assert.AreEqual(0, second.Captures);
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CriticalWorldGateWaitsForEveryActualLoadCallbackAndRejectsMixedProtection(bool legacyPartner)
    {
        var extender = new FakeExtender();
        var first = new CriticalSourceFixture("Critical.Writer.WorldGateFirst", extender);
        var second = new CriticalSourceFixture("Critical.Writer.WorldGateSecond", extender);
        Assert.IsTrue(extender.Registry.TryJoinCriticalWriterCohort(first.Enrollment, out string reason), reason);
        Assert.IsTrue(extender.Registry.TryJoinCriticalWriterCohort(second.Enrollment, out reason), reason);
        object world = new object();
        long session = 77;
        var stages = new List<string>();
        var callbackResults = new List<bool>();
        var callbackCreatedResults = new List<bool>();
        first.DuringPrepared = load =>
        {
            stages.Add("PreparedA");
            callbackResults.Add(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, world, session, out _));
        };
        first.DuringLoaded = load =>
        {
            stages.Add("LoadedA");
            callbackResults.Add(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, world, session, out _));
            callbackCreatedResults.Add(extender.Registry.TryAuthorizeCriticalCreatedWorld(first.PreparedSource, new object(), 79, out _));
        };
        second.DuringLoaded = load =>
        {
            stages.Add("LoadedB");
            callbackResults.Add(extender.Registry.TryValidateCriticalWorld(second.PreparedSource, world, session, out _));
            callbackCreatedResults.Add(extender.Registry.TryAuthorizeCriticalCreatedWorld(second.PreparedSource, new object(), 79, out _));
        };
        // B has no delivered record when A's Prepared callback probes the gate.
        // Only completion of both NoRecord callbacks can establish the pair's absence.
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "world-gate.sav", session);
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        CollectionAssert.AreEqual(new[] { "PreparedA", "LoadedA", "LoadedB" }, stages);
        foreach (bool result in callbackResults) Assert.IsFalse(result, "No callback may publish a partially finalized cohort.");
        Assert.AreEqual(2, callbackCreatedResults.Count);
        foreach (bool result in callbackCreatedResults) Assert.IsFalse(result, "A running Loaded callback cannot borrow created-world authority.");
        Assert.IsTrue(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, world, session, out reason), reason);
        Assert.IsTrue(extender.Registry.TryValidateCriticalWorld(second.PreparedSource, world, session, out reason), reason);
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, world, 0, out _));
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, world, session + 1, out _));
        byte[] protectedFirst;
        using (var frame = extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            protectedFirst = first.Mod.SaveCallback(CriticalLogicalContext);
            Assert.IsNotNull(second.Mod.SaveCallback(CriticalLogicalContext));
            Assert.IsTrue(frame.TryGetWrittenCapture(out _, out reason), reason);
        }

        session = 78;
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "mixed-world-gate.sav", session);
        first.Mod.Load(protectedFirst);
        if (legacyPartner)
            second.Mod.Load(SaveWith(2, 2, new[] { "value" },
                new Dictionary<string, byte[]> { ["value"] = new byte[] { 7 } }));
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        Assert.AreEqual(6, stages.Count); Assert.AreEqual(6, callbackResults.Count);
        foreach (bool result in callbackResults) Assert.IsFalse(result);
        Assert.AreEqual(4, callbackCreatedResults.Count);
        foreach (bool result in callbackCreatedResults) Assert.IsFalse(result);
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, world, session, out _));
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(second.PreparedSource, world, session, out _));
        Assert.AreEqual(1, first.Captures); Assert.AreEqual(1, second.Captures);

        // A separately authorized new blank world has its own exact world/session;
        // it does not rehabilitate this rejected archive or the previous identity.
        object createdWorld = new object();
        Assert.IsTrue(extender.Registry.TryAuthorizeCriticalCreatedWorld(first.PreparedSource, createdWorld, 79, out reason), reason);
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, createdWorld, 79, out _),
            "The other participant still belongs to the rejected archive.");
        Assert.IsTrue(extender.Registry.TryAuthorizeCriticalCreatedWorld(second.PreparedSource, createdWorld, 79, out reason), reason);
        Assert.IsTrue(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, createdWorld, 79, out reason), reason);
        Assert.IsTrue(extender.Registry.TryValidateCriticalWorld(second.PreparedSource, createdWorld, 79, out reason), reason);
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, world, 79, out _));
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(first.PreparedSource, createdWorld, session, out _));
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(5)]
    public void CriticalWriterUpgradesMarkerlessReaderTwoRecordsWithoutLosingFutureEntries(int recordVersion)
    {
        var fixture = new CriticalSourceFixture("Critical.Writer.MarkerlessFuture");
        Assert.IsTrue(fixture.Extender.Registry.TryJoinCriticalWriterCohort(fixture.Enrollment, out string reason), reason);
        byte[] original = SaveWith(recordVersion, 2, new[] { "value", "future" },
            new Dictionary<string, byte[]> { ["value"] = new byte[] { 1 }, ["future"] = new byte[] { 7 } });
        fixture.Value = 9;
        fixture.Load(original);
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, fixture.Mod.Loads[0].Status);
        Assert.IsFalse(fixture.Mod.Handle.KeepsLoadedRecord);
        fixture.Extender.Registry.RequireCriticalWriterReady();
        using (var frame = fixture.Extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
        {
            frame.CompleteOwnedPreparation();
            frame.Capture(CriticalLogicalContext);
            Assert.IsNull(frame.Failure);
            byte[] bytes = fixture.Mod.SaveCallback(CriticalLogicalContext);
            Assert.AreEqual(1, fixture.Captures);
            Assert.IsTrue(frame.TryGetWrittenCapture(out var capture, out reason), reason);
            Assert.IsTrue(capture.MatchesRecord(fixture.Enrollment, bytes, out reason), reason);
            var decoded = VersionedSaveRecordCodec.Decode(bytes, 3);
            Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.Valid, decoded.Status);
            Assert.AreEqual(3, decoded.RecordVersion); Assert.AreEqual(3, decoded.MinimumReaderVersion);
            Assert.IsTrue(decoded.Entries.TryGet("value", out byte[] value)); CollectionAssert.AreEqual(new byte[] { 9 }, value);
            Assert.IsTrue(decoded.Entries.TryGet("future", out byte[] future)); CollectionAssert.AreEqual(new byte[] { 7 }, future);
            Assert.IsTrue(decoded.Entries.TryGet("cutover", out byte[] marker)); Assert.IsTrue(marker.Length > 0);
            Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.TooNew, VersionedSaveRecordCodec.Decode(bytes, 2).Status);
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    public void OwnedFactoryPreparationRefusesEarlyDispatchAndCompletionRetainsItsExactFailure()
    {
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
        int preparations = 0, continuation = 0;
        using (var frame = VersionedSaveFactoryFrame.BeginOwnedPreparation(context => { preparations++; return null; }))
        {
            frame.Capture(CriticalLogicalContext);
            Assert.AreEqual(0, preparations);
            Assert.IsInstanceOfType<InvalidOperationException>(frame.Failure);
            Exception primary = frame.Failure;
            var rejected = Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                frame.CompleteOwnedPreparation(); continuation++;
            });
            Assert.AreSame(primary, rejected); Assert.AreSame(primary, frame.Failure);
            Assert.AreEqual(0, continuation);
            frame.Capture(CriticalLogicalContext);
            Assert.AreEqual(0, preparations); Assert.AreSame(primary, frame.Failure);
            Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }
}
