using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using APIShared.Economy;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class AutoTradeArchiveTests
    {
        [TestMethod]
        public void ArchiveUsesOnlyTheCurrentCompletedDelivery()
        {
            var fixture = new Fixture(false);
            string path = Path.Combine(Path.GetTempPath(), "auto-trade.msv");
            byte[] bytes = fixture.Save(new VersionedSaveContext(true, false, path));
            Assert.IsNotNull(bytes);
            Assert.IsNull(fixture.Save(new VersionedSaveContext(false, true, path)));
            CollectionAssert.AreEqual(new[] { "APIShared_AutoTrade" },
                fixture.Registry.SimulationRecords.Select(entry => entry.Value).ToArray());

            fixture.Begin(VersionedSaveLoadSource.MultiplayerSave, path, 14, bytes);
            Assert.IsNull(fixture.Archive.Resolve(14, path, true), "Prepared does not prove native completion.");
            fixture.Registry.EndLoad();
            Assert.AreEqual(false, fixture.Archive.Resolve(14, path, true));
            Assert.IsNull(fixture.Archive.Resolve(14, path + ".other", true));
            Assert.IsNull(fixture.Archive.Resolve(15, path, true));
            Assert.IsNull(fixture.Archive.Resolve(14, path, false));

            fixture.Begin(VersionedSaveLoadSource.MultiplayerSave, path, 15, null);
            Assert.IsNull(fixture.Archive.Resolve(14, path, true), "A new window invalidates the prior origin.");
            fixture.Registry.EndLoad();
            Assert.IsNull(fixture.Archive.Resolve(15, path, true), "A legacy MP save has no confirmed value.");

            fixture.Begin(VersionedSaveLoadSource.MultiplayerSave, path, 16, bytes);
            fixture.Begin(VersionedSaveLoadSource.MultiplayerSave, path + ".other", 16, bytes);
            fixture.Registry.EndLoad();
            Assert.IsNull(fixture.Archive.Resolve(16, path, true));
            Assert.AreEqual(false, fixture.Archive.Resolve(16, path + ".other", true));
            fixture.Registry.Unload();
            Assert.IsNull(fixture.Archive.Resolve(16, path + ".other", true));
        }

        [TestMethod]
        public void LocalLegacyFallbackRequiresExactFreshAbsenceAndFile()
        {
            var fixture = new Fixture(null);
            string path = Path.Combine(Path.GetTempPath(), "auto-trade-" + Guid.NewGuid().ToString("N") + ".sav");
            try
            {
                File.WriteAllBytes(path, MakeLegacySave("1"));
                fixture.Archive.PrepareLegacy(21, path);
                fixture.Begin(VersionedSaveLoadSource.SaveGame, path, 21, null);
                fixture.Registry.EndLoad();
                Assert.AreEqual(true, fixture.Archive.Resolve(21, path, false));
                Assert.IsNull(fixture.Archive.Resolve(21, path, true));
                Assert.IsNull(fixture.Archive.Resolve(20, path, false));

                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
                Assert.IsNull(fixture.Archive.Resolve(21, path, false), "Changed file metadata invalidates the staged flag.");
                fixture.Archive.PrepareLegacy(22, path);
                fixture.Begin(VersionedSaveLoadSource.SaveGame, path, 22, InvalidRuleRecord());
                fixture.Registry.EndLoad();
                Assert.IsNull(fixture.Archive.Resolve(22, path, false), "An unreadable rule must not become a legacy default.");
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        [DataRow("0", true, false)]
        [DataRow("1", true, true)]
        [DataRow("garbled", false, false)]
        [DataRow("2", false, false)]
        public void LegacyFieldParsesOnlyStrictVanillaBits(string token, bool valid, bool expected)
        {
            Assert.AreEqual(valid, AutoTradeArchive.TryReadLegacyRule(MakeLegacySave(token), out bool allowed));
            Assert.AreEqual(expected, allowed);
            Assert.IsFalse(AutoTradeArchive.TryReadLegacyRule(new byte[29], out _));
        }

        private static byte[] InvalidRuleRecord() =>
            VersionedSaveRecordCodec.Encode(1, 1, new Dictionary<string, byte[]>
            {
                { "allowed", new byte[] { 2 } }
            });

        private static byte[] MakeLegacySave(string autoTrade)
        {
            byte[] payload;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)60); // Current Vanilla restart payload.
                writer.Write(8);
                writer.Write(new byte[8 * 12]);
                writer.Write((byte)0);
                writer.Write(1);
                writer.Write((byte)'m');
                var settings = Enumerable.Repeat("0", 117).ToArray();
                settings[0] = "-12";
                settings[5] = autoTrade;
                byte[] setup = Encoding.ASCII.GetBytes(string.Join(",", settings) + ",");
                writer.Write(setup.Length);
                writer.Write(setup);
                payload = stream.ToArray();
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(-2); // SCDE container magic.
                for (int block = 0; block < 5; block++) writer.Write(0);
                writer.Write(4);
                writer.Write(0);
                writer.Write(payload.Length);
                writer.Write(payload);
                writer.Write(0);
                writer.Write(2036);
                stream.SetLength(35000);
                return stream.ToArray();
            }
        }

        private sealed class Fixture
        {
            internal readonly VersionedSaveDataRegistry Registry;
            internal readonly AutoTradeArchive Archive;
            internal Func<VersionedSaveContext, byte[]> Save;
            private Action<byte[]> deliver;

            internal Fixture(bool? value)
            {
                Registry = new VersionedSaveDataRegistry((_, save, load) =>
                { Save = save; deliver = load; return true; }, _ => { }, _ => { });
                Assert.IsTrue(AutoTradeArchive.TryCreate(() => value, out Archive, out string reason, Registry), reason);
            }

            internal void Begin(VersionedSaveLoadSource source, string filePath, long session, byte[] bytes)
            {
                Registry.BeginLoad(source, filePath, session);
                deliver(bytes);
                Registry.PrepareLoad();
            }
        }
    }
}
