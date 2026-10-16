using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using APIShared;
using APIShared.Savegames;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API.Components.Archive;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class SavegameReadinessProtocolTests
    {
        private const string Token = "01234567890123456789012345678901";

        [TestMethod]
        public void CurrentPacketsCarryVersionRecordsAndReason()
        {
            var records = new[]
            {
                new SavegameRecordDigest("Author.Garrison", "Author.Garrison.Data", SavegameRecordState.Present, Enumerable.Repeat((byte)7, 32).ToArray()),
                new SavegameRecordDigest("Author.Navy", "Author.Navy.Ships", SavegameRecordState.Absent, null),
            };
            var packet = new SavegameLoadPacket { Operation = 1, Lobby = 123, Token = Token, SaveName = "coop-save",
                Fingerprint = new string('A', 64), Roster = "101,102", Checksum = 17, Time = 29, Metadata = new byte[] { 1 },
                Records = records, Reason = "why" };
            byte[] bytes = MessagePackSerializer.Serialize(packet);
            var copy = MessagePackSerializer.Deserialize<SavegameLoadPacket>(bytes);
            Assert.AreEqual(SavegameLoadPacket.CurrentVersion, copy.Version);
            Assert.AreEqual(1, copy.Operation);
            Assert.AreEqual("why", copy.Reason);
            Assert.AreEqual(2, copy.Records.Count);
            Assert.AreEqual("Author.Garrison.Data", copy.Records[0].Identifier);
            Assert.AreEqual(SavegameRecordState.Present, copy.Records[0].State);
            CollectionAssert.AreEqual(records[0].Hash, copy.Records[0].Hash);
            Assert.AreEqual(SavegameRecordState.Absent, copy.Records[1].State);
            Assert.AreEqual(0, copy.Records[1].Hash.Length);
            Assert.IsNull(SavegameRecordComparison.Describe(records, copy.Records, "Bob"));
            Assert.AreEqual(12, MessagePackSerializer.Deserialize<object[]>(bytes).Length);
        }

        [TestMethod]
        public void ProtocolOnePacketsAreRecognizedAndCanBeAnswered()
        {
            // The nine-field layout of the first readiness protocol, starting with the operation.
            byte[] legacy = MessagePackSerializer.Serialize(new object[]
                { 0, 123UL, Token, "coop-save", new string('A', 64), "101,102", 17, 29, new byte[] { 1, 2 } });
            var packet = MessagePackSerializer.Deserialize<SavegameLoadPacket>(legacy);
            Assert.AreEqual(SavegameLoadPacket.LegacyVersion, packet.Version);
            Assert.AreEqual(0, packet.Operation);
            Assert.AreEqual(Token, packet.Token);
            Assert.AreEqual(2, packet.Metadata[1]);
            Assert.AreEqual(0, packet.Records.Count);

            var cancel = new SavegameLoadPacket { Version = SavegameLoadPacket.LegacyVersion, Operation = 3, Lobby = 123,
                Token = Token, SaveName = "coop-save", Fingerprint = new string('A', 64), Roster = "101,102", Checksum = 17, Time = 29 };
            object[] fields = MessagePackSerializer.Deserialize<object[]>(MessagePackSerializer.Serialize(cancel));
            Assert.AreEqual(9, fields.Length, "A protocol-1 peer must be able to read the cancel.");
            Assert.AreEqual(3, Convert.ToInt32(fields[0]));
        }

        [TestMethod]
        public void LaterProtocolsKeepOnlyTheirVersion()
        {
            var packet = MessagePackSerializer.Deserialize<SavegameLoadPacket>(
                MessagePackSerializer.Serialize(new object[] { 3, "unknown layout", new byte[] { 9 } }));
            Assert.AreEqual(3, packet.Version);
            Assert.AreEqual(-1, packet.Operation);
            Assert.IsNull(packet.Token);
        }

        [TestMethod]
        public void OversizedOrMalformedRecordListsAreRejected()
        {
            var tooMany = Enumerable.Range(0, SavegameRecordComparison.MaximumRecords + 1)
                .Select(i => new SavegameRecordDigest("o", "r" + i, SavegameRecordState.Absent, null)).ToArray();
            foreach (SavegameLoadPacket bad in new[]
            {
                Packet(tooMany, null),
                Packet(new[] { new SavegameRecordDigest("o", "r", SavegameRecordState.Present, new byte[33]) }, null),
                Packet(new[] { new SavegameRecordDigest("o", new string('r', 201), SavegameRecordState.Absent, null) }, null),
                Packet(new[] { new SavegameRecordDigest("o", "r", (SavegameRecordState)5, null) }, null),
                Packet(new SavegameRecordDigest[0], new string('x', SavegameLoadPacket.MaximumReasonLength + 1)),
            })
            {
                byte[] bytes = MessagePackSerializer.Serialize(bad);
                Assert.ThrowsExactly<MessagePackSerializationException>(() => MessagePackSerializer.Deserialize<SavegameLoadPacket>(bytes));
            }
        }

        [TestMethod]
        public void StoredRecordDigestsComeFromEachPlayersOwnSaveCopy()
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string declared = "APIShared.Tests.Sim." + suffix, local = "APIShared.Tests.View." + suffix;
            var simulation = new VersionedSaveDataRegistration("APIShared.Tests.Sim", declared, 1, 1, new[] { "a" }, _ => true, _ => { })
                { SimulationRelevant = true };
            Assert.IsTrue(VersionedSaveData.TryRegister(simulation, out _, out string reason), reason);
            Assert.IsTrue(VersionedSaveData.TryRegister(new VersionedSaveDataRegistration("APIShared.Tests.Sim", local, 1, 1,
                new[] { "a" }, _ => true, _ => { }), out _, out reason), reason);

            string directory = Path.Combine(Path.GetTempPath(), "APISharedRecords-" + suffix);
            Directory.CreateDirectory(directory);
            // The Extender's archive and logging code binds its own dependencies (for example
            // System.Threading.Tasks.Extensions 4.2.4.0); resolve them from the installed Extender, as
            // WorkshopHookTests does, so no failed bind is cached for later suites.
            try { WithExtenderResolver(() => CompareCopies(directory, declared, local)); }
            finally { Directory.Delete(directory, true); }
        }

        private static void CompareCopies(string directory, string declared, string local)
        {
            byte[] record = { 1, 2, 3, 4 };
            // Different vanilla bytes and local view data in each copy, the same declared record.
            string host = WriteSave(directory, "host.sav", 0x11, 4096, new Dictionary<string, byte[]> { [declared] = record, [local] = new byte[] { 1 } });
            string client = WriteSave(directory, "client.sav", 0x22, 1000, new Dictionary<string, byte[]> { [declared] = record, [local] = new byte[] { 2 } });
            List<SavegameRecordDigest> hostDigests = Mine(host, declared), clientDigests = Mine(client, declared);
            Assert.AreEqual(1, hostDigests.Count);
            Assert.AreEqual(SavegameRecordState.Present, hostDigests[0].State);
            Assert.IsNull(SavegameRecordComparison.Describe(hostDigests, clientDigests, "Bob"));
            Assert.IsFalse(VersionedSaveDataRuntime.CaptureSimulationDigests(host).Any(x => x.Identifier == local),
                "Undeclared records are never read for the comparison.");

            string changed = WriteSave(directory, "changed.sav", 0x22, 1000, new Dictionary<string, byte[]> { [declared] = new byte[] { 9 } });
            StringAssert.Contains(SavegameRecordComparison.Describe(hostDigests, Mine(changed, declared), "Bob"),
                "record '" + declared + "' of mod 'APIShared.Tests.Sim' differs");

            string vanilla = Path.Combine(directory, "vanilla.sav");
            File.WriteAllBytes(vanilla, Enumerable.Repeat((byte)0x33, 2048).ToArray());
            Assert.AreEqual(SavegameRecordState.Absent, Mine(vanilla, declared)[0].State, "A save without mod data has no record.");

            string damaged = Path.Combine(directory, "damaged.sav");
            byte[] whole = File.ReadAllBytes(host);
            File.WriteAllBytes(damaged, whole.Take(whole.Length - 40).ToArray());
            Assert.AreEqual(SavegameRecordState.Unreadable, Mine(damaged, declared)[0].State,
                "A damaged archive blocks the load instead of passing as absent.");

            // Per-load cost on a large save: one archive read plus SHA-256 of the declared records.
            string large = WriteSave(directory, "large.sav", 0x44, 8 * 1024 * 1024,
                new Dictionary<string, byte[]> { [declared] = new byte[256 * 1024], [local] = new byte[1024] });
            Mine(large, declared);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) Mine(large, declared);
            double capture = watch.Elapsed.TotalMilliseconds / 5;
            // For scale: the whole-file SHA-256 the coordinator already takes twice per player and load.
            watch.Restart();
            for (int i = 0; i < 5; i++)
                using (var stream = File.OpenRead(large))
                using (var sha = System.Security.Cryptography.SHA256.Create()) sha.ComputeHash(stream);
            Console.WriteLine("SAVE_RECORD_DIGEST_COST: " + capture.ToString("F1") + " ms per capture, whole-file SHA-256 " +
                (watch.Elapsed.TotalMilliseconds / 5).ToString("F1") + " ms, 8 MiB save with a 256 KiB declared record");
        }

        private static void WithExtenderResolver(Action test)
        {
            string game = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim().Trim('﻿');
            string extender = Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            ResolveEventHandler resolve = (sender, args) =>
            {
                string path = Path.Combine(extender, new System.Reflection.AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try { test(); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        }

        private static SavegameLoadPacket Packet(SavegameRecordDigest[] records, string reason) =>
            new SavegameLoadPacket { Operation = 1, Lobby = 1, Token = Token, SaveName = "s", Fingerprint = new string('A', 64),
                Roster = "1", Records = records, Reason = reason };

        private static List<SavegameRecordDigest> Mine(string save, string identifier) =>
            VersionedSaveDataRuntime.CaptureSimulationDigests(save).Where(x => x.Identifier == identifier).ToList();

        // A save body followed by the appended archive, as the Extender writes it.
        private static string WriteSave(string directory, string name, byte fill, int bodyBytes, Dictionary<string, byte[]> records)
        {
            string path = Path.Combine(directory, name);
            using (MapArchive archive = MapArchive.CreateEmpty(path))
            {
                foreach (var record in records)
                    Assert.IsTrue(archive.TryWriteBinaryFile("_SE_ModData_" + record.Key + ".msgpack", record.Value, true, true));
                archive.CommitChanges();
                File.WriteAllBytes(path, Enumerable.Repeat(fill, bodyBytes).Concat(archive.ArchiveStream.ToArray()).ToArray());
            }
            return path;
        }
    }
}
