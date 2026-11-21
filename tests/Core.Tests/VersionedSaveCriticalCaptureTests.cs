using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Reflection;
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
        Assert.IsTrue(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, world, 21,
            out var createdOrigin, out reason), reason);
        Assert.IsTrue(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, world, 21,
            out var repeatedOrigin, out reason), reason);
        Assert.AreSame(createdOrigin, repeatedOrigin, "Idempotent acknowledgement returns the original exact token.");
        Assert.IsTrue(fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.PreparedSource,
            null, createdOrigin, world, 21, out var initialLease, out reason), reason);
        initialLease.Dispose();
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
            Assert.IsFalse(fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.PreparedSource,
                null, createdOrigin, world, 21, out var revokedLease, out _));
            Assert.IsNull(revokedLease);
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
        if (unload)
        {
            // A trusted creator may acknowledge a later world. Even equal world/session
            // values do not make the retained, revoked token current again.
            Assert.IsTrue(fixture.Extender.Registry.TryAuthorizeCriticalCreatedWorld(fixture.PreparedSource, world, 21,
                out var replacementOrigin, out reason), reason);
            Assert.AreNotSame(createdOrigin, replacementOrigin);
            Assert.IsFalse(fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.PreparedSource,
                null, createdOrigin, world, 21, out var staleLease, out _));
            Assert.IsNull(staleLease);
            Assert.IsTrue(fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.PreparedSource,
                null, replacementOrigin, world, 21, out var replacementLease, out reason), reason);
            replacementLease.Dispose();
        }
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

    private sealed class CriticalParticipantFixture
    {
        internal readonly FakeExtender Extender;
        internal readonly FakeExtender.Mod Mod;
        internal readonly string Owner;
        internal CriticalSaveParticipant Participant;
        internal VersionedSavePreparedOrigin Origin;
        internal Action DuringCapture;
        internal int Captures;

        internal CriticalParticipantFixture(string owner, string identifier, FakeExtender extender = null,
            Action<VersionedSaveDataRegistration> configure = null)
        {
            Owner = owner; Extender = extender ?? new FakeExtender();
            var registration = new VersionedSaveDataRegistration(owner, identifier, 3, 1, new[] { "value", "cutover" }, writer =>
            {
                Captures++; DuringCapture?.Invoke(); writer.Write("value", new byte[] { 1 }); return true;
            }, ignored => { })
            {
                Scope = VersionedSaveScope.SaveGamesAndMaps,
                SimulationRelevant = true,
                Prepared = load => Participant?.PreparedSource.TryGetCurrent(load, out Origin, out _)
            };
            configure?.Invoke(registration);
            Mod = Extender.Register(registration);
        }

        internal void Enroll(CriticalSaveOptions options)
        {
            Assert.IsTrue(Extender.Registry.TryEnrollCriticalParticipant(Owner, Mod.Handle, options, out Participant, out string reason), reason);
        }

        internal void LoadAbsent()
        {
            Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "participants.sav", 77);
            Extender.Registry.PrepareLoad(); Extender.Registry.EndLoad();
        }
    }

    private static CriticalSaveOptions CriticalParticipantOptions(bool storage = false) => new("cutover", 3,
        (VersionedSaveContext context, ref object state) => true, (context, state) => { }) { RequiresNativeStorage = storage };

    [TestMethod]
    [DataRow("foreign-owner")]
    [DataRow("foreign-handle")]
    [DataRow("stale-handle")]
    [DataRow("marker")]
    [DataRow("reader-low")]
    [DataRow("reader-high")]
    [DataRow("scope")]
    [DataRow("simulation")]
    [DataRow("prepared")]
    [DataRow("modes-none")]
    [DataRow("modes-unknown")]
    [DataRow("prepare-null")]
    [DataRow("release-null")]
    [DataRow("options-null")]
    public void CriticalParticipantInvalidEnrollmentLeavesTheOrdinaryRecordAndDemandUntouched(string invalid)
    {
        const string owner = "unrelated.mod.arbitrary-guid";
        var fixture = new CriticalParticipantFixture(owner, "Participant.Invalid", configure: registration =>
        {
            if (invalid == "scope") registration.Scope = VersionedSaveScope.SaveGames;
            if (invalid == "simulation") registration.SimulationRelevant = false;
            if (invalid == "prepared") registration.Prepared = null;
        });
        VersionedSaveDataHandle handle = fixture.Mod.Handle;
        if (invalid == "foreign-handle") handle = new CriticalParticipantFixture(owner, "Participant.Foreign").Mod.Handle;
        if (invalid == "stale-handle") handle = new VersionedSaveDataHandle(owner, fixture.Mod.Identifier);
        var options = new CriticalSaveOptions(invalid == "marker" ? "unknown" : "cutover",
            invalid == "reader-low" ? 0 : invalid == "reader-high" ? 4 : 3,
            invalid == "prepare-null" ? null : (VersionedSaveContext context, ref object state) => true,
            invalid == "release-null" ? null : (context, state) => { });
        if (invalid == "modes-none") options.Modes = CriticalSaveModes.None;
        if (invalid == "modes-unknown") options.Modes = (CriticalSaveModes)2;
        if (invalid == "options-null") options = null;
        string caller = invalid == "foreign-owner" ? "another.mod" : owner;
        Assert.IsFalse(fixture.Extender.Registry.TryPrepareCriticalParticipant(caller, handle, options, out var plan, out string reason));
        Assert.IsNull(plan); Assert.IsFalse(string.IsNullOrEmpty(reason));
        Assert.IsFalse(fixture.Extender.Registry.TryCommitCriticalParticipant(plan, out var rejected, out _));
        Assert.IsNull(rejected);
        Assert.IsFalse(fixture.Extender.Registry.TryEnrollCriticalParticipant(caller, handle, options, out rejected, out _));
        Assert.IsNull(rejected); Assert.IsFalse(fixture.Extender.Registry.HasCriticalWriterDemand);
        Assert.IsNull(fixture.Extender.Registry.BeginParticipantPreparation(CriticalLogicalContext));
        Assert.AreEqual(1, fixture.Extender.Handlers.Count); Assert.AreEqual(0, fixture.Captures);
        CollectionAssert.AreEqual(SaveWith(3, 1, new[] { "value" }, new Dictionary<string, byte[]> { ["value"] = new byte[] { 1 } }),
            fixture.Mod.SaveCallback(CriticalLogicalContext));
        Assert.AreEqual(1, fixture.Captures);
    }

    [TestMethod]
    public void CriticalParticipantTwoPhaseCommitSnapshotsOptionsAndPublishesWithoutTransportReregistration()
    {
        var fixture = new CriticalParticipantFixture("foreign.owner.example", "Participant.Snapshot");
        int prepares = 0, releases = 0;
        object expected = new object(), released = null;
        var options = new CriticalSaveOptions("cutover", 3,
            (VersionedSaveContext context, ref object state) => { prepares++; state = expected; return true; },
            (context, state) => { releases++; released = state; });
        Assert.IsTrue(fixture.Extender.Registry.TryPrepareCriticalParticipant(fixture.Owner, fixture.Mod.Handle, options, out var plan, out string reason), reason);
        Assert.IsFalse(fixture.Extender.Registry.HasCriticalWriterDemand);
        options.Modes = CriticalSaveModes.None; options.RequiresNativeStorage = true;
        var foreign = new FakeExtender();
        Assert.IsFalse(foreign.Registry.TryCommitCriticalParticipant(plan, out _, out _));
        Assert.IsFalse(foreign.Registry.HasCriticalWriterDemand);
        Assert.IsTrue(fixture.Extender.Registry.TryCommitCriticalParticipant(plan, out fixture.Participant, out reason), reason);
        Assert.AreEqual(fixture.Owner, fixture.Participant.OwnerGuid);
        Assert.AreEqual(fixture.Mod.Identifier, fixture.Participant.SaveDataIdentifier);
        Assert.AreSame(fixture.Mod.Handle, fixture.Participant.Handle);
        Assert.IsFalse(fixture.Participant.RequiresNativeStorage, "The caller's later option mutation cannot introduce a storage requirement.");
        Assert.IsTrue(fixture.Extender.Registry.HasCriticalWriterDemand);
        Assert.AreEqual(1, fixture.Extender.Handlers.Count);
        Assert.IsFalse(fixture.Extender.Registry.TryCommitCriticalParticipant(plan, out _, out _));
        Assert.IsFalse(fixture.Extender.Registry.TryEnrollCriticalParticipant(fixture.Owner, fixture.Mod.Handle, CriticalParticipantOptions(), out _, out _));
        fixture.LoadAbsent();
        var preparation = fixture.Extender.Registry.BeginParticipantPreparation(CriticalLogicalContext);
        Assert.IsNotNull(preparation);
        try { Assert.IsTrue(preparation.Prepare()); preparation.RequireCurrent(); }
        finally { preparation.Dispose(); }
        Assert.AreEqual(1, prepares); Assert.AreEqual(1, releases); Assert.AreSame(expected, released);
        Assert.AreEqual(0, fixture.Captures);
    }

    [TestMethod]
    public void CriticalParticipantStalePlanRefusesWithoutPublishingAndAFreshPlanCanStillCommit()
    {
        var fixture = new CriticalParticipantFixture("third.party.guid", "Participant.Stale");
        Assert.IsTrue(fixture.Extender.Registry.TryPrepareCriticalParticipant(fixture.Owner, fixture.Mod.Handle,
            CriticalParticipantOptions(), out var stale, out string reason), reason);
        _ = new CriticalParticipantFixture("other.owner", "Participant.Other", fixture.Extender);
        Assert.IsFalse(fixture.Extender.Registry.TryCommitCriticalParticipant(stale, out var rejected, out _));
        Assert.IsNull(rejected); Assert.IsFalse(fixture.Extender.Registry.HasCriticalWriterDemand);
        Assert.AreEqual(0, fixture.Captures); Assert.AreEqual(2, fixture.Extender.Handlers.Count);
        fixture.Enroll(CriticalParticipantOptions());
        Assert.IsTrue(fixture.Extender.Registry.HasCriticalWriterDemand);
        Assert.AreEqual(2, fixture.Extender.Handlers.Count);
    }

    [TestMethod]
    public void CriticalParticipantEnrollmentCannotReenterOrFollowAnOrdinarySourceSaveAttempt()
    {
        var fixture = new CriticalParticipantFixture("arbitrary.producer", "Participant.SaveAttempt");
        bool accepted = true; string refusal = null;
        fixture.DuringCapture = () => accepted = fixture.Extender.Registry.TryEnrollCriticalParticipant(fixture.Owner,
            fixture.Mod.Handle, CriticalParticipantOptions(), out _, out refusal);
        byte[] bytes = fixture.Mod.SaveCallback(CriticalLogicalContext);
        Assert.IsFalse(accepted); Assert.IsFalse(string.IsNullOrEmpty(refusal));
        Assert.AreEqual(1, fixture.Captures); Assert.IsFalse(fixture.Extender.Registry.HasCriticalWriterDemand);
        CollectionAssert.AreEqual(SaveWith(3, 1, new[] { "value" }, new Dictionary<string, byte[]> { ["value"] = new byte[] { 1 } }), bytes);
        Assert.IsFalse(fixture.Extender.Registry.TryPrepareCriticalParticipant(fixture.Owner, fixture.Mod.Handle,
            CriticalParticipantOptions(), out _, out _));
        Assert.IsFalse(fixture.Extender.Registry.TryEnrollCriticalParticipant(fixture.Owner, fixture.Mod.Handle,
            CriticalParticipantOptions(), out _, out _));
        Assert.AreEqual(1, fixture.Extender.Handlers.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CriticalParticipantStorageRequirementCannotCertifyASmallerCohort(bool storageJoined)
    {
        var fixture = new CriticalParticipantFixture("independent.storage.consumer", "Participant.NeedsStorage");
        var storage = new CriticalSourceFixture("framework.storage", fixture.Extender);
        fixture.Enroll(CriticalParticipantOptions(true));
        Assert.IsTrue(fixture.Participant.RequiresNativeStorage);
        if (storageJoined)
            Assert.IsTrue(fixture.Extender.Registry.TryJoinNativeStorageCriticalSource(storage.Enrollment, out string joinedReason), joinedReason);
        fixture.LoadAbsent();
        if (!storageJoined)
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Extender.Registry.RequireCriticalWriterReady());
        else fixture.Extender.Registry.RequireCriticalWriterReady();
        using (var preparation = fixture.Extender.Registry.BeginParticipantPreparation(CriticalLogicalContext))
        {
            Assert.IsNotNull(preparation); Assert.IsTrue(preparation.Prepare());
            if (!storageJoined)
                Assert.ThrowsExactly<InvalidOperationException>(() => preparation.RequireCurrent());
            else preparation.RequireCurrent();
            using (var frame = fixture.Extender.Registry.BeginCriticalWriterFrame(CriticalLogicalContext))
            {
                frame.CompleteOwnedPreparation(); frame.Capture(CriticalLogicalContext);
                if (!storageJoined)
                {
                    Assert.IsNotNull(frame.Failure); Assert.IsFalse(frame.TryGetWrittenCapture(out _, out _));
                    Assert.AreEqual(0, fixture.Captures); Assert.AreEqual(0, storage.Captures);
                }
                else
                {
                    Assert.IsNull(frame.Failure);
                    Assert.IsNotNull(fixture.Mod.SaveCallback(CriticalLogicalContext));
                    Assert.IsNotNull(storage.Mod.SaveCallback(CriticalLogicalContext));
                    Assert.IsTrue(frame.TryGetWrittenCapture(out _, out string reason), reason);
                }
            }
        }
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void CriticalParticipantPreparationPairsEveryEnteredStateAndReleasesInReverseDespiteFailure(int outcome)
    {
        var extender = new FakeExtender();
        var zulu = new CriticalParticipantFixture("z.participant", "Zulu", extender);
        var alpha = new CriticalParticipantFixture("a.participant", "Alpha", extender);
        var middle = new CriticalParticipantFixture("m.participant", "Middle", extender);
        var events = new List<string>();
        var contexts = new List<VersionedSaveContext>();
        var released = new List<KeyValuePair<string, object>>();
        object alphaState = new object(), zuluState = new object(), middleState = outcome == 0 ? null : new object();
        var primary = new InvalidOperationException("controlled participant preparation failure");
        var cleanup = new InvalidOperationException("controlled middle release failure");
        var laterCleanup = new InvalidOperationException("controlled last release failure");
        zulu.Enroll(new CriticalSaveOptions("cutover", 3,
            (VersionedSaveContext context, ref object state) => { events.Add("PrepareZ"); contexts.Add(context); state = zuluState; return true; },
            (context, state) => { events.Add("ReleaseZ"); contexts.Add(context); released.Add(new("Z", state)); }));
        alpha.Enroll(new CriticalSaveOptions("cutover", 3,
            (VersionedSaveContext context, ref object state) => { events.Add("PrepareA"); contexts.Add(context); state = alphaState; return true; },
            (context, state) => { events.Add("ReleaseA"); contexts.Add(context); released.Add(new("A", state)); throw laterCleanup; }));
        middle.Enroll(new CriticalSaveOptions("cutover", 3,
            (VersionedSaveContext context, ref object state) =>
            {
                events.Add("PrepareM"); contexts.Add(context); state = middleState;
                if (outcome == 2) throw primary;
                return outcome != 1;
            },
            (context, state) => { events.Add("ReleaseM"); contexts.Add(context); released.Add(new("M", state)); throw cleanup; }));
        alpha.LoadAbsent();
        var preparation = extender.Registry.BeginParticipantPreparation(CriticalLogicalContext);
        Assert.IsNotNull(preparation);
        bool ready = false; Exception actual = null, currentFailure = null, releaseFailure = null;
        try { ready = preparation.Prepare(); } catch (Exception error) { actual = error; }
        try { preparation.RequireCurrent(); } catch (Exception error) { currentFailure = error; }
        try { preparation.Dispose(); } catch (Exception error) { releaseFailure = error; }
        preparation.Dispose();
        Assert.AreEqual(outcome == 0, ready);
        if (outcome == 2) Assert.AreSame(primary, actual); else Assert.IsNull(actual);
        if (outcome == 0) Assert.IsNull(currentFailure);
        else Assert.IsInstanceOfType<InvalidOperationException>(currentFailure);
        Assert.AreSame(cleanup, releaseFailure);
        CollectionAssert.AreEqual(outcome == 0 ? new[] { "PrepareA", "PrepareM", "PrepareZ", "ReleaseZ", "ReleaseM", "ReleaseA" } :
            new[] { "PrepareA", "PrepareM", "ReleaseM", "ReleaseA" }, events);
        Assert.AreEqual(outcome == 0 ? 3 : 2, released.Count);
        Assert.AreEqual("A", released[released.Count - 1].Key); Assert.AreSame(alphaState, released[released.Count - 1].Value);
        int middleIndex = outcome == 0 ? 1 : 0;
        Assert.AreEqual("M", released[middleIndex].Key); Assert.AreSame(middleState, released[middleIndex].Value);
        if (outcome == 0) Assert.IsNull(released[middleIndex].Value);
        if (outcome == 0) Assert.AreSame(zuluState, released[0].Value);
        foreach (var context in contexts)
        {
            Assert.AreEqual(CriticalLogicalContext.IsSaveFile, context.IsSaveFile);
            Assert.AreEqual(CriticalLogicalContext.IsMapEditorSave, context.IsMapEditorSave);
            Assert.AreEqual(CriticalLogicalContext.FilePath, context.FilePath);
        }
        if (outcome == 2) Assert.AreSame(primary, actual, "Release failure must not replace the owner's retained preparation failure.");
    }

    [TestMethod]
    public void CriticalParticipantPreparationCannotRemainCurrentAfterItsCallbackReentersAnArchiveLoad()
    {
        var fixture = new CriticalParticipantFixture("reentrant.producer", "Participant.Reentrant");
        object expected = new object(), released = null; int releases = 0;
        fixture.Enroll(new CriticalSaveOptions("cutover", 3,
            (VersionedSaveContext context, ref object state) =>
            {
                state = expected; fixture.Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "replacement.sav", 78); return true;
            }, (context, state) => { released = state; releases++; }));
        fixture.LoadAbsent();
        var preparation = fixture.Extender.Registry.BeginParticipantPreparation(CriticalLogicalContext);
        Assert.IsNotNull(preparation);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => preparation.RequireCurrent());
            Assert.IsTrue(preparation.Prepare());
            Assert.ThrowsExactly<InvalidOperationException>(() => preparation.RequireCurrent());
        }
        finally
        {
            preparation.Dispose();
            fixture.Extender.Registry.PrepareLoad(); fixture.Extender.Registry.EndLoad();
        }
        preparation.Dispose();
        Assert.AreEqual(1, releases); Assert.AreSame(expected, released); Assert.AreEqual(0, fixture.Captures);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CriticalParticipantCallbacksRunOutsideTheRegistryLock(bool probeDuringRelease)
    {
        var fixture = new CriticalParticipantFixture("lock.probe.owner", "Participant.LockProbe");
        var finished = new ManualResetEvent(false);
        Thread worker = null; Exception workerFailure = null;
        bool accepted = true, completedInsideCallback = false, joined = true;
        Action probe = () =>
        {
            worker = new Thread(() =>
            {
                try { accepted = fixture.Extender.Registry.TryPrepareCriticalParticipant("another.owner", fixture.Mod.Handle,
                    CriticalParticipantOptions(), out _, out _); }
                catch (Exception error) { workerFailure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true };
            worker.Start(); completedInsideCallback = finished.WaitOne(1000);
        };
        fixture.Enroll(new CriticalSaveOptions("cutover", 3,
            (VersionedSaveContext context, ref object state) => { if (!probeDuringRelease) probe(); return true; },
            (context, state) => { if (probeDuringRelease) probe(); }));
        fixture.LoadAbsent();
        try
        {
            var preparation = fixture.Extender.Registry.BeginParticipantPreparation(CriticalLogicalContext);
            Assert.IsNotNull(preparation);
            try { Assert.IsTrue(preparation.Prepare()); preparation.RequireCurrent(); }
            finally { preparation.Dispose(); }
        }
        finally
        {
            if (worker != null) joined = worker.Join(2000);
            if (joined) finished.Dispose();
        }
        Assert.IsTrue(joined); Assert.IsNull(workerFailure); Assert.IsFalse(accepted);
        Assert.IsTrue(completedInsideCallback, "A bounded independent registry query must complete while this callback is running.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CriticalGenerationObserverIsOwnedAndRevokesOriginsOutsideTheRegistryLock(bool probeOnUnload)
    {
        var fixture = new CriticalSourceFixture("Critical.GenerationObserver");
        var observed = new List<long>();
        var finished = new ManualResetEvent(false);
        Thread worker = null; Exception workerFailure = null;
        bool acceptedFromWorker = true, completedInsideCallback = false, joined = true;
        Action<long> observer = null;
        observer = generation =>
        {
            observed.Add(generation);
            // The actual generation owner sees BeginLoad's open window. Its
            // nested unload belongs to that load and must not advance it again.
            if (observed.Count == 1) fixture.Extender.Registry.Unload();
            if (observed.Count != (probeOnUnload ? 2 : 1)) return;
            worker = new Thread(() =>
            {
                try { acceptedFromWorker = fixture.Extender.Registry.TryObserveGeneration(observer, out _); }
                catch (Exception error) { workerFailure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true };
            worker.Start(); completedInsideCallback = finished.WaitOne(1000);
        };
        Assert.IsFalse(fixture.Extender.Registry.TryObserveGeneration(null, out _));
        Assert.IsTrue(fixture.Extender.Registry.TryObserveGeneration(observer, out string reason), reason);
        Assert.IsTrue(fixture.Extender.Registry.TryObserveGeneration(observer, out reason), reason);
        Assert.IsFalse(fixture.Extender.Registry.TryObserveGeneration(ignored => { }, out _));
        Assert.AreEqual(0, observed.Count, "Startup binding must not replay an invented archive generation.");
        try
        {
            fixture.Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "observed.sav", 91);
            Assert.AreEqual(1, observed.Count); Assert.IsTrue(observed[0] > 0);
            fixture.Extender.Registry.PrepareLoad(); fixture.Extender.Registry.EndLoad();
            Assert.IsNull(fixture.OriginError); Assert.IsNotNull(fixture.Origin);
            VersionedSavePreparedOrigin previous = fixture.Origin;
            Assert.IsTrue(fixture.PreparedSource.TryValidateCompleted(previous, out reason), reason);
            fixture.Extender.Registry.Unload();
            Assert.AreEqual(2, observed.Count); Assert.IsTrue(observed[1] > observed[0]);
            Assert.IsFalse(fixture.PreparedSource.TryValidateCompleted(previous, out _));
            Assert.IsFalse(fixture.Extender.Registry.TryReadCriticalPreparedMarker(fixture.Enrollment, previous, out _, out _, out _));
            Assert.IsFalse(fixture.Extender.Registry.TryObserveGeneration(observer, out _),
                "Even the same delegate cannot reopen startup binding after a source generation began.");
        }
        finally
        {
            if (worker != null) joined = worker.Join(2000);
            if (joined) finished.Dispose();
        }
        Assert.IsTrue(joined); Assert.IsNull(workerFailure); Assert.IsFalse(acceptedFromWorker);
        Assert.IsTrue(completedInsideCallback,
            "The independent bounded generation query must finish while BeginLoad/Unload notifies its owner.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CriticalImportCommitLeaseBlocksGenerationReplacementUntilPublicationFinishes(bool worldPublication)
    {
        var fixture = new CriticalParticipantFixture("atomic.import.owner", "Participant.AtomicImport");
        var storage = new CriticalSourceFixture("framework.atomic.storage", fixture.Extender);
        fixture.Enroll(CriticalParticipantOptions(true));
        Assert.IsTrue(fixture.Extender.Registry.TryJoinNativeStorageCriticalSource(storage.Enrollment, out string reason), reason);
        var started = new ManualResetEvent(false);
        var notified = new ManualResetEvent(false);
        var finished = new ManualResetEvent(false);
        int notifications = 0;
        long observedGeneration = 0;
        Assert.IsTrue(fixture.Extender.Registry.TryObserveGeneration(generation =>
        {
            Interlocked.Exchange(ref observedGeneration, generation);
            if (Interlocked.Increment(ref notifications) > 1) notified.Set();
        }, out reason), reason);
        fixture.LoadAbsent();
        VersionedSavePreparedOrigin previous = fixture.Origin;
        Assert.IsNotNull(previous); Assert.AreEqual(1, Volatile.Read(ref notifications));
        long originalGeneration = Interlocked.Read(ref observedGeneration);
        object world = new object();
        long replacementSession = worldPublication ? 77 : 78;
        CriticalImportCommitLease lease;
        bool acquired = worldPublication
            ? fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.Participant.PreparedSource,
                previous, null, world, 77, out lease, out reason)
            : fixture.Extender.Registry.TryAcquireCriticalImportCommit(fixture.Participant.PreparedSource,
                previous, storage.PreparedSource, 77, out lease, out reason);
        Assert.IsTrue(acquired, reason); Assert.IsNotNull(lease);
        Exception workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                started.Set();
                fixture.Extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "replacement.sav", replacementSession);
                fixture.Extender.Registry.PrepareLoad(); fixture.Extender.Registry.EndLoad();
            }
            catch (Exception error) { workerFailure = error; }
            finally { finished.Set(); }
        }) { IsBackground = true };
        bool entered = false, blockedOnMonitor = false, finishedWhileHeld = true, notifiedWhileHeld = true;
        bool joined = true; long generationWhileHeld = -1;
        try
        {
            worker.Start(); entered = started.WaitOne(1000);
            // This worker has no wait between its entry signal and BeginLoad.
            // WaitSleepJoin therefore observes the real contended registry monitor,
            // rather than treating an unscheduled thread as evidence of exclusion.
            blockedOnMonitor = SpinWait.SpinUntil(() =>
                (worker.ThreadState & ThreadState.WaitSleepJoin) != 0 || finished.WaitOne(0), 1000);
            blockedOnMonitor &= (worker.ThreadState & ThreadState.WaitSleepJoin) != 0;
            finishedWhileHeld = finished.WaitOne(0); notifiedWhileHeld = notified.WaitOne(0);
            generationWhileHeld = Interlocked.Read(ref observedGeneration);
        }
        finally
        {
            lease.Dispose();
            joined = worker.Join(3000);
            if (joined) { started.Dispose(); notified.Dispose(); finished.Dispose(); }
        }
        Assert.IsTrue(entered); Assert.IsTrue(blockedOnMonitor);
        Assert.IsFalse(finishedWhileHeld); Assert.IsFalse(notifiedWhileHeld);
        Assert.AreEqual(originalGeneration, generationWhileHeld);
        Assert.IsTrue(joined); Assert.IsNull(workerFailure);
        Assert.AreEqual(2, Volatile.Read(ref notifications));
        Assert.IsTrue(Interlocked.Read(ref observedGeneration) > originalGeneration);
        Assert.IsFalse(fixture.Participant.PreparedSource.TryValidateCompleted(previous, out _));
        CriticalImportCommitLease stale, current;
        bool staleAccepted = worldPublication
            ? fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.Participant.PreparedSource,
                previous, null, world, replacementSession, out stale, out _)
            : fixture.Extender.Registry.TryAcquireCriticalImportCommit(fixture.Participant.PreparedSource,
                previous, storage.PreparedSource, replacementSession, out stale, out _);
        Assert.IsFalse(staleAccepted); Assert.IsNull(stale);
        if (worldPublication)
        {
            // Native capacity can repeatedly ask about an unready retained origin.
            // Warm and measure the actual rejection entry, without assertions,
            // formatting or callbacks inside the measured region.
            for (int i = 0; i < 256; i++)
                if (fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.Participant.PreparedSource,
                    previous, null, world, replacementSession, out var warmLease, out _)) warmLease.Dispose();
            bool unexpected = false;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++)
                if (fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.Participant.PreparedSource,
                    previous, null, world, replacementSession, out var rejectedLease, out _))
                { unexpected = true; rejectedLease.Dispose(); }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.IsFalse(unexpected); Assert.AreEqual(0L, allocated);
        }
        // The world-publication row reuses mission 77 deliberately. Only the actual
        // replacement origin may publish; an equal session cannot revive the old token.
        bool currentAccepted = worldPublication
            ? fixture.Extender.Registry.TryAcquireCriticalWorldPublication(fixture.Participant.PreparedSource,
                fixture.Origin, null, world, replacementSession, out current, out reason)
            : fixture.Extender.Registry.TryAcquireCriticalImportCommit(fixture.Participant.PreparedSource,
                fixture.Origin, storage.PreparedSource, replacementSession, out current, out reason);
        Assert.IsTrue(currentAccepted, reason); current.Dispose();
    }

    private static void FaultCriticalPreparedSession(VersionedSaveDataRegistry registry, VersionedSaveDataHandle handle, long session)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        // A real archive window assigns one ID to every outcome. Deliberately
        // replace only this actual slot's immutable load metadata to exercise
        // the all-member guard against an otherwise unreachable inconsistent pair.
        // No native world, Prepared receiver or separate registry model is forged.
        var slots = (Array)typeof(VersionedSaveDataRegistry).GetField("slots", fields).GetValue(registry);
        foreach (object slot in slots)
        {
            if (!ReferenceEquals(slot.GetType().GetField("Handle", fields).GetValue(slot), handle)) continue;
            object outcome = slot.GetType().GetField("PreparedOutcome", fields).GetValue(slot);
            FieldInfo loadField = outcome.GetType().GetField("Load", fields);
            var load = (VersionedSaveLoad)loadField.GetValue(outcome);
            var replacement = new VersionedSaveLoad(load.Status, load.Source, load.FilePath, load.RecordVersion,
                load.MinimumReaderVersion, load.Entries, load.KeptEntryCount, load.Detail, load.PreparedOrigin, session);
            loadField.SetValue(outcome, replacement);
            return;
        }
        Assert.Fail("The exact registered slot must exist for this bounded metadata fault injection.");
    }

    [TestMethod]
    [DataRow(77L, 77L, true)]
    [DataRow(0L, 77L, false)]
    [DataRow(77L, 0L, false)]
    [DataRow(78L, 77L, false)]
    [DataRow(77L, 78L, false)]
    public void CriticalWriterWorldRequiresEveryStateOnlySourceToMatchTheActualPositiveSession(long firstSession, long secondSession, bool allowed)
    {
        var extender = new FakeExtender();
        var first = new CriticalParticipantFixture("state.only.owner.a", "StateOnly.First", extender);
        var second = new CriticalParticipantFixture("state.only.owner.b", "StateOnly.Second", extender);
        first.Enroll(CriticalParticipantOptions()); second.Enroll(CriticalParticipantOptions());
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "state-only.sav", 77);
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        if (firstSession != 77) FaultCriticalPreparedSession(extender.Registry, first.Mod.Handle, firstSession);
        if (secondSession != 77) FaultCriticalPreparedSession(extender.Registry, second.Mod.Handle, secondSession);
        // Readable completed origins retain their established metadata-only
        // meaning; the demanded native owner adds this exact world-session fence.
        extender.Registry.RequireCriticalWriterReady();
        Assert.IsTrue(first.Participant.PreparedSource.TryValidateCompleted(first.Origin, out string reason), reason);
        Assert.IsTrue(second.Participant.PreparedSource.TryValidateCompleted(second.Origin, out reason), reason);
        object world = new object();
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
        Assert.AreEqual(allowed, extender.Registry.TryValidateCriticalWorld(first.Participant.PreparedSource, world, 77, out reason), reason);
        Assert.AreEqual(allowed, extender.Registry.TryValidateCriticalWorld(second.Participant.PreparedSource, world, 77, out reason), reason);
        if (allowed) extender.Registry.RequireCriticalWriterWorld(world, 77);
        else Assert.ThrowsExactly<InvalidOperationException>(() => extender.Registry.RequireCriticalWriterWorld(world, 77));
        Assert.AreEqual(0, first.Captures); Assert.AreEqual(0, second.Captures);
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
        extender.Registry.RequireCriticalWriterReady();
    }

    [TestMethod]
    public void CriticalWriterWorldRejectsARealUnassociatedArchiveWindowBeforeCapture()
    {
        var extender = new FakeExtender();
        var first = new CriticalParticipantFixture("state.only.unowned.a", "Unassociated.First", extender);
        var second = new CriticalParticipantFixture("state.only.unowned.b", "Unassociated.Second", extender);
        first.Enroll(CriticalParticipantOptions()); second.Enroll(CriticalParticipantOptions());
        // This negative comes from the real registry window, without fault injection.
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "unassociated.sav", 0);
        extender.Registry.PrepareLoad(); extender.Registry.EndLoad();
        extender.Registry.RequireCriticalWriterReady();
        object world = new object();
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(first.Participant.PreparedSource, world, 77, out _));
        Assert.IsFalse(extender.Registry.TryValidateCriticalWorld(second.Participant.PreparedSource, world, 77, out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => extender.Registry.RequireCriticalWriterWorld(world, 77));
        Assert.ThrowsExactly<InvalidOperationException>(() => extender.Registry.RequireCriticalWriterWorld(world, 0));
        Assert.AreEqual(0, first.Captures); Assert.AreEqual(0, second.Captures);
        Assert.IsNull(VersionedSaveFactoryFrame.Current);
        extender.Registry.RequireCriticalWriterReady();
    }
}
