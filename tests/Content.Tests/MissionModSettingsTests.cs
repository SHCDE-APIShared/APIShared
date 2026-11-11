using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Content.Tests
{
    [TestClass]
    public class MissionModSettingsTests
    {
        [TestMethod]
        public void SchemaThreeRoundtripsArbitraryModsWithDeterministicOrdering()
        {
            const string json = "{\"schemaVersion\":3,\"mods\":{\"third.party\":{\"playerSettings\":[\"z\",\"a\",\"z\"],\"overrides\":{\"values\":[true,7,\"text\"],\"number\":2.5}},\"empty\":{\"playerSettings\":[],\"overrides\":{}}}}";
            var document = MissionModSettingsJson.ParseObject(json);
            CollectionAssert.AreEqual(new[] { "a", "z" }, document.Mods["third.party"].PlayerSettings);
            Assert.AreEqual(1, document.Mods.Count);
            string serialized = MissionModSettingsJson.Serialize(document);
            Assert.IsTrue(serialized.EndsWith("\r\n", StringComparison.Ordinal));
            Assert.IsTrue(serialized.IndexOf("\"number\"", StringComparison.Ordinal) < serialized.IndexOf("\"values\"", StringComparison.Ordinal));
            Assert.AreEqual(serialized, MissionModSettingsJson.Serialize(MissionModSettingsJson.ParseObject(serialized)));
            Assert.AreEqual(0, MissionModSettingsDocument.CreateModDefaults().Mods.Count);
            Assert.AreEqual(3, MissionModSettingsJson.NormalizeAndValidate(null, null).SchemaVersion);
        }

        [TestMethod]
        public void DecodedObjectsUseTheSameSchemaValidation()
        {
            var root = new Dictionary<string, object> {
                ["schemaVersion"] = 3,
                ["mods"] = new Dictionary<string, object> {
                    ["arbitrary"] = new Dictionary<string, object> {
                        ["playerSettings"] = new List<object> { "choice" },
                        ["overrides"] = new Dictionary<string, object> { ["fixed"] = true }
                    }
                }
            };
            Assert.AreEqual(true, MissionModSettingsJson.ParseDocument(root).Mods["arbitrary"].Overrides["fixed"]);
            root["schemaVersion"] = 3L;
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsJson.ParseDocument(root));
        }

        [TestMethod]
        public void InvalidSchemasShapesNamesAndConflictsAreRejected()
        {
            foreach (string json in new[] {
                "[]", "{}", "{\"schemaVersion\":2,\"mods\":{}}", "{\"schemaVersion\":3,\"mods\":[]}",
                "{\"schemaVersion\":3,\"mods\":{\"a\":{\"playerSettings\":[],\"overrides\":null}}}",
                "{\"schemaVersion\":3,\"mods\":{\"a\":{\"playerSettings\":[\" padded\"],\"overrides\":{}}}}",
                "{\"schemaVersion\":3,\"mods\":{\"a\":{\"playerSettings\":[\"same\"],\"overrides\":{\"same\":true}}}}",
                "{\"schemaVersion\":3,\"mods\":{\"a\":{\"playerSettings\":[],\"overrides\":{\"nested\":[[1]]}}}}"
            })
                Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsJson.ParseObject(json), json);
        }

        [TestMethod]
        public void LimitsCountUtf8BytesAndNamesBeforeDeduplication()
        {
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsJson.ParseObject(null));
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsJson.ParseObject(new string('\u20ac', MissionModSettingsJson.MaximumBytes / 3 + 1)));
            var entry = new MissionModSettingsEntry {
                PlayerSettings = Enumerable.Repeat("duplicate", MissionModSettingsJson.MaximumSettingsPerMod).ToArray()
            };
            var document = Document(entry);
            MissionModSettingsJson.NormalizeAndValidate(document, null);
            Assert.AreEqual(1, entry.PlayerSettings.Length);
            entry.PlayerSettings = Enumerable.Repeat("duplicate", MissionModSettingsJson.MaximumSettingsPerMod + 1).ToArray();
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsJson.NormalizeAndValidate(document, null));
            Assert.AreEqual(MissionModSettingsJson.MaximumSettingsPerMod + 1, entry.PlayerSettings.Length);
        }

        [TestMethod]
        public void NormalizationPreservesEntryAliasesAndPartialMutationsOnFailure()
        {
            var first = new MissionModSettingsEntry { PlayerSettings = new[] { "z", "a", "z" } };
            var document = Document(first);
            document.Mods["alias"] = first;
            var invalid = new MissionModSettingsEntry { PlayerSettings = new[] { " bad" } };
            document.Mods["z-invalid"] = invalid;
            var dictionary = document.Mods;
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsJson.NormalizeAndValidate(document, "source"));
            Assert.AreSame(dictionary, document.Mods);
            CollectionAssert.AreEqual(new[] { "a", "z" }, first.PlayerSettings);
            Assert.AreSame(first, document.Mods["alias"]);
            document.Mods.Remove("z-invalid");
            Assert.AreSame(document, MissionModSettingsJson.NormalizeAndValidate(document, "source"));
            Assert.AreNotSame(dictionary, document.Mods);
            Assert.AreSame(first, document.Mods["alias"]);
        }

        [TestMethod]
        public void PruningMutatesExistingDictionaryAndRetainsPlayerOrdering()
        {
            var entry = new MissionModSettingsEntry {
                PlayerSettings = new[] { "z", "removed", "a", "z" },
                Overrides = new Dictionary<string, object> { ["obsolete"] = true, ["fixed"] = 7 }
            };
            var document = Document(entry);
            var dictionary = document.Mods;
            var overrides = entry.Overrides;
            CollectionAssert.AreEqual(new[] { "obsolete", "removed" }, MissionModSettingsJson.RemoveUnknownSettings(document, "mod", new[] { "a", "z", "fixed" }));
            Assert.AreSame(dictionary, document.Mods);
            Assert.AreSame(overrides, entry.Overrides);
            CollectionAssert.AreEqual(new[] { "z", "a", "z" }, entry.PlayerSettings);
            MissionModSettingsJson.RemoveUnknownSettings(document, "mod", null);
            Assert.AreEqual(0, dictionary.Count);
        }

        [TestMethod]
        public void UnsupportedScalarsAndNestedValuesKeepTheirHistoricalRules()
        {
            foreach (object value in new object[] { true, "x", (byte)1, (sbyte)-1, (short)2, (ushort)2, 3, 3U, 4L, 4UL, 1.5m, 1.5d, Array.Empty<object>(), new object[] { 1, "x", false } })
                Assert.IsTrue(MissionModSettingsJson.IsSupportedValue(value));
            foreach (object value in new object[] { null, 1f, new Dictionary<string, object>(), new object[] { null }, new object[] { new[] { 1 } } })
                Assert.IsFalse(MissionModSettingsJson.IsSupportedValue(value));
        }

        [TestMethod]
        public void OversizeSerializationStillExposesNormalization()
        {
            var entry = new MissionModSettingsEntry {
                PlayerSettings = new[] { "z", "a", "a" },
                Overrides = new Dictionary<string, object> { ["text"] = new string('x', MissionModSettingsJson.MaximumBytes) }
            };
            var document = Document(entry);
            var original = document.Mods;
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsJson.Serialize(document));
            Assert.AreNotSame(original, document.Mods);
            CollectionAssert.AreEqual(new[] { "a", "z" }, entry.PlayerSettings);
        }

        private static MissionModSettingsDocument Document(MissionModSettingsEntry entry) =>
            new MissionModSettingsDocument { Mods = new Dictionary<string, MissionModSettingsEntry> { ["mod"] = entry } };
    }
}
