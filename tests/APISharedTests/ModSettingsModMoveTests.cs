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
using SHCDESE.API.Components.Network;

namespace APISharedTests
{
    // A release can move a feature to a new plugin GUID while old saves and authored maps
    // still name its former owner. These tests execute the production document migrations
    // and startup resolver, preserving author rules rather than substituting local defaults.
    [TestClass, DoNotParallelize]
    public sealed class ModSettingsModMoveTests
    {
        private static string Id() => "move.tests." + Guid.NewGuid().ToString("N");
        private static byte[] Bool(bool value) => MessagePackSerializer.Serialize(value);
        private static bool Read(byte[] value) => MessagePackSerializer.Deserialize<bool>(value);
        private static ModSettingMove Move(string from = "OldFeature", string to = "Feature") =>
            new ModSettingMove(from, to, typeof(bool));
        private static Dictionary<string, byte[]> Values(params (string Key, bool Value)[] values) =>
            values.ToDictionary(value => value.Key, value => Bool(value.Value), StringComparer.Ordinal);
        private static void Register(string source, string target, bool copyGate = true) =>
            ModSettingsMigration.RegisterModMove(target, "release-split", new ModSettingsModMove(
                source, target, new[] { Move() }, copyGate ? new[] { Move("EnableMod", "EnableMod") } : null,
                new Dictionary<string, object> { ["EnableMod"] = true }));
        private static SavegameModSettingsRecord Record(string source) => new SavegameModSettingsRecord
        {
            Version = 3, Kind = (int)GameModeKind.CustomGame, TrailCustomizeAllowed = true,
            Mods = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal)
            { [source] = Values(("OldFeature", false), ("EnableMod", false), ("Unmoved", true)) },
            CreatorRules = new Dictionary<string, Dictionary<string, TrailCreatorRule>>(StringComparer.Ordinal)
        };

        [TestMethod]
        public void SavedValuesMoveAndSharedActivationIsCopiedWithoutChangingCallerData()
        {
            string source = Id(), target = Id(); Register(source, target);
            var record = Record(source);
            record.CreatorRules[source] = new Dictionary<string, TrailCreatorRule>
            {
                ["OldFeature"] = new TrailCreatorRule { Mode = 2, FixedValue = Bool(false) },
                ["EnableMod"] = new TrailCreatorRule { Mode = 1 }
            };
            var result = ModSettingsMigration.MigrateSavegameRecord(record);
            Assert.AreNotSame(record, result);
            Assert.IsFalse(Read(result.Mods[target]["Feature"]));
            Assert.IsFalse(Read(result.Mods[target]["EnableMod"]), "A stored false outranks the newly introduced gate's true default.");
            Assert.IsFalse(result.Mods[source].ContainsKey("OldFeature"));
            Assert.IsFalse(result.CreatorRules[source].ContainsKey("OldFeature"));
            Assert.IsFalse(Read(result.Mods[source]["EnableMod"]), "Copied settings retain their original owner.");
            Assert.AreEqual(1, result.CreatorRules[source]["EnableMod"].Mode);
            Assert.AreEqual(2, result.CreatorRules[target]["Feature"].Mode);
            Assert.IsFalse(Read(result.CreatorRules[target]["Feature"].FixedValue));
            Assert.AreEqual(1, result.CreatorRules[target]["EnableMod"].Mode);
            Assert.IsTrue(record.Mods[source].ContainsKey("OldFeature"));
            result.Mods[target]["Feature"][0] = 0xc3;
            result.CreatorRules[target]["Feature"].FixedValue[0] = 0xc3;
            Assert.IsFalse(Read(record.Mods[source]["OldFeature"]));
            Assert.IsFalse(Read(record.CreatorRules[source]["OldFeature"].FixedValue));
        }

        [TestMethod]
        public void GateDefaultIsFixedOnlyForRecognizedOldFeatureData()
        {
            string source = Id(), target = Id(); Register(source, target);
            var record = Record(source); record.Mods[source].Remove("EnableMod");
            var result = ModSettingsMigration.MigrateSavegameRecord(record);
            Assert.IsTrue(Read(result.Mods[target]["EnableMod"]));
            Assert.AreEqual(2, result.CreatorRules[target]["EnableMod"].Mode);
            Assert.IsTrue(Read(result.CreatorRules[target]["EnableMod"].FixedValue));
            record.Mods[source].Remove("OldFeature");
            record.CreatorRules[source] = new Dictionary<string, TrailCreatorRule>
            { ["EnableMod"] = new TrailCreatorRule { Mode = 1 } };
            result = ModSettingsMigration.MigrateSavegameRecord(record);
            Assert.IsFalse(result.Mods.ContainsKey(target), "A copied activation rule alone does not identify an old moved feature.");
            Assert.IsFalse(result.CreatorRules.ContainsKey(target));
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void RulesRetainedWhileAnOwnerIsMissingStillMoveWithoutStoredValues(int mode)
        {
            string source = Id(), target = Id(); Register(source, target);
            var raw = Record(source);
            raw.Mods.Clear();
            raw.Kind = (int)GameModeKind.CustomTrail;
            raw.TrailCustomizeAllowed = false;
            raw.CreatorRules[source] = new Dictionary<string, TrailCreatorRule>
            {
                ["OldFeature"] = new TrailCreatorRule { Mode = mode, FixedValue = mode == 2 ? Bool(false) : null },
                ["EnableMod"] = new TrailCreatorRule { Mode = 2, FixedValue = Bool(true) }
            };
            Assert.IsTrue(SavegameModSettings.TryDeserialize(MessagePackSerializer.Serialize(raw), out var decoded),
                "An absent installed owner does not invalidate its retained schema-3 author rules.");
            var result = ModSettingsMigration.MigrateSavegameRecord(decoded);
            Assert.AreEqual(mode, result.CreatorRules[target]["Feature"].Mode);
            Assert.IsFalse(result.CreatorRules[source].ContainsKey("OldFeature"));
            Assert.IsTrue(raw.CreatorRules[source].ContainsKey("OldFeature"));
            var current = new Dictionary<string, Dictionary<string, byte[]>>
            { [target] = Values(("EnableMod", false), ("Feature", true)) };
            var snapshot = Resolve(target, result, result.Mods, current);
            Assert.IsTrue(Read(snapshot["EnableMod"]));
            Assert.AreEqual(mode != 2, Read(snapshot["Feature"]));
            var repeated = ModSettingsMigration.MigrateSavegameRecord(result);
            Assert.AreEqual(mode, repeated.CreatorRules[target]["Feature"].Mode);
        }

        [TestMethod]
        public void DocumentMovesRetainMissionRepresentationsForEnumsFloatsAndTheirArrays()
        {
            string source = Id(), target = Id();
            var types = new[] { typeof(DayOfWeek), typeof(float), typeof(DayOfWeek[]), typeof(float[]) };
            var values = new object[] { "Monday", 0.5d, new object[] { "Monday", "Friday" }, new object[] { 0.5d, 1.25d } };
            var names = new[] { "Enum", "Float", "Enums", "Floats" };
            ModSettingsMigration.RegisterModMove(target, "typed-mission", new ModSettingsModMove(source, target,
                names.Select((name, index) => new ModSettingMove(name, name, types[index]))));
            var raw = new MissionModSettingsDocument();
            raw.Mods[source] = new MissionModSettingsEntry();
            for (int i = 0; i < names.Length; i++) raw.Mods[source].Overrides[names[i]] = values[i];
            var result = ModSettingsMigration.MigrateMissionDocument(raw);
            // Round-trip the actual mission codec, then materialize with the same
            // production conversion used by the destination settings endpoint.
            result = MissionModSettingsJson.ParseObject(MissionModSettingsJson.Serialize(result));
            Assert.AreEqual(DayOfWeek.Monday, ModSettingsPresetJson.ConvertValue(result.Mods[target].Overrides["Enum"], types[0]));
            Assert.AreEqual(0.5f, ModSettingsPresetJson.ConvertValue(result.Mods[target].Overrides["Float"], types[1]));
            CollectionAssert.AreEqual(new[] { DayOfWeek.Monday, DayOfWeek.Friday },
                (DayOfWeek[])ModSettingsPresetJson.ConvertValue(result.Mods[target].Overrides["Enums"], types[2]));
            CollectionAssert.AreEqual(new[] { 0.5f, 1.25f },
                (float[])ModSettingsPresetJson.ConvertValue(result.Mods[target].Overrides["Floats"], types[3]));
            ((System.Collections.IList)result.Mods[target].Overrides["Enums"])[0] = "Sunday";
            Assert.AreEqual("Monday", ((object[])raw.Mods[source].Overrides["Enums"])[0]);
        }

        [TestMethod]
        [DataRow(typeof(DayOfWeek), "Monday", DayOfWeek.Monday)]
        [DataRow(typeof(int), "17", 17)]
        [DataRow(typeof(bool), "false", false)]
        public void MappedDefaultsUseTheDeclaredSaveWireTypeAndRetainTheirDocumentRepresentation(Type type, string value, object expected)
        {
            string source = Id(), target = Id();
            ModSettingsMigration.RegisterModMove(target, "typed-default", new ModSettingsModMove(source, target,
                new[] { Move(), new ModSettingMove("MissingOldTyped", "Typed", type) },
                targetDefaults: new Dictionary<string, object> { ["Typed"] = value }));
            var migrated = ModSettingsMigration.MigrateSavegameRecord(Record(source));
            Assert.AreEqual(expected, MessagePackSerializer.Deserialize(type, migrated.Mods[target]["Typed"]));
            Assert.AreEqual(expected, MessagePackSerializer.Deserialize(type, migrated.CreatorRules[target]["Typed"].FixedValue));
            var document = new MissionModSettingsDocument();
            document.Mods[source] = new MissionModSettingsEntry();
            document.Mods[source].Overrides["OldFeature"] = false;
            var movedDocument = ModSettingsMigration.MigrateMissionDocument(document);
            Assert.AreEqual(value, movedDocument.Mods[target].Overrides["Typed"]);
            Assert.AreEqual(expected, ModSettingsPresetJson.ConvertValue(movedDocument.Mods[target].Overrides["Typed"], type));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ExistingNewOwnerInValuesOrRulesBlocksTheWholeImport(bool rulesOnly)
        {
            string source = Id(), target = Id(); Register(source, target);
            var record = Record(source);
            if (rulesOnly) record.CreatorRules[target] = new Dictionary<string, TrailCreatorRule>();
            else record.Mods[target] = Values(("EnableMod", false));
            record.Mods[source]["OldFeature"] = MessagePackSerializer.Serialize("obsolete and no longer authoritative");
            var result = ModSettingsMigration.MigrateSavegameRecord(record);
            Assert.IsFalse(result.Mods[source].ContainsKey("OldFeature"), "Retired moved names cannot reactivate after a later empty-target normalization.");
            Assert.IsTrue(result.Mods[source].ContainsKey("EnableMod"), "Auxiliary copied names remain with the source.");
            if (rulesOnly) Assert.IsFalse(result.Mods.ContainsKey(target));
            else Assert.AreEqual(1, result.Mods[target].Count);
        }

        [TestMethod]
        public void AuthoredPlayerAndFixedChoicesKeepTheirMeaningAndAreDetached()
        {
            string source = Id(), target = Id(); Register(source, target);
            var document = new MissionModSettingsDocument();
            document.Mods[source] = new MissionModSettingsEntry { PlayerSettings = new[] { "OldFeature", "EnableMod" } };
            var result = ModSettingsMigration.MigrateMissionDocument(document);
            CollectionAssert.AreEquivalent(new[] { "Feature", "EnableMod" }, result.Mods[target].PlayerSettings);
            Assert.IsFalse(result.Mods[target].Overrides.ContainsKey("EnableMod"), "An explicit Player gate outranks a fixed compatibility default.");
            CollectionAssert.AreEqual(new[] { "EnableMod" }, result.Mods[source].PlayerSettings);
            CollectionAssert.AreEqual(new[] { "OldFeature", "EnableMod" }, document.Mods[source].PlayerSettings);
            document.Mods[source] = new MissionModSettingsEntry();
            document.Mods[source].Overrides["OldFeature"] = false;
            result = ModSettingsMigration.MigrateMissionDocument(document);
            Assert.AreEqual(false, result.Mods[target].Overrides["Feature"]);
            Assert.AreEqual(true, result.Mods[target].Overrides["EnableMod"]);
            result.Mods[target].Overrides["Feature"] = true;
            Assert.AreEqual(false, document.Mods[source].Overrides["OldFeature"]);
        }

        [TestMethod]
        public void AuthoredDocumentsWithoutMovedKeysOrWithANewOwnerStayAuthoritative()
        {
            string source = Id(), target = Id(); Register(source, target);
            var document = new MissionModSettingsDocument();
            document.Mods[source] = new MissionModSettingsEntry { PlayerSettings = new[] { "EnableMod" } };
            Assert.IsFalse(ModSettingsMigration.MigrateMissionDocument(document).Mods.ContainsKey(target));
            document.Mods[source].Overrides["OldFeature"] = true;
            document.Mods[target] = new MissionModSettingsEntry();
            var result = ModSettingsMigration.MigrateMissionDocument(document);
            Assert.IsFalse(result.Mods.TryGetValue(source, out var retained) && retained.Overrides.ContainsKey("OldFeature"));
            Assert.AreEqual(0, result.Mods[target].Overrides.Count);
            Assert.AreEqual(0, result.Mods[target].PlayerSettings.Length);
        }

        [TestMethod]
        public void ChainsAreAppliedByDependencyEvenWhenRegisteredInReverseOrder()
        {
            string first = Id(), second = Id(), third = Id();
            ModSettingsMigration.RegisterModMove(third, "second-edge", new ModSettingsModMove(second, third,
                new[] { Move("Middle", "Final") }));
            ModSettingsMigration.RegisterModMove(second, "first-edge", new ModSettingsModMove(first, second,
                new[] { Move("OldFeature", "Middle") }));
            var result = ModSettingsMigration.MigrateSavegameRecord(Record(first));
            Assert.IsFalse(Read(result.Mods[third]["Final"]));
            Assert.IsFalse(result.Mods[first].ContainsKey("OldFeature"));
            Assert.IsFalse(result.Mods.TryGetValue(second, out var remaining) && remaining.ContainsKey("Middle"));
        }

        [TestMethod]
        public void IdenticalRegistrationIsIdempotentAndAmbiguousGraphsAreRejected()
        {
            string source = Id(), target = Id(), other = Id();
            Register(source, target); Register(source, target);
            MustReject(() => ModSettingsMigration.RegisterModMove(target, "release-split",
                new ModSettingsModMove(source, target, new[] { Move("Different", "Feature") })));
            MustReject(() => ModSettingsMigration.RegisterModMove(target, "duplicate-target",
                new ModSettingsModMove(other, target, new[] { Move() })));
            MustReject(() => ModSettingsMigration.RegisterModMove(other, "ambiguous-source",
                new ModSettingsModMove(source, other, new[] { Move() })));
            MustReject(() => ModSettingsMigration.RegisterModMove(other, "moves-a-copied-name",
                new ModSettingsModMove(source, other, new[] { Move("EnableMod", "Feature") })));
            string a = Id(), b = Id();
            ModSettingsMigration.RegisterModMove(b, "forward", new ModSettingsModMove(a, b, new[] { Move() }));
            MustReject(() => ModSettingsMigration.RegisterModMove(a, "cycle",
                new ModSettingsModMove(b, a, new[] { Move("Feature", "OldFeature") })));
            Assert.IsFalse(Read(ModSettingsMigration.MigrateSavegameRecord(Record(source)).Mods[target]["Feature"]),
                "Rejected registrations must not poison the previously valid graph.");
        }

        [TestMethod]
        public void DeclarationsRejectMutableDefaultsAndAmbiguousRegistrationIdentities()
        {
            string source = Id(), target = Id();
            MustReject(() => new ModSettingsModMove(source, target, new[] { Move() },
                targetDefaults: new Dictionary<string, object> { ["EnableMod"] = new List<object> { true } }));
            MustReject(() => new ModSettingsModMove(source + "\nother", target, new[] { Move() }));
            MustReject(() => ModSettingsMigration.RegisterModMove(source, "wrong-owner",
                new ModSettingsModMove(source, target, new[] { Move() })));
            MustReject(() => ModSettingsMigration.RegisterModMove(target, "bad\nid",
                new ModSettingsModMove(source, target, new[] { Move() })));
        }

        [TestMethod]
        public void MalformedRequiredValuesRejectWithoutChangingTheOriginalRecordOrDocument()
        {
            string source = Id(), target = Id(); Register(source, target);
            var record = Record(source);
            record.Mods[source]["OldFeature"] = MessagePackSerializer.Serialize("not a boolean");
            Assert.ThrowsExactly<InvalidDataException>(() => ModSettingsMigration.MigrateSavegameRecord(record));
            Assert.IsFalse(record.Mods.ContainsKey(target));
            record.Mods[source]["OldFeature"] = Bool(true);
            record.CreatorRules[source] = new Dictionary<string, TrailCreatorRule>
            { ["OldFeature"] = new TrailCreatorRule { Mode = 2, FixedValue = MessagePackSerializer.Serialize("bad fixed value") } };
            Assert.ThrowsExactly<InvalidDataException>(() => ModSettingsMigration.MigrateSavegameRecord(record));
            Assert.IsFalse(record.Mods.ContainsKey(target));
            var document = new MissionModSettingsDocument();
            document.Mods[source] = new MissionModSettingsEntry();
            document.Mods[source].Overrides["OldFeature"] = "not a boolean";
            Assert.ThrowsExactly<InvalidDataException>(() => ModSettingsMigration.MigrateMissionDocument(document));
            Assert.IsFalse(document.Mods.ContainsKey(target));
            Assert.AreEqual("not a boolean", document.Mods[source].Overrides["OldFeature"]);
        }

        private sealed class Endpoint : IModSettingsPresetEndpoint
        {
            [SyncHostOnly] public bool EnableMod { get; set; }
            [SyncHostOnly] public bool Feature { get; set; }
            public bool IsMissionPresetActive { get; private set; }
            public Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot() =>
                Values(("EnableMod", false), ("Feature", true));
            public void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable)
            { IsMissionPresetActive = true; EnableMod = Read(snapshot["EnableMod"]); Feature = Read(snapshot["Feature"]); }
            public void System_ExitMissionPreset() { IsMissionPresetActive = false; }
        }

        private sealed class LegacyEndpoint : IModSettingsPresetEndpoint
        {
            [SyncHostOnly] public bool OldFeature { get; set; }
            public bool IsMissionPresetActive { get; private set; }
            public Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot() => Values(("OldFeature", false));
            public void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable)
            { IsMissionPresetActive = true; OldFeature = Read(snapshot["OldFeature"]); }
            public void System_ExitMissionPreset() { IsMissionPresetActive = false; }
        }

        private sealed class CoreEndpoint : IModSettingsPresetEndpoint
        {
            [SyncHostOnly] public bool EnableMod { get; set; }
            [SyncHostOnly] public bool Feature { get; set; }
            [SyncHostOnly] public bool Core { get; set; } = true;
            public bool IsMissionPresetActive => false;
            public Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot() =>
                Values(("EnableMod", false), ("Feature", true), ("Core", true));
            public void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable) { }
            public void System_ExitMissionPreset() { }
        }

        [TestMethod]
        public void RepeatedSessionPreparationPreservesAnExplicitNewOwnerDefaultChoice()
        {
            string source = Id(), target = Id(); Register(source, target, copyGate: false);
            var document = new MissionModSettingsDocument();
            document.Mods[source] = new MissionModSettingsEntry();
            document.Mods[source].Overrides["OldFeature"] = false;
            document.Mods[target] = new MissionModSettingsEntry();
            var newOwner = new Endpoint();
            var participants = new Dictionary<string, IModSettingsPresetEndpoint>
            { [source] = new LegacyEndpoint(), [target] = newOwner };
            var session = new MissionModSettingsSession();
            try
            {
                session.Apply(document, participants, MissionModSettingsApplicationKind.Playable);
                session.Apply(document, participants, MissionModSettingsApplicationKind.Playable);
                Assert.IsTrue(newOwner.Feature,
                    "An explicit new-owner ModDefault choice must still block the retired owner's false after repeated session preparation.");
            }
            finally { session.Exit(); }
        }

        // The resolver is private because normal callers use save admission. Reflection is limited
        // to this real production boundary; no duplicated startup/default/rule algorithm is tested.
        private static Dictionary<string, byte[]> Resolve(string target, SavegameModSettingsRecord record,
            Dictionary<string, Dictionary<string, byte[]>> values, Dictionary<string, Dictionary<string, byte[]>> current,
            IModSettingsPresetEndpoint endpoint = null)
        {
            endpoint = endpoint ?? new Endpoint();
            const BindingFlags members = BindingFlags.NonPublic | BindingFlags.Instance;
            var participantType = typeof(SavegameModSettings).GetNestedType("CompatibleMod", BindingFlags.NonPublic);
            var participant = Activator.CreateInstance(participantType, true);
            participantType.GetField("Endpoint", members).SetValue(participant, endpoint);
            participantType.GetField("Properties", members).SetValue(participant,
                endpoint.GetType().GetProperties().Where(property =>
                    property.IsDefined(typeof(SyncHostOnlyAttribute), true)).ToArray());
            participantType.GetField("PersonalProperties", members).SetValue(participant, Array.Empty<PropertyInfo>());
            return (Dictionary<string, byte[]>)typeof(SavegameModSettings).GetMethod("ResolveStartupSnapshot",
                BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { target, participant, record, values, current });
        }

        [TestMethod]
        public void SavedCurrentAndStrictTrailStartupRetainTheMovedValuesAndAuthorRules()
        {
            string source = Id(), target = Id(); Register(source, target);
            var raw = Record(source); raw.Mods[source].Remove("EnableMod");
            var record = ModSettingsMigration.MigrateSavegameRecord(raw);
            var current = new Dictionary<string, Dictionary<string, byte[]>>
            { [target] = Values(("EnableMod", false), ("Feature", true)) };
            var saved = Resolve(target, record, record.Mods, current);
            Assert.IsTrue(Read(saved["EnableMod"]));
            Assert.IsFalse(Read(saved["Feature"]));
            var chosenCurrent = Resolve(target, record, current, current);
            Assert.IsFalse(Read(chosenCurrent["EnableMod"]), "Choosing current settings must retain an explicitly disabled new owner.");
            Assert.IsTrue(Read(chosenCurrent["Feature"]));

            raw.Kind = (int)GameModeKind.CustomTrail; raw.TrailCustomizeAllowed = false;
            raw.CreatorRules[source] = new Dictionary<string, TrailCreatorRule>
            { ["OldFeature"] = new TrailCreatorRule { Mode = 2, FixedValue = Bool(false) } };
            record = ModSettingsMigration.MigrateSavegameRecord(raw);
            var strict = Resolve(target, record, current, current);
            Assert.IsTrue(Read(strict["EnableMod"]), "The compatibility gate is fixed author data in a strict Trail.");
            Assert.IsFalse(Read(strict["Feature"]), "Fixed old author data outranks current settings.");

            raw.CreatorRules[source]["OldFeature"] = new TrailCreatorRule { Mode = 1 };
            record = ModSettingsMigration.MigrateSavegameRecord(raw);
            var player = Resolve(target, record, record.Mods, current);
            Assert.IsTrue(Read(player["Feature"]), "Player rules resolve against the new owner's current property, not the retired owner's defaults.");
        }

        [TestMethod]
        public void StrictTrailNeverEnablesAnExplicitlyDisabledOldOwnerThroughAGeneratedGateDefault()
        {
            string source = Id(), target = Id(); Register(source, target);
            var raw = Record(source);
            raw.Kind = (int)GameModeKind.CustomTrail;
            raw.TrailCustomizeAllowed = false;
            var record = ModSettingsMigration.MigrateSavegameRecord(raw);
            var current = new Dictionary<string, Dictionary<string, byte[]>>
            { [target] = Values(("EnableMod", true), ("Feature", true)) };
            Assert.IsFalse(Read(Resolve(target, record, record.Mods, current)["EnableMod"]),
                "A compatibility default cannot turn an old explicitly disabled mod on when strict author rules are materialized.");
        }

        [TestMethod]
        public void CurrentChoiceIgnoresUnusedMalformedStoredValuesButSavedOnlyTrailsStillRejectThem()
        {
            string source = Id(), target = Id(); Register(source, target);
            var raw = Record(source);
            raw.Mods[source]["OldFeature"] = MessagePackSerializer.Serialize("unused old value");
            raw.CreatorRules[source] = new Dictionary<string, TrailCreatorRule>
            { ["OldFeature"] = new TrailCreatorRule { Mode = 2, FixedValue = MessagePackSerializer.Serialize("unused fixed value") } };
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var choice = typeof(SavegameModSettings).GetField("useChosenCurrent", flags);
            var normalize = typeof(SavegameModSettings).GetMethod("NormalizeRecordForLoad", flags);
            object previous = choice.GetValue(null);
            try
            {
                choice.SetValue(null, true);
                var record = (SavegameModSettingsRecord)normalize.Invoke(null, new object[] { raw });
                var current = new Dictionary<string, Dictionary<string, byte[]>>
                { [target] = Values(("EnableMod", false), ("Feature", true)) };
                var snapshot = Resolve(target, record, current, current);
                Assert.IsFalse(Read(snapshot["EnableMod"]));
                Assert.IsTrue(Read(snapshot["Feature"]));
                choice.SetValue(null, false);
                var savedError = Assert.ThrowsExactly<TargetInvocationException>(() => normalize.Invoke(null, new object[] { raw }));
                Assert.IsInstanceOfType<InvalidDataException>(savedError.InnerException);
                choice.SetValue(null, true);
                raw.Kind = (int)GameModeKind.CustomTrail;
                raw.TrailCustomizeAllowed = false;
                var strictError = Assert.ThrowsExactly<TargetInvocationException>(() => normalize.Invoke(null, new object[] { raw }));
                Assert.IsInstanceOfType<InvalidDataException>(strictError.InnerException);
            }
            finally { choice.SetValue(null, previous); }
        }

        [TestMethod]
        public void StrictTrailCompatibilityDefaultsKeepANewCoreOffWhenTheMasterGateIsFixedOrPlayerEnabled()
        {
            string source = Id(), target = Id();
            ModSettingsMigration.RegisterModMove(target, "split-with-new-core", new ModSettingsModMove(
                source, target, new[] { Move() }, new[] { Move("EnableMod", "EnableMod") },
                new Dictionary<string, object> { ["EnableMod"] = true, ["Core"] = false }));
            var raw = Record(source);
            raw.Kind = (int)GameModeKind.CustomTrail;
            raw.TrailCustomizeAllowed = false;
            raw.Mods[source]["EnableMod"] = Bool(true);
            raw.CreatorRules[source] = new Dictionary<string, TrailCreatorRule>
            { ["EnableMod"] = new TrailCreatorRule { Mode = 2, FixedValue = Bool(true) } };
            var current = new Dictionary<string, Dictionary<string, byte[]>>
            { [target] = Values(("EnableMod", true), ("Feature", true), ("Core", true)) };
            var fixedRecord = ModSettingsMigration.MigrateSavegameRecord(raw);
            var fixedSnapshot = Resolve(target, fixedRecord, fixedRecord.Mods, current, new CoreEndpoint());
            Assert.IsTrue(Read(fixedSnapshot["EnableMod"]));
            Assert.IsFalse(Read(fixedSnapshot["Core"]), "A new default-on engine must remain opt-out in an old strict Trail even when its old master gate was explicitly enabled.");
            raw.CreatorRules[source]["EnableMod"] = new TrailCreatorRule { Mode = 1 };
            var playerRecord = ModSettingsMigration.MigrateSavegameRecord(raw);
            var playerSnapshot = Resolve(target, playerRecord, playerRecord.Mods, current, new CoreEndpoint());
            Assert.IsTrue(Read(playerSnapshot["EnableMod"]));
            Assert.IsFalse(Read(playerSnapshot["Core"]), "Resolving the old master gate from the new owner's enabled Player preference must not enable the new core.");
        }

        [TestMethod]
        public void SavedRulesKeepTheirNewOwnerThroughWireRoundTripActiveCaptureAndResaveNormalization()
        {
            string source = Id(), target = Id(); Register(source, target);
            var raw = Record(source);
            raw.Kind = (int)GameModeKind.CustomTrail;
            raw.TrailCustomizeAllowed = false;
            raw.CreatorRules[source] = new Dictionary<string, TrailCreatorRule>
            {
                ["OldFeature"] = new TrailCreatorRule { Mode = 2, FixedValue = Bool(false) },
                ["EnableMod"] = new TrailCreatorRule { Mode = 1 }
            };
            var first = ModSettingsMigration.MigrateSavegameRecord(RoundTrip(raw));
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var activeRules = typeof(SavegameModSettings).GetField("activeCreatorRules", flags);
            object previous = activeRules.GetValue(null);
            try
            {
                // Supply the completed load's effective metadata at the existing capture boundary.
                // CaptureCreatorRules is the production resave path; no policy is reimplemented here.
                activeRules.SetValue(null, first.CreatorRules);
                var mode = new GameModeSnapshot(false, true, false, true, false, false, true, false, true,
                    false, false, 0, 0, 0, 0, 0, -1, -1, -1, 0, GameModeKind.CustomTrail,
                    GameModeLaunchVariant.Standard, 0, -1, -1, -1, 0, false);
                var mission = new MissionContext(100101, MissionStartKind.LoadedSave, mode, "save", "save", false);
                var capturedRules = (Dictionary<string, Dictionary<string, TrailCreatorRule>>)
                    typeof(SavegameModSettings).GetMethod("CaptureCreatorRules", flags).Invoke(null, new object[] { mission });
                Assert.AreEqual(2, capturedRules[target]["Feature"].Mode);
                Assert.IsFalse(Read(capturedRules[target]["Feature"].FixedValue));
                Assert.IsFalse(capturedRules[source].ContainsKey("OldFeature"));
                first.CreatorRules = capturedRules;
                // The live snapshot may differ; the retained strict author rule must still win.
                first.Mods[target]["Feature"] = Bool(true);
                var second = ModSettingsMigration.MigrateSavegameRecord(RoundTrip(first));
                Assert.IsTrue(Read(second.Mods[target]["Feature"]), "An explicit current-owner snapshot remains authoritative on a second normalization.");
                Assert.AreEqual(1, second.CreatorRules[target]["EnableMod"].Mode);
                var current = new Dictionary<string, Dictionary<string, byte[]>>
                { [target] = Values(("EnableMod", true), ("Feature", true)) };
                var snapshot = Resolve(target, second, second.Mods, current);
                Assert.IsTrue(Read(snapshot["EnableMod"]));
                Assert.IsFalse(Read(snapshot["Feature"]), "The reloaded fixed rule must still address the moved property under its new GUID.");
            }
            finally { activeRules.SetValue(null, previous); }
        }

        private static SavegameModSettingsRecord RoundTrip(SavegameModSettingsRecord record)
        {
            Assert.IsTrue(SavegameModSettings.TryDeserialize(MessagePackSerializer.Serialize(record), out var restored),
                "The real save-record formatter and wire validator must accept the effective migration record.");
            return restored;
        }

        private static void MustReject(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            Assert.Fail("A conflicting migration declaration was accepted.");
        }
    }
}
