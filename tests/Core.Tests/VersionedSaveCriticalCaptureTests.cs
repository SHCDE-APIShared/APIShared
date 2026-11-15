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
}
