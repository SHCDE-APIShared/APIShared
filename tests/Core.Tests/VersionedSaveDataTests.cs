using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class VersionedSaveDataTests
{
    [TestMethod]
    public void RoundTripRestoresKnownEntriesAndRewritesIdenticalBytes()
    {
        var extender = new FakeExtender();
        var state = new Dictionary<string, byte[]> { ["rally"] = new byte[] { 9 }, ["garrison/17"] = new byte[] { 1, 2, 3 } };
        var mod = extender.Register("Mod.Garrison", 1, 1, new[] { "garrison/", "rally" }, Writes(state));
        byte[] saved = extender.Save(mod);
        Assert.AreEqual(VersionedSaveWriteStatus.Written, mod.Handle.LastWriteStatus);

        extender.LoadSave(saved, mod);
        VersionedSaveLoad load = mod.Loads.Single();
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, load.Status);
        Assert.AreEqual(VersionedSaveLoadSource.SaveGame, load.Source);
        Assert.AreEqual(1, load.RecordVersion);
        Assert.AreEqual(1, load.MinimumReaderVersion);
        Assert.AreEqual(0, load.KeptEntryCount);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, load.Entries["garrison/17"]);
        CollectionAssert.AreEqual(new byte[] { 9 }, load.Entries["rally"]);
        CollectionAssert.AreEqual(saved, extender.Save(mod), "The same state must give the same bytes.");
    }

    [TestMethod]
    public void RecordFormatIsPinned()
    {
        var extender = new FakeExtender();
        var mod = extender.Register("Mod.Pin", 3, 2, new[] { "ab" }, Writes(new() { ["ab"] = new byte[] { 0x10, 0x20 } }));
        byte[] saved = extender.Save(mod);
        var expected = new List<byte> { (byte)'A', (byte)'P', (byte)'S', (byte)'V', 1, 0, 1, 0, 32, 0, 0, 0,
            3, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 10, 0, 0, 0, 0, 0, 0, 0 };
        expected.AddRange(new byte[] { 2, 0, (byte)'a', (byte)'b', 2, 0, 0, 0, 0x10, 0x20 });
        byte[] bytes = expected.ToArray();
        uint crc = ReferenceCrc(bytes);
        bytes[28] = (byte)crc; bytes[29] = (byte)(crc >> 8); bytes[30] = (byte)(crc >> 16); bytes[31] = (byte)(crc >> 24);
        CollectionAssert.AreEqual(bytes, saved);
    }

    [TestMethod]
    public void UnknownEntriesSurviveAnOlderVersionAndReturnUnchanged()
    {
        var newer = new FakeExtender();
        var newState = new Dictionary<string, byte[]> { ["a"] = new byte[] { 1 }, ["b"] = new byte[] { 2 }, ["c"] = new byte[] { 3, 3 } };
        var writer3 = newer.Register("Mod.Data", 3, 2, new[] { "a", "b", "c" }, Writes(newState));
        byte[] fromNewer = newer.Save(writer3);

        var older = new FakeExtender();
        var oldState = new Dictionary<string, byte[]>();
        var reader2 = older.Register("Mod.Data", 2, 2, new[] { "a", "b" }, Writes(oldState));
        older.LoadSave(fromNewer, reader2);
        VersionedSaveLoad downgraded = reader2.Loads.Single();
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, downgraded.Status);
        Assert.AreEqual(3, downgraded.RecordVersion);
        Assert.AreEqual(2, downgraded.MinimumReaderVersion);
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, downgraded.Entries.Keys.ToArray());
        Assert.AreEqual(1, downgraded.KeptEntryCount);
        oldState["a"] = new byte[] { 10 };
        oldState["b"] = new byte[] { 20 };
        byte[] fromOlder = older.Save(reader2);

        var upgraded = new FakeExtender();
        var registration = upgraded.Registration("Mod.Data", 3, 2, new[] { "a", "b", "c" }, Writes(new()));
        registration.AddMigration(2, entries => { if (!entries.Contains("c")) entries.Set("c", new byte[] { 0 }); });
        var reader3 = upgraded.Register(registration);
        upgraded.LoadSave(fromOlder, reader3);
        VersionedSaveLoad again = reader3.Loads.Single();
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, again.Status);
        Assert.AreEqual(2, again.RecordVersion);
        CollectionAssert.AreEqual(new byte[] { 10 }, again.Entries["a"]);
        CollectionAssert.AreEqual(new byte[] { 20 }, again.Entries["b"]);
        CollectionAssert.AreEqual(new byte[] { 3, 3 }, again.Entries["c"], "The older version must carry the unknown entry back unchanged.");
    }

    [TestMethod]
    public void KnownEntriesLeftOutOfCaptureAreDeletedNotResurrected()
    {
        var extender = new FakeExtender();
        var state = new Dictionary<string, byte[]> { ["garrison/1"] = new byte[] { 1 }, ["garrison/2"] = new byte[] { 2 } };
        var mod = extender.Register("Mod.Garrison", 1, 1, new[] { "garrison/" }, Writes(state));
        extender.LoadSave(extender.Save(mod), mod);
        state.Remove("garrison/2");
        extender.LoadSave(extender.Save(mod), mod);
        CollectionAssert.AreEqual(new[] { "garrison/1" }, mod.Loads.Last().Entries.Keys.ToArray());
        Assert.AreEqual(0, mod.Loads.Last().KeptEntryCount);
    }

    [TestMethod]
    public void MigrationStepsRunInOrderFromTheRecordVersion()
    {
        byte[] version1 = SaveWith(1, 1, new[] { "old" }, new() { ["old"] = new byte[] { 5 } });
        byte[] version3 = SaveWith(3, 1, new[] { "new", "count" }, new() { ["new"] = new byte[] { 6 } });
        var extender = new FakeExtender();
        var steps = new List<int>();
        var registration = extender.Registration("Mod.Data", 4, 1, new[] { "new", "count", "extra" }, Writes(new()));
        registration.AddMigration(1, entries =>
        {
            steps.Add(1);
            entries.TryGet("old", out byte[] value);
            entries.Set("new", value);
            entries.Remove("old");
        });
        registration.AddMigration(3, entries => { steps.Add(3); entries.Set("extra", new byte[] { (byte)entries.Count }); });
        var mod = extender.Register(registration);

        extender.LoadSave(version1, mod);
        CollectionAssert.AreEqual(new[] { 1, 3 }, steps, "Step 2 has no change and is skipped.");
        VersionedSaveLoad migrated = mod.Loads.Last();
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, migrated.Status);
        Assert.AreEqual(1, migrated.RecordVersion);
        CollectionAssert.AreEquivalent(new[] { "new", "extra" }, migrated.Entries.Keys.ToArray());
        CollectionAssert.AreEqual(new byte[] { 5 }, migrated.Entries["new"]);

        steps.Clear();
        extender.LoadSave(version3, mod);
        CollectionAssert.AreEqual(new[] { 3 }, steps);
        CollectionAssert.AreEquivalent(new[] { "new", "extra" }, mod.Loads.Last().Entries.Keys.ToArray());
    }

    [TestMethod]
    public void FailedMigrationKeepsTheLoadedBytesWithoutCapturing()
    {
        byte[] version1 = SaveWith(1, 1, new[] { "a" }, new() { ["a"] = new byte[] { 1 } });
        var extender = new FakeExtender();
        int captures = 0;
        var registration = extender.Registration("Mod.Data", 2, 1, new[] { "a" }, _ => { captures++; return true; });
        registration.AddMigration(1, _ => throw new InvalidOperationException("broken step"));
        var mod = extender.Register(registration);
        extender.LoadSave(version1, mod);
        Assert.AreEqual(VersionedSaveLoadStatus.MigrationFailed, mod.Loads.Single().Status);
        Assert.AreEqual(0, mod.Loads.Single().Entries.Count);
        Assert.IsTrue(mod.Handle.KeepsLoadedRecord);
        CollectionAssert.AreEqual(version1, extender.Save(mod));
        Assert.AreEqual(VersionedSaveWriteStatus.KeptLoadedRecord, mod.Handle.LastWriteStatus);
        Assert.AreEqual(0, captures);
        Assert.AreEqual(1L, extender.Registry.CallbackFailures);
    }

    [TestMethod]
    public void DamagedRecordsAreCorruptAndReplacedByTheNextSave()
    {
        byte[] good = SaveWith(1, 1, new[] { "a" }, new() { ["a"] = new byte[] { 1, 2, 3, 4 } });
        byte[] flipped = (byte[])good.Clone(); flipped[^1] ^= 0x40;
        byte[] truncated = good.Take(good.Length - 2).ToArray();
        byte[] trailing = WithCrc(good.Concat(new byte[] { 0 }).ToArray(), payloadDelta: 1);
        byte[] shortHeader = good.Take(20).ToArray();
        foreach (byte[] damaged in new[] { flipped, truncated, trailing, shortHeader })
        {
            var extender = new FakeExtender();
            var mod = extender.Register("Mod.Data", 1, 1, new[] { "a" }, Writes(new() { ["a"] = new byte[] { 9 } }));
            extender.LoadSave(damaged, mod);
            Assert.AreEqual(VersionedSaveLoadStatus.Corrupt, mod.Loads.Single().Status);
            Assert.IsFalse(string.IsNullOrEmpty(mod.Loads.Single().Detail));
            Assert.IsFalse(mod.Handle.KeepsLoadedRecord);
            byte[] replaced = extender.Save(mod);
            Assert.AreEqual(VersionedSaveWriteStatus.Written, mod.Handle.LastWriteStatus);
            extender.LoadSave(replaced, mod);
            CollectionAssert.AreEqual(new byte[] { 9 }, mod.Loads.Last().Entries["a"]);
        }
    }

    [TestMethod]
    public void TooNewRecordsAreKeptByteForByte()
    {
        byte[] version3 = SaveWith(3, 3, new[] { "a" }, new() { ["a"] = new byte[] { 1 } });
        byte[] newerEnvelope = (byte[])version3.Clone();
        newerEnvelope[4] = 2; newerEnvelope[6] = 2;
        foreach (byte[] record in new[] { version3, newerEnvelope })
        {
            var extender = new FakeExtender();
            int captures = 0;
            var mod = extender.Register("Mod.Data", 2, 1, new[] { "a" }, _ => { captures++; return true; });
            extender.LoadSave(record, mod);
            Assert.AreEqual(VersionedSaveLoadStatus.TooNew, mod.Loads.Single().Status);
            CollectionAssert.AreEqual(record, extender.Save(mod));
            Assert.AreEqual(VersionedSaveWriteStatus.KeptLoadedRecord, mod.Handle.LastWriteStatus);
            Assert.AreEqual(0, captures);
            extender.Registry.Unload();
            Assert.IsFalse(mod.Handle.KeepsLoadedRecord, "Kept bytes belong to the loaded mission only.");
            Assert.AreEqual(32, extender.Save(mod).Length);
            Assert.AreEqual(1, captures);
        }
        var reader = new FakeExtender();
        var probe = reader.Register("Mod.Data", 2, 1, new[] { "a" }, _ => true);
        reader.LoadSave(version3, probe);
        Assert.AreEqual(3, probe.Loads.Single().RecordVersion);
        Assert.AreEqual(3, probe.Loads.Single().MinimumReaderVersion);
    }

    [TestMethod]
    public void UnversionedDataUsesTheLegacyReaderOrIsKept()
    {
        byte[] legacy = { (byte)'H', (byte)'D', (byte)'U', (byte)'P', 7 };
        var plain = new FakeExtender();
        var unconverted = plain.Register("Mod.Legacy", 2, 1, new[] { "value" }, _ => true);
        plain.LoadSave(legacy, unconverted);
        Assert.AreEqual(VersionedSaveLoadStatus.Unrecognized, unconverted.Loads.Single().Status);
        CollectionAssert.AreEqual(legacy, plain.Save(unconverted));

        var converting = new FakeExtender();
        var registration = converting.Registration("Mod.Legacy", 2, 1, new[] { "value", "added" }, _ => true);
        registration.LegacyReader = (byte[] data, VersionedSaveEntries entries, out int version) =>
        {
            version = 1;
            if (data.Length != 5 || data[0] != 'H') return false;
            entries.Set("value", new[] { data[4] });
            return true;
        };
        registration.AddMigration(1, entries => entries.Set("added", new byte[] { 1 }));
        var mod = converting.Register(registration);
        converting.LoadSave(legacy, mod);
        VersionedSaveLoad imported = mod.Loads.Single();
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, imported.Status);
        Assert.AreEqual(1, imported.RecordVersion);
        Assert.AreEqual(0, imported.MinimumReaderVersion);
        CollectionAssert.AreEqual(new byte[] { 7 }, imported.Entries["value"]);
        Assert.IsTrue(imported.Entries.ContainsKey("added"));
        Assert.IsTrue(VersionedSaveRecordCodec.HasSignature(converting.Save(mod)), "The next save writes the versioned format.");

        converting.LoadSave(new byte[] { 1, 2, 3 }, mod);
        Assert.AreEqual(VersionedSaveLoadStatus.Unrecognized, mod.Loads.Last().Status);

        foreach (VersionedSaveLegacyReader broken in new VersionedSaveLegacyReader[]
        {
            (byte[] d, VersionedSaveEntries e, out int v) => throw new FormatException("bad"),
            (byte[] d, VersionedSaveEntries e, out int v) => { v = 3; return true; },
        })
        {
            var failing = new FakeExtender();
            var failed = failing.Registration("Mod.Legacy", 2, 1, new[] { "value" }, _ => true);
            failed.LegacyReader = broken;
            var slot = failing.Register(failed);
            failing.LoadSave(legacy, slot);
            Assert.AreEqual(VersionedSaveLoadStatus.MigrationFailed, slot.Loads.Single().Status);
            CollectionAssert.AreEqual(legacy, failing.Save(slot));
        }
    }

    [TestMethod]
    public void LoadsWithoutARecordReportNoRecordAndDropThePreviousMission()
    {
        byte[] withUnknown = SaveWith(1, 1, new[] { "a", "x" }, new() { ["a"] = new byte[] { 1 }, ["x"] = new byte[] { 2 } });
        var extender = new FakeExtender();
        var state = new Dictionary<string, byte[]> { ["a"] = new byte[] { 5 } };
        var mod = extender.Register("Mod.Data", 1, 1, new[] { "a" }, Writes(state));

        extender.LoadSave(withUnknown, mod);
        Assert.AreEqual(1, mod.Loads.Last().KeptEntryCount);
        extender.LoadSave(null, mod);
        Assert.AreEqual(VersionedSaveLoadStatus.NoRecord, mod.Loads.Last().Status);
        AssertEntries(extender, mod, "a");

        extender.LoadSave(withUnknown, mod);
        extender.Registry.Unload();
        AssertEntries(extender, mod, "a");
    }

    [TestMethod]
    public void EachLoadIsReportedOnceWhicheverHandlerRunsFirst()
    {
        byte[] record = SaveWith(1, 1, new[] { "a" }, new() { ["a"] = new byte[] { 1 } });
        foreach (bool extenderFirst in new[] { false, true })
        {
            var extender = new FakeExtender();
            var mod = extender.Register("Mod.Data", 1, 1, new[] { "a" }, _ => true);
            extender.LoadSave(record, mod, extenderFirst: extenderFirst);
            extender.LoadSave(record, mod, extenderFirst: extenderFirst);
            Assert.AreEqual(2, mod.Loads.Count, "Reloading the same save is a new load.");
            Assert.IsTrue(mod.Loads.All(load => load.Status == VersionedSaveLoadStatus.Restored));
            extender.LoadSave(null, mod, extenderFirst: extenderFirst);
            Assert.AreEqual(VersionedSaveLoadStatus.NoRecord, mod.Loads.Last().Status);
        }
    }

    [TestMethod]
    public void MapUnloadInsideALoadBelongsToThatLoad()
    {
        byte[] record = SaveWith(1, 1, new[] { "a", "x" }, new() { ["a"] = new byte[] { 1 }, ["x"] = new byte[] { 2 } });
        var extender = new FakeExtender();
        var mod = extender.Register("Mod.Data", 1, 1, new[] { "a" }, Writes(new() { ["a"] = new byte[] { 3 } }));
        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav");
        mod.Load(record);
        extender.Registry.Unload();
        extender.Registry.EndLoad();
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, mod.Loads.Single().Status);
        AssertEntries(extender, mod, "a", "x");
    }

    [TestMethod]
    public void RegistrationsAreIsolatedAndReportedInOwnerOrder()
    {
        var extender = new FakeExtender();
        var order = new List<string>();
        var state = new Dictionary<string, byte[]> { ["a"] = new byte[] { 1 } };
        var zulu = extender.Register(new VersionedSaveDataRegistration("zulu.mod", "Zulu.Data", 1, 1, new[] { "a" },
            _ => throw new InvalidOperationException("capture"), _ => order.Add("zulu")));
        var alpha = extender.Register(new VersionedSaveDataRegistration("alpha.mod", "Alpha.Data", 1, 1, new[] { "a" },
            Writes(state), _ => { order.Add("alpha"); throw new InvalidOperationException("load"); }));
        byte[] alphaRecord = extender.Save(alpha);
        Assert.IsNull(extender.Save(zulu));
        Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, zulu.Handle.LastWriteStatus);
        Assert.AreEqual(VersionedSaveWriteStatus.Written, alpha.Handle.LastWriteStatus);

        extender.Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav");
        zulu.Load(alphaRecord);
        alpha.Load(alphaRecord);
        extender.Registry.EndLoad();
        CollectionAssert.AreEqual(new[] { "alpha", "zulu" }, order);
        Assert.IsTrue(alpha.Handle.KeepsLoadedRecord, "A record the consumer failed to take is never overwritten.");
        Assert.IsFalse(zulu.Handle.KeepsLoadedRecord);
        state["a"] = new byte[] { 2 };
        CollectionAssert.AreEqual(alphaRecord, extender.Save(alpha));
        Assert.AreEqual(2L, extender.Registry.CallbackFailures);
    }

    [TestMethod]
    public void LimitsMatchTheSavegameReaderAndApplyToWriterAndReader()
    {
        Assert.AreEqual(16384, VersionedSaveData.MaximumEntries, "Same per-mod limit as the savegame settings record (issue #10).");
        Assert.AreEqual(1024 * 1024, VersionedSaveData.MaximumEntryBytes);
        Assert.AreEqual(8 * 1024 * 1024, VersionedSaveData.MaximumRecordBytes);
        Assert.AreEqual(256, VersionedSaveData.MaximumNameLength);

        var extender = new FakeExtender();
        int count = 16384;
        var many = extender.Register("Mod.Many", 1, 1, new[] { "e/" }, writer =>
        {
            for (int i = 0; i < count; i++) writer.Write("e/" + i, new byte[] { 1 });
            return true;
        });
        byte[] full = extender.Save(many);
        Assert.AreEqual(VersionedSaveWriteStatus.Written, many.Handle.LastWriteStatus);
        extender.LoadSave(full, many);
        Assert.AreEqual(16384, many.Loads.Single().Entries.Count);
        count = 16385;
        Assert.IsNull(extender.Save(many));
        Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, many.Handle.LastWriteStatus);

        byte[] overCount = (byte[])full.Clone();
        overCount[20] = 0x01; overCount[21] = 0x40; // 16385
        extender.LoadSave(WithCrc(overCount, 0), many);
        Assert.AreEqual(VersionedSaveLoadStatus.Corrupt, many.Loads.Last().Status);

        var big = extender.Register("Mod.Big", 1, 1, new[] { "b/" }, writer =>
        {
            for (int i = 0; i < 9; i++) writer.Write("b/" + i, new byte[1024 * 1024]);
            return true;
        });
        Assert.IsNull(extender.Save(big));
        Assert.AreEqual(VersionedSaveWriteStatus.TooLarge, big.Handle.LastWriteStatus);

        byte[] withUnknown = SaveWith(1, 1, new[] { "u/" }, Enumerable.Range(0, 10).ToDictionary(i => "u/" + i, i => new byte[] { 1 }));
        var merged = extender.Register("Mod.Merged", 1, 1, new[] { "k/" }, writer =>
        {
            for (int i = 0; i < 16380; i++) writer.Write("k/" + i, new byte[0]);
            return true;
        });
        extender.LoadSave(withUnknown, merged);
        Assert.IsNull(extender.Save(merged), "Kept unknown entries count against the limit.");
        Assert.AreEqual(VersionedSaveWriteStatus.TooLarge, merged.Handle.LastWriteStatus);

        byte[] oversized = new byte[VersionedSaveData.MaximumRecordBytes + 1];
        oversized[0] = (byte)'A'; oversized[1] = (byte)'P'; oversized[2] = (byte)'S'; oversized[3] = (byte)'V'; oversized[4] = 1; oversized[6] = 1;
        extender.LoadSave(oversized, merged);
        Assert.AreEqual(VersionedSaveLoadStatus.Corrupt, merged.Loads.Last().Status);
    }

    [TestMethod]
    public void WriterRejectsInvalidEntries()
    {
        var known = new[] { "a", "p/" };
        foreach (Action<VersionedSaveWriter> invalid in new Action<VersionedSaveWriter>[]
        {
            w => w.Write("unknown", new byte[0]),
            w => w.Write("p/", new byte[0]),
            w => w.Write(" ", new byte[0]),
            w => w.Write(new string('a', 257), new byte[0]),
            w => w.Write("a\ud800", new byte[0]),
            w => { w.Write("a", new byte[0]); w.Write("a", new byte[0]); },
            w => w.Write("a", new byte[1024 * 1024 + 1]),
            w => w.Write("a", null),
        })
        {
            var extender = new FakeExtender();
            var mod = extender.Register("Mod.Data", 1, 1, known, w => { invalid(w); return true; });
            Assert.IsNull(extender.Save(mod));
            Assert.AreEqual(VersionedSaveWriteStatus.CaptureFailed, mod.Handle.LastWriteStatus);
        }
        VersionedSaveWriter retained = null;
        var late = new FakeExtender();
        var lateMod = late.Register("Mod.Late", 1, 1, known, w => { retained = w; return true; });
        late.Save(lateMod);
        Assert.ThrowsExactly<InvalidOperationException>(() => retained.Write("a", new byte[0]));
    }

    [TestMethod]
    public void ScopeControlsWhichLoadsAndSavesParticipate()
    {
        byte[] record = SaveWith(1, 1, new[] { "a" }, new() { ["a"] = new byte[] { 1 } });
        var extender = new FakeExtender();
        var saves = extender.Register("Mod.Saves", 1, 1, new[] { "a" }, Writes(new() { ["a"] = new byte[] { 2 } }));
        var maps = extender.Register(new VersionedSaveDataRegistration("owner", "Mod.Maps", 1, 1, new[] { "a" },
            Writes(new() { ["a"] = new byte[] { 3 } }), _ => { }) { Scope = VersionedSaveScope.SaveGamesAndMaps });
        foreach (VersionedSaveLoadSource source in new[] { VersionedSaveLoadSource.MapToPlay, VersionedSaveLoadSource.EditorMap })
        {
            extender.LoadSave(record, saves, source);
            Assert.AreEqual(VersionedSaveLoadStatus.OutOfScope, saves.Loads.Last().Status);
            Assert.AreEqual(source, saves.Loads.Last().Source);
            extender.LoadSave(record, maps, source);
            Assert.AreEqual(VersionedSaveLoadStatus.Restored, maps.Loads.Last().Status);
        }
        extender.LoadSave(record, saves, VersionedSaveLoadSource.MultiplayerSave);
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, saves.Loads.Last().Status);

        Assert.IsNull(extender.Save(saves, mapEditor: true));
        Assert.AreEqual(VersionedSaveWriteStatus.NotWritten, saves.Handle.LastWriteStatus);
        Assert.IsNull(extender.Save(saves, saveFile: false));
        Assert.IsNotNull(extender.Save(maps, mapEditor: true));
        Assert.AreEqual(VersionedSaveWriteStatus.Written, maps.Handle.LastWriteStatus);
    }

    [TestMethod]
    public void CaptureMayDeclineAndAnEmptyRecordReplacesOlderData()
    {
        var extender = new FakeExtender();
        bool write = false;
        var state = new Dictionary<string, byte[]>();
        var mod = extender.Register("Mod.Data", 1, 1, new[] { "a" }, writer =>
        {
            foreach (var entry in state) writer.Write(entry.Key, entry.Value);
            return write;
        });
        Assert.IsNull(extender.Save(mod));
        Assert.AreEqual(VersionedSaveWriteStatus.NotWritten, mod.Handle.LastWriteStatus);
        write = true;
        byte[] empty = extender.Save(mod);
        Assert.AreEqual(32, empty.Length, "An empty record still overwrites older Extender data.");
        extender.LoadSave(empty, mod);
        Assert.AreEqual(VersionedSaveLoadStatus.Restored, mod.Loads.Single().Status);
        Assert.AreEqual(0, mod.Loads.Single().Entries.Count);
    }

    [TestMethod]
    public void RegistrationIsValidatedBeforeTheExtenderSeesIt()
    {
        var extender = new FakeExtender();
        VersionedSaveCapture capture = _ => true;
        Action<VersionedSaveLoad> loaded = _ => { };
        string[] known = { "a" };
        var invalid = new List<VersionedSaveDataRegistration>
        {
            new(" ", "Mod.Data", 1, 1, known, capture, loaded),
            new("owner", "Mod/Data", 1, 1, known, capture, loaded),
            new("owner", "", 1, 1, known, capture, loaded),
            new("owner", "Mod.Data", 0, 1, known, capture, loaded),
            new("owner", "Mod.Data", 2, 3, known, capture, loaded),
            new("owner", "Mod.Data", 2, 0, known, capture, loaded),
            new("owner", "Mod.Data", 1, 1, new string[0], capture, loaded),
            new("owner", "Mod.Data", 1, 1, new[] { "/" }, capture, loaded),
            new("owner", "Mod.Data", 1, 1, new[] { "a", "a" }, capture, loaded),
            new("owner", "Mod.Data", 1, 1, known, null, loaded),
            new("owner", "Mod.Data", 1, 1, known, capture, null),
            new("owner", "Mod.Data", 1, 1, known, capture, loaded) { Scope = (VersionedSaveScope)7 },
        };
        var outOfRange = new VersionedSaveDataRegistration("owner", "Mod.Data", 2, 1, known, capture, loaded);
        outOfRange.AddMigration(2, _ => { });
        var twice = new VersionedSaveDataRegistration("owner", "Mod.Data", 3, 1, known, capture, loaded);
        twice.AddMigration(1, _ => { });
        twice.AddMigration(1, _ => { });
        invalid.Add(outOfRange);
        invalid.Add(twice);
        invalid.Add(null);
        foreach (VersionedSaveDataRegistration registration in invalid)
        {
            Assert.IsFalse(extender.Registry.TryRegister(registration, out var handle, out string reason));
            Assert.IsNull(handle);
            Assert.IsFalse(string.IsNullOrEmpty(reason));
        }
        Assert.AreEqual(0, extender.Handlers.Count);

        Assert.IsTrue(extender.Registry.TryRegister(new("owner", "Mod.Data", 1, 1, known, capture, loaded), out _, out _));
        Assert.IsFalse(extender.Registry.TryRegister(new("other", "MOD.DATA", 1, 1, known, capture, loaded), out _, out _),
            "Archive entry names ignore case.");
        extender.Reject = true;
        Assert.IsFalse(extender.Registry.TryRegister(new("owner", "Mod.Other", 1, 1, known, capture, loaded), out _, out string rejected));
        StringAssert.Contains(rejected, "Script Extender");
    }

    [TestMethod]
    public void EntriesEnforceNamesAndLimitsForMigrations()
    {
        var entries = new VersionedSaveEntries();
        byte[] data = { 1 };
        entries.Set("a", data);
        data[0] = 2;
        Assert.IsTrue(entries.TryGet("a", out byte[] stored));
        Assert.AreEqual(1, stored[0], "Set copies its input.");
        Assert.ThrowsExactly<ArgumentException>(() => entries.Set("", new byte[0]));
        Assert.ThrowsExactly<ArgumentNullException>(() => entries.Set("b", null));
        Assert.ThrowsExactly<ArgumentException>(() => entries.Set("b", new byte[1024 * 1024 + 1]));
        for (int i = 1; i < VersionedSaveData.MaximumEntries; i++) entries.Set("n" + i, new byte[0]);
        Assert.ThrowsExactly<InvalidOperationException>(() => entries.Set("one-more", new byte[0]));
        entries.Set("a", new byte[] { 3 });
        Assert.IsTrue(entries.Remove("a"));
        Assert.IsFalse(entries.Contains("a"));
    }

    private static void AssertEntries(FakeExtender extender, FakeExtender.Mod mod, params string[] names)
    {
        VersionedSaveRecordCodec.Decoded written = VersionedSaveRecordCodec.Decode(extender.Save(mod), int.MaxValue);
        Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.Valid, written.Status, written.Detail);
        CollectionAssert.AreEqual(names, written.Entries.Names.ToArray());
    }

    private static byte[] SaveWith(int version, int minimumReader, string[] known, Dictionary<string, byte[]> state)
    {
        var extender = new FakeExtender();
        return extender.Save(extender.Register("Mod.Writer", version, minimumReader, known, Writes(state)));
    }

    private static VersionedSaveCapture Writes(Dictionary<string, byte[]> state) => writer =>
    {
        foreach (var entry in state) writer.Write(entry.Key, entry.Value);
        return true;
    };

    // Rewrites the payload length (when bytes were appended) and the checksum with an independent CRC-32.
    private static byte[] WithCrc(byte[] record, int payloadDelta)
    {
        if (payloadDelta != 0)
        {
            int payload = BitConverter.ToInt32(record, 24) + payloadDelta;
            BitConverter.GetBytes(payload).CopyTo(record, 24);
        }
        uint crc = ReferenceCrc(record);
        BitConverter.GetBytes(crc).CopyTo(record, 28);
        return record;
    }

    private static uint ReferenceCrc(byte[] record)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < record.Length; i++)
        {
            if (i >= 28 && i < 32) continue;
            crc ^= record[i];
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    // Models Script Extender 2.14.1: one ModSaveDataAPI handler per identifier, and per archive load a
    // delivery in the Pre and in the Post phase, with APIShared's event handler before or after the
    // Extender's archive loader.
    private sealed class FakeExtender
    {
        internal readonly Dictionary<string, Mod> Handlers = new(StringComparer.Ordinal);
        internal readonly VersionedSaveDataRegistry Registry;
        internal bool Reject;
        private Mod pendingRegistration;

        internal FakeExtender()
        {
            Registry = new VersionedSaveDataRegistry((id, save, load) =>
            {
                if (Reject || Handlers.ContainsKey(id)) return false;
                Mod mod = pendingRegistration ?? new Mod(id);
                pendingRegistration = null;
                mod.Bind(save, load);
                Handlers.Add(id, mod);
                return true;
            }, _ => { }, _ => { });
        }

        internal VersionedSaveDataRegistration Registration(string id, int version, int minimumReader, string[] known,
            VersionedSaveCapture capture) =>
            new("owner", id, version, minimumReader, known, capture, _ => { });

        internal Mod Register(string id, int version, int minimumReader, string[] known, VersionedSaveCapture capture) =>
            Register(Registration(id, version, minimumReader, known, capture));

        internal Mod Register(VersionedSaveDataRegistration registration)
        {
            var mod = new Mod(registration.SaveDataIdentifier);
            var observed = new VersionedSaveDataRegistration(registration.OwnerGuid, registration.SaveDataIdentifier,
                registration.CurrentVersion, registration.MinimumReaderVersion, registration.KnownEntries, registration.Capture,
                load => { mod.Loads.Add(load); registration.Loaded(load); })
            { Scope = registration.Scope, LegacyReader = registration.LegacyReader };
            foreach (var step in registration.Migrations) observed.AddMigration(step.Key, step.Value);
            pendingRegistration = mod;
            Assert.IsTrue(Registry.TryRegister(observed, out var handle, out string reason), reason);
            mod.Handle = handle;
            return mod;
        }

        internal byte[] Save(Mod mod, bool saveFile = true, bool mapEditor = false) =>
            mod.SaveCallback(new VersionedSaveContext(saveFile, mapEditor, "test.sav"));

        internal void LoadSave(byte[] record, Mod mod, VersionedSaveLoadSource source = VersionedSaveLoadSource.SaveGame,
            bool extenderFirst = false)
        {
            if (extenderFirst)
            {
                Deliver(record, mod);   // Pre phase, before APIShared's Pre handler
                Registry.BeginLoad(source, "test.sav");
                Deliver(record, mod);   // Post phase, before APIShared's Post handler
                Registry.EndLoad();
            }
            else
            {
                Registry.BeginLoad(source, "test.sav");
                Deliver(record, mod);
                Registry.EndLoad();
                Deliver(record, mod);
            }
        }

        private static void Deliver(byte[] record, Mod mod)
        {
            if (record != null && record.Length != 0) mod.Load(record);
        }

        internal sealed class Mod
        {
            internal Mod(string identifier) { Identifier = identifier; }
            internal string Identifier { get; }
            internal List<VersionedSaveLoad> Loads { get; } = new();
            internal VersionedSaveDataHandle Handle { get; set; }
            internal Func<VersionedSaveContext, byte[]> SaveCallback { get; private set; }
            private Action<byte[]> loadCallback;
            internal void Bind(Func<VersionedSaveContext, byte[]> save, Action<byte[]> load) { SaveCallback = save; loadCallback = load; }
            internal void Load(byte[] record) => loadCallback((byte[])record.Clone());

        }
    }
}
