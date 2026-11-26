using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using APIShared;
using APIShared.GameModes;
using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public class StoredSnapshotMissionTests
    {
        private sealed class SyncHostOnlyAttribute : Attribute { }

        // Models the presence-sensitive legacy Brazier conversion, independently of the
        // consumer's parser tests: missing Human costs derive; explicit values always win.
        private sealed class Endpoint : IModSettingsWorkingCopyEndpoint, IModSettingsStoredSnapshotMigration, IModSettingsSnapshotMigration
        {
            private string buildingCosts = "personal";
            [SyncHostOnly] public string BuildingCosts { get => RefuseLiveReads ? throw new InvalidOperationException("Capture read live settings") : buildingCosts; set => buildingCosts = value; }
            [SyncHostOnly] public string HumanCosts { get; set; } = "0";
            public string Personal { get; set; } = "own";
            internal int RawCalls, LegacyCalls, Entries;
            internal bool Fail, InjectPersonal, RefuseLiveReads;
            internal Dictionary<string, byte[]> Seen, Player;
            internal readonly Dictionary<string, byte[]> Baseline = Values("default", "0");
            public bool IsMissionPresetActive { get; private set; }
            internal static Dictionary<string, byte[]> Values(string building, string human) => new Dictionary<string, byte[]> {
                [nameof(BuildingCosts)] = MessagePackSerializer.Serialize(building),
                [nameof(HumanCosts)] = MessagePackSerializer.Serialize(human),
                [nameof(Personal)] = MessagePackSerializer.Serialize("own") };
            public Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot() => Baseline;
            public Dictionary<string, byte[]> System_CreateModDefaultSnapshot() => Baseline;
            public Dictionary<string, byte[]> System_CreatePlayerMissionPresetSnapshot() => Player ?? Values(BuildingCosts, HumanCosts);
            public Dictionary<string, byte[]> System_CreateCurrentMissionPresetSnapshot() => Values(BuildingCosts, HumanCosts);
            public Dictionary<string, byte[]> System_CreateCurrentWorkingSnapshot() => Values(BuildingCosts, HumanCosts);
            public void System_ApplyMissionPresetSnapshot(Dictionary<string, byte[]> snapshot, string label) => Apply(snapshot);
            public void System_ApplyWorkingSnapshot(Dictionary<string, byte[]> snapshot) => Apply(snapshot);
            public void System_LoadModDefaults() => Apply(Baseline);
            public void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable)
            { Entries++; Apply(snapshot); IsMissionPresetActive = true; }
            public void System_ExitMissionPreset() { IsMissionPresetActive = false; }
            private void Apply(Dictionary<string, byte[]> snapshot)
            {
                BuildingCosts = MessagePackSerializer.Deserialize<string>(snapshot[nameof(BuildingCosts)]);
                HumanCosts = MessagePackSerializer.Deserialize<string>(snapshot[nameof(HumanCosts)]);
            }
            public Dictionary<string, byte[]> System_MigrateStoredSnapshot(Dictionary<string, byte[]> snapshot)
            {
                RawCalls++;
                Seen = snapshot.ToDictionary(x => x.Key, x => x.Value == null ? null : (byte[])x.Value.Clone());
                if (Fail)
                {
                    if (snapshot.TryGetValue(nameof(BuildingCosts), out var corrupt) && corrupt?.Length > 0) corrupt[0] ^= 0x7f;
                    throw new InvalidDataException("Injected raw migration failure");
                }
                if (snapshot.TryGetValue(nameof(BuildingCosts), out var bytes) && bytes != null && MessagePackSerializer.Deserialize<string>(bytes) == "Brazier=50")
                {
                    snapshot[nameof(BuildingCosts)] = MessagePackSerializer.Serialize("Brazier=-1");
                    if (!snapshot.ContainsKey(nameof(HumanCosts))) snapshot[nameof(HumanCosts)] = MessagePackSerializer.Serialize("50");
                }
                if (InjectPersonal) snapshot[nameof(Personal)] = MessagePackSerializer.Serialize("forbidden");
                return snapshot;
            }
            public Dictionary<string, byte[]> System_MigrateMissionSnapshot(Dictionary<string, byte[]> snapshot)
            { LegacyCalls++; return snapshot; }
        }

        private static MissionModSettingsEntry Entry(string human = null, bool includeHuman = false)
        {
            var entry = new MissionModSettingsEntry();
            entry.Overrides[nameof(Endpoint.BuildingCosts)] = "Brazier=50";
            if (includeHuman) entry.Overrides[nameof(Endpoint.HumanCosts)] = human;
            return entry;
        }
        private static string Read(Dictionary<string, byte[]> values, string name) => MessagePackSerializer.Deserialize<string>(values[name]);

        [TestMethod]
        public void MaterializeSelectedValuesBeforeDefaultsAndPreserveExplicitHumanPresence()
        {
            var endpoint = new Endpoint { InjectPersonal = true, Player = Endpoint.Values("Brazier=50", "0") };
            var player = new MissionModSettingsEntry { PlayerSettings = new[] { nameof(Endpoint.BuildingCosts) } };
            var selected = MissionModSettingsPreparation.MaterializeSource(endpoint, player);
            Assert.AreEqual("Brazier=-1", Read(selected, nameof(Endpoint.BuildingCosts)));
            Assert.AreEqual("50", Read(selected, nameof(Endpoint.HumanCosts)));
            Assert.IsFalse(endpoint.Seen.ContainsKey(nameof(Endpoint.HumanCosts)), "An unselected Player value or a default hid absence.");
            Assert.AreEqual("own", Read(selected, nameof(Endpoint.Personal)), "Converter additions escaped host authorization.");
            Assert.AreEqual("Brazier=50", Read(endpoint.Player, nameof(Endpoint.BuildingCosts)));
            Assert.AreEqual(0, endpoint.LegacyCalls, "The new raw path must not invoke the old mission converter.");
            foreach (string value in new[] { "0", "" })
            {
                var explicitValue = MissionModSettingsPreparation.MaterializeSource(endpoint, Entry(value, true));
                Assert.AreEqual(value, Read(explicitValue, nameof(Endpoint.HumanCosts)));
                Assert.IsTrue(endpoint.Seen.ContainsKey(nameof(Endpoint.HumanCosts)));
            }
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsPreparation.MaterializeSource(endpoint, Entry(null, true)),
                "The existing mission JSON null-conversion contract must remain unchanged.");
            player.PlayerSettings = new[] { nameof(Endpoint.BuildingCosts), nameof(Endpoint.HumanCosts) };
            Assert.AreEqual("0", Read(MissionModSettingsPreparation.MaterializeSource(endpoint, player), nameof(Endpoint.HumanCosts)));
            endpoint.Player = new Dictionary<string, byte[]> { [nameof(Endpoint.BuildingCosts)] = MessagePackSerializer.Serialize("Brazier=50") };
            var missingPlayerValue = MissionModSettingsPreparation.MaterializeSource(endpoint, player);
            Assert.AreEqual("0", Read(missingPlayerValue, nameof(Endpoint.HumanCosts)), "An explicitly selected missing Player value must retain its code default.");
            Assert.AreEqual("0", Read(endpoint.Seen, nameof(Endpoint.HumanCosts)), "The default fallback lost its explicit selection presence before migration.");
            Assert.IsFalse(endpoint.Player.ContainsKey(nameof(Endpoint.HumanCosts)), "Materialization modified the partial Player source.");
            Assert.AreEqual("Brazier=50", Read(endpoint.Player, nameof(Endpoint.BuildingCosts)));
            Assert.AreEqual("default", Read(endpoint.Baseline, nameof(Endpoint.BuildingCosts)));
            Assert.AreEqual("0", Read(endpoint.Baseline, nameof(Endpoint.HumanCosts)));
        }

        [TestMethod]
        public void MissionApplicationsNormalizeSelectedSourcesBeforeAnyEndpointEntry()
        {
            foreach (var kind in new[] { MissionModSettingsApplicationKind.Playable, MissionModSettingsApplicationKind.Preview,
                MissionModSettingsApplicationKind.Editable, MissionModSettingsApplicationKind.Authoring, MissionModSettingsApplicationKind.AuthoringTest })
            {
                var endpoint = new Endpoint(); var session = new MissionModSettingsSession();
                var document = new MissionModSettingsDocument(); document.Mods["mod"] = Entry();
                session.Apply(document, new Dictionary<string, IModSettingsPresetEndpoint> { ["mod"] = endpoint }, kind);
                Assert.AreEqual("50", endpoint.HumanCosts, kind.ToString());
                Assert.AreEqual(1, endpoint.RawCalls, kind.ToString());
                Assert.IsFalse(endpoint.Seen.ContainsKey(nameof(Endpoint.HumanCosts)), kind.ToString());
                Assert.AreEqual("Brazier=50", document.Mods["mod"].Overrides[nameof(Endpoint.BuildingCosts)]);
                session.Exit();
            }
            var first = new Endpoint(); var failing = new Endpoint { Fail = true };
            var failedDocument = new MissionModSettingsDocument(); failedDocument.Mods["first"] = Entry(); failedDocument.Mods["failing"] = Entry();
            Assert.ThrowsExactly<InvalidDataException>(() => new MissionModSettingsSession().Apply(failedDocument,
                new Dictionary<string, IModSettingsPresetEndpoint> { ["first"] = first, ["failing"] = failing }, MissionModSettingsApplicationKind.Playable));
            Assert.AreEqual(0, first.Entries + failing.Entries, "A later converter failure applied an earlier prepared endpoint.");
            Assert.AreEqual("default", Read(failing.Baseline, nameof(Endpoint.BuildingCosts)));
        }

        private static Dictionary<string, byte[]> Resolve(Endpoint endpoint, Dictionary<string, byte[]> values,
            SavegameModSettingsRecord record = null, bool migrateRaw = true, Dictionary<string, byte[]> current = null)
        {
            Type service = typeof(SavegameModSettings);
            Type participantType = service.GetNestedType("CompatibleMod", BindingFlags.NonPublic);
            object participant = Activator.CreateInstance(participantType, true);
            participantType.GetField("Endpoint", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(participant, endpoint);
            participantType.GetField("Properties", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(participant,
                new[] { typeof(Endpoint).GetProperty(nameof(Endpoint.BuildingCosts)), typeof(Endpoint).GetProperty(nameof(Endpoint.HumanCosts)) });
            return (Dictionary<string, byte[]>)service.GetMethod("ResolveStartupSnapshotCore", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                new object[] { "mod", participant, record, new Dictionary<string, Dictionary<string, byte[]>> { ["mod"] = values },
                    current == null ? null : new Dictionary<string, Dictionary<string, byte[]>> { ["mod"] = current }, migrateRaw });
        }

        [TestMethod]
        public void MigratedTrailCreatorRulesRoundTripFixedValuesAndPreserveExplicitPlayerDefaults()
        {
            var endpoint = new Endpoint(); var session = new MissionModSettingsSession(); var document = new MissionModSettingsDocument();
            var entry = Entry(); document.Mods["mod"] = entry;
            session.Apply(document, new Dictionary<string, IModSettingsPresetEndpoint> { ["mod"] = endpoint }, MissionModSettingsApplicationKind.Playable);
            Assert.AreEqual("50", endpoint.HumanCosts);
            var applied = endpoint.System_CreateCurrentWorkingSnapshot();
            endpoint.RefuseLiveReads = true;
            var rules = MissionModSettingsCreatorRules.Capture(endpoint, entry);
            Assert.AreEqual(2, rules[nameof(Endpoint.HumanCosts)].Mode);
            Assert.AreEqual("50", MessagePackSerializer.Deserialize<string>(rules[nameof(Endpoint.HumanCosts)].FixedValue));
            var record = new SavegameModSettingsRecord { Kind = (int)GameModeKind.CustomTrail,
                CreatorRules = new Dictionary<string, Dictionary<string, TrailCreatorRule>> { ["mod"] = rules } };
            Assert.AreEqual("50", Read(Resolve(new Endpoint(), applied, record), nameof(Endpoint.HumanCosts)),
                "A synthetic ModDefault rule suppressed the migration on the saved strict Trail.");
            var explicitPlayer = Entry(); explicitPlayer.PlayerSettings = new[] { nameof(Endpoint.HumanCosts) };
            rules = MissionModSettingsCreatorRules.Capture(endpoint, explicitPlayer);
            Assert.AreEqual(1, rules[nameof(Endpoint.HumanCosts)].Mode);
            record.CreatorRules["mod"] = rules;
            Assert.AreEqual("25", Read(Resolve(new Endpoint(), applied, record, current: Endpoint.Values("ignored", "25")), nameof(Endpoint.HumanCosts)));
            var basePlayer = new MissionModSettingsEntry { PlayerSettings = new[] { nameof(Endpoint.BuildingCosts) } };
            rules = MissionModSettingsCreatorRules.Capture(endpoint, basePlayer);
            Assert.AreEqual(1, rules[nameof(Endpoint.BuildingCosts)].Mode);
            Assert.IsFalse(rules.ContainsKey(nameof(Endpoint.HumanCosts)), "A missing companion acquired a fabricated Player/default rule.");
            Assert.AreEqual(0, MissionModSettingsCreatorRules.Capture(endpoint, null).Count);
            record.CreatorRules["mod"] = MissionModSettingsCreatorRules.Capture(endpoint, entry);
            record.CreatorRules["mod"][nameof(Endpoint.HumanCosts)] = new TrailCreatorRule { Mode = 0 };
            Assert.AreEqual("0", Read(Resolve(new Endpoint(), applied, record), nameof(Endpoint.HumanCosts)), "Existing explicit savegame ModDefault must remain binding.");
            session.Exit();
        }

        [TestMethod]
        public void SaveRawPresenceAndCreatorDefaultsNormalizeBeforeHostFingerprintButNotAgainOnClient()
        {
            var stored = new Dictionary<string, byte[]> { [nameof(Endpoint.BuildingCosts)] = MessagePackSerializer.Serialize("Brazier=50") };
            var host = new Endpoint { InjectPersonal = true };
            var normalized = Resolve(host, stored);
            Assert.AreEqual("50", Read(normalized, nameof(Endpoint.HumanCosts)));
            Assert.AreEqual(1, host.RawCalls); Assert.AreEqual(1, host.LegacyCalls);
            Assert.IsFalse(host.Seen.ContainsKey(nameof(Endpoint.HumanCosts)));
            Assert.AreEqual("own", Read(normalized, nameof(Endpoint.Personal)));
            Assert.AreEqual("Brazier=50", Read(stored, nameof(Endpoint.BuildingCosts)));
            foreach (byte[] explicitHuman in new[] { MessagePackSerializer.Serialize("0"), MessagePackSerializer.Serialize(""), null, Array.Empty<byte>() })
            {
                var explicitStored = new Dictionary<string, byte[]>(stored) { [nameof(Endpoint.HumanCosts)] = explicitHuman };
                var explicitEndpoint = new Endpoint();
                var explicitSnapshot = Resolve(explicitEndpoint, explicitStored);
                Assert.IsTrue(explicitEndpoint.Seen.ContainsKey(nameof(Endpoint.HumanCosts)));
                Assert.AreNotEqual("50", Read(explicitSnapshot, nameof(Endpoint.HumanCosts)), "Explicit zero/empty/null bytes were mistaken for absence.");
            }
            var client = new Endpoint();
            var clientSnapshot = Resolve(client, normalized, migrateRaw: false);
            Assert.AreEqual(0, client.RawCalls, "Authenticated canonical host values were migrated again.");
            Assert.AreEqual(1, client.LegacyCalls, "The established post-default converter changed semantics.");
            string Fingerprint(Dictionary<string, byte[]> snapshot) => SavegameLoadAgreement.Fingerprint(
                new Dictionary<string, Dictionary<string, byte[]>> { ["mod"] = snapshot.Where(x => x.Key != nameof(Endpoint.Personal)).ToDictionary(x => x.Key, x => x.Value) });
            Assert.AreEqual(Fingerprint(normalized), Fingerprint(clientSnapshot));
            var record = new SavegameModSettingsRecord { Kind = (int)GameModeKind.CustomTrail, TrailCustomizeAllowed = false,
                CreatorRules = new Dictionary<string, Dictionary<string, TrailCreatorRule>>() };
            record.CreatorRules["mod"] = new Dictionary<string, TrailCreatorRule> {
                [nameof(Endpoint.BuildingCosts)] = new TrailCreatorRule { Mode = 2, FixedValue = stored[nameof(Endpoint.BuildingCosts)] },
                [nameof(Endpoint.HumanCosts)] = new TrailCreatorRule { Mode = 0 } };
            var explicitDefault = Resolve(new Endpoint(), stored, record);
            Assert.AreEqual("0", Read(explicitDefault, nameof(Endpoint.HumanCosts)), "Explicit ModDefault was mistaken for an absent legacy Human setting.");
        }

        [TestMethod]
        public void FailedRawSaveMigrationDoesNotMutateStoredValuesOrDefaultBuffers()
        {
            var stored = new Dictionary<string, byte[]> { [nameof(Endpoint.BuildingCosts)] = MessagePackSerializer.Serialize("Brazier=50") };
            byte[] original = (byte[])stored[nameof(Endpoint.BuildingCosts)].Clone();
            var endpoint = new Endpoint { Fail = true };
            var failure = Assert.ThrowsExactly<TargetInvocationException>(() => Resolve(endpoint, stored));
            Assert.IsInstanceOfType<InvalidDataException>(failure.InnerException);
            CollectionAssert.AreEqual(original, stored[nameof(Endpoint.BuildingCosts)]);
            Assert.AreEqual("default", Read(endpoint.Baseline, nameof(Endpoint.BuildingCosts)));
            Assert.AreEqual(0, endpoint.Entries + endpoint.LegacyCalls);
        }
    }
}
