using APIShared.Presentation;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using SHCDESE.API.Components.Network;
using SHCDESE.Interop.Enums;
using SHCDESE.API;
using System.Runtime.Serialization;
using Mono.Cecil;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class PackagedLordPresentationTests
    {
        [TestMethod]
        public void DistinctLordsWithTheSameSubtypeKeepTheirOwnFullNames()
        {
            var one = new PackagedLordPresentationEntry(3, "123\\Alpha", "Alpha", "{\"LocalizedTitles\":{\"en-US\":[\", The First\"]}}");
            var two = new PackagedLordPresentationEntry(4, "456\\Beta", "Beta", "{}");
            Assert.AreEqual("Alpha, The First", one.ResolveName("de-DE", 0));
            Assert.AreEqual("Beta", two.ResolveName("de-DE", 0));
            Assert.AreEqual("Beta", two.ResolveName("de-DE", 7));
        }
        [TestMethod]
        public void InstalledVanillaStartHasOneEncoderAndTheInjectionPreservesItsReceiver()
        {
            var target = typeof(Platform_Multiplayer).GetMethod("StartGame", new[] { typeof(EngineInterface.MultiplayerSetupData), typeof(FileHeader), typeof(int), typeof(int) });
            using (var copy = new DynamicMethodDefinition(target))
            using (var context = new ILContext(copy.Definition))
            {
                int before = context.Body.Instructions.Count;
                CoopLordRestartPresentation.Patch(context);
                Assert.IsTrue(context.Body.Instructions.Count > before);
                Assert.AreEqual(1, context.Body.Instructions.Count(i => i.Operand is Mono.Cecil.MethodReference m && m.Name == "encode" && m.DeclaringType.Name == "RestartMPInfo"));
                Assert.IsTrue(context.Body.Instructions.Any(i => i.OpCode == OpCodes.Dup));
            }
        }
        [TestMethod]
        public void NonCoopAndAbsentLobbyPreserveExistingRestartNames()
        {
            var restart = new HUD_IngameMenu.RestartMPInfo(); restart.LordNames[2] = "Preserved";
            CoopLordRestartPresentation.Populate(restart, null, 0);
            CoopLordRestartPresentation.Populate(restart, null, 1);
            Assert.AreEqual("Preserved", restart.LordNames[2]);
        }
        [TestMethod]
        public void PopulatedObjectAndItsEncodedRestartRetainDistinctWorkshopLordNames()
        {
            var restart = new HUD_IngameMenu.RestartMPInfo();
            var originalTypes = restart.LordType.ToArray();
            foreach (int id in new[] { 3, 5, 8 })
                CoopLordRestartPresentation.ApplySelection(restart, id, 31, new FRONT_Multiplayer.MPAIVInfo {
                    lordType = 31, builtInLord = false, lordName = "123\\Lord " + id });
            var decoded = HUD_IngameMenu.RestartMPInfo.decode(restart.encode());
            foreach (int id in new[] { 3, 5, 8 }) Assert.AreEqual("123\\Lord " + id, decoded.LordNames[id - 1]);
            CollectionAssert.AreEqual(originalTypes, restart.LordType, "Presentation must preserve the gameplay type array.");
            CoopLordRestartPresentation.ApplySelection(restart, 3, 30, new FRONT_Multiplayer.MPAIVInfo {
                lordType = 31, builtInLord = false, lordName = "Wrong selection" });
            Assert.AreEqual("123\\Lord 3", restart.LordNames[2]);
            CoopLordRestartPresentation.ApplySelection(restart, 3, 31, new FRONT_Multiplayer.MPAIVInfo {
                lordType = 31, builtInLord = false, lordName = "host\\Lord 3" }, "peer\\Lord 3");
            Assert.AreEqual("peer\\Lord 3", HUD_IngameMenu.RestartMPInfo.decode(restart.encode()).LordNames[2],
                "The host's optional media alias must not hide the peer's authenticated presentation.");
        }
        [TestMethod]
        public void ClearedAndConflictingSelectionsNeverClaimAnUnrelatedLord()
        {
            var entries = typeof(PackagedLordPresentation).GetField("entries", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = entries.GetValue(null);
            try
            {
                var one = new PackagedLordPresentationEntry(3, "Alpha", "Alpha", "{}");
                entries.SetValue(null, new[] { one });
                Assert.IsTrue(PackagedLordPresentation.Claims(3, "alpha"));
                Assert.IsFalse(PackagedLordPresentation.Claims(3, "Beta"));
                entries.SetValue(null, new[] { one, new PackagedLordPresentationEntry(3, "Beta", "Beta", "{}") });
                Assert.IsFalse(PackagedLordPresentation.Claims(3, "Alpha"));
                var identityOnly = new PackagedLordPresentationEntry(3, "Alpha", "Alpha", null);
                entries.SetValue(null, new[] { identityOnly });
                Assert.IsTrue(PackagedLordPresentation.Claims(3, "Alpha"));
                var restart = new HUD_IngameMenu.RestartMPInfo();
                CoopLordRestartPresentation.ApplySelection(restart, 3, 31, new FRONT_Multiplayer.MPAIVInfo {
                    lordType = 31, builtInLord = false, lordName = "host\\Alpha" }, PackagedLordPresentation.RestartIdentity(3));
                Assert.AreEqual("Alpha", HUD_IngameMenu.RestartMPInfo.decode(restart.encode()).LordNames[2],
                    "A metadata-free identity must still populate Coop restart/save names.");
                entries.SetValue(null, new[] { identityOnly, one });
                Assert.IsFalse(PackagedLordPresentation.Claims(3, "Alpha"),
                    "Absent metadata and an authored empty document have different runtime policies.");
                entries.SetValue(null, Array.Empty<PackagedLordPresentationEntry>());
                Assert.IsFalse(PackagedLordPresentation.Claims(3, "Alpha"));
            }
            finally { entries.SetValue(null, previous); }
        }
        [TestMethod]
        public void MediaAvailabilityCannotChangeClipSelectionAndSubtitlesUseAuthoredMetadata()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(PackagedLordPresentation);
            var entries = type.GetField("entries", flags);
            var rng = typeof(DeterministicRandom).GetField("_rng", flags);
            var spoken = (Dictionary<string, string>)type.GetField("spoken", flags).GetValue(null);
            var previousEntries = entries.GetValue(null); var previousRng = rng.GetValue(null);
            var previousSpeech = new Dictionary<string, string>(spoken);
            try
            {
                string json = "{\"Messages\":{\"Taunt1\":[{\"AudioPath\":\"first\",\"LocalizedText\":{\"en-US\":\"First line\"}},{\"AudioPath\":\"second\",\"LocalizedText\":{\"en-US\":\"Second line\"}}]}}";
                string selectedText = null;
                foreach (string provider in new string[] { null, "private.owner" })
                {
                    entries.SetValue(null, new[] { new PackagedLordPresentationEntry(3, "Lord", "Authored", json, provider) });
                    var random = new Random(123); rng.SetValue(null, random);
                    object[] call = { "Lord", AILordMessageType.Taunt1, null, null, null };
                    Assert.IsTrue((bool)type.GetMethod("TryResolveMedia", flags).Invoke(null, call), "A claimed Lord is answered by the packaged presentation.");
                    bool available = (bool)call[2];
                    Assert.AreEqual(provider != null, available);
                    object[] subtitle = { null, "Lord", null };
                    Assert.IsTrue((bool)type.GetMethod("Speech", flags).Invoke(null, subtitle));
                    if (selectedText == null) selectedText = (string)subtitle[2]; else Assert.AreEqual(selectedText, subtitle[2]);
                    var reference = new Random(123); reference.Next(0, 2);
                    Assert.AreEqual(reference.Next(), random.Next(), "Local file availability changed synchronized RNG consumption.");
                    if (available) StringAssert.StartsWith((string)call[4], "_SLA_/private.owner/");
                }
                foreach (string provider in new string[] { null, "private.owner" })
                {
                    entries.SetValue(null, new[] { new PackagedLordPresentationEntry(3, "Lord", "Authored", null, provider) });
                    var random = new Random(123); rng.SetValue(null, random);
                    object[] call = { "Lord", AILordMessageType.Taunt1, null, null, null };
                    Assert.IsTrue((bool)type.GetMethod("TryResolveMedia", flags).Invoke(null, call));
                    Assert.IsFalse((bool)call[2], "An identity-only claim must not select optional peer-local SE clips.");
                    Assert.AreEqual("", call[3]); Assert.AreEqual("", call[4]);
                    Assert.AreEqual(new Random(123).Next(), random.Next(), "A metadata-free claim consumed synchronized RNG.");
                }
                foreach (int secondSlot in new[] { 3, 4 }) foreach (bool reverse in new[] { false, true })
                {
                    // Same slot models flattened registrations from two owners; different slots model two selected Lords.
                    var claims = new[] { new PackagedLordPresentationEntry(3, "Lord", "Authored", null),
                        new PackagedLordPresentationEntry(secondSlot, "Lord", "Authored", "{}") };
                    entries.SetValue(null, reverse ? claims.Reverse().ToArray() : claims);
                    var random = new Random(123); rng.SetValue(null, random);
                    object[] call = { "Lord", AILordMessageType.Taunt1, null, null, null };
                    Assert.IsTrue((bool)type.GetMethod("TryResolveMedia", flags).Invoke(null, call),
                        "Different title fallback policies must not release a compatible media claim to local SE data.");
                    Assert.IsFalse((bool)call[2]); Assert.AreEqual("", call[3]); Assert.AreEqual("", call[4]);
                    Assert.AreEqual(new Random(123).Next(), random.Next(), "A title-policy conflict changed synchronized RNG consumption.");
                }
            }
            finally
            {
                entries.SetValue(null, previousEntries); rng.SetValue(null, previousRng); spoken.Clear();
                foreach (var pair in previousSpeech) spoken.Add(pair.Key, pair.Value);
            }
        }

        [TestMethod]
        public void MetadataFreeNamesUseRealVanillaTitlesWithoutReplacingTheAuthoredAlias()
        {
            using (var fixture = new LegacyTitleFixture())
            {
                var entry = new PackagedLordPresentationEntry(3, "peer\\Donor", "Authored", null);
                fixture.SetEntries(entry);
                for (int nickname = 0; nickname < 8; nickname++)
                {
                    fixture.LegacyText = null;
                    Assert.AreEqual("Authored Vanilla " + nickname, fixture.Name((int)Enums.AILords.SK_X1 + 2, nickname));
                    Assert.AreEqual("Authored Vanilla " + nickname, fixture.PlayerName(3, nickname));
                    fixture.LegacyText = ", Legacy " + nickname;
                    Assert.AreEqual("Authored, Legacy " + nickname, fixture.Name((int)Enums.AILords.SK_X1 + 2, nickname));
                    Assert.AreEqual("Authored, Legacy " + nickname, fixture.PlayerName(3, nickname));
                }
                int lookups = fixture.LegacyLookups;
                Assert.AreEqual("Authored", fixture.Name((int)Enums.AILords.SK_X1 + 2, -1));
                Assert.AreEqual("Authored", fixture.Name((int)Enums.AILords.SK_X1 + 2, 8));
                Assert.AreEqual("Authored", fixture.PlayerName(3, -1));
                Assert.AreEqual("Authored", fixture.PlayerName(3, 8));
                Assert.AreEqual(lookups, fixture.LegacyLookups, "Invalid nicknames must not index legacy title tables.");
                CustomisationFileManager.CustomMediaExists = false;
                Assert.AreEqual("Authored Vanilla 0", fixture.Name((int)Enums.AILords.SK_X1 + 2, 0));
                Assert.AreEqual("Authored Vanilla 0", fixture.PlayerName(3, 0));
                Assert.AreEqual(lookups, fixture.LegacyLookups, "Without custom media Vanilla must not query text.txt.");
                Assert.AreEqual("original-name", fixture.Name((int)Enums.AILords.SK_X1, 0));
                Assert.AreEqual("original-player", fixture.PlayerName(3, 0, exclude: true));

                object[] title = { null, entry.InternalName, null, 0 };
                Assert.IsFalse((bool)Production("TitleText").Invoke(null, title));
                Assert.AreEqual("", title[2]);
                // An explicit empty document remains a complete authored presentation.
                fixture.SetEntries(new PackagedLordPresentationEntry(3, entry.InternalName, entry.AuthoredName, "{}"));
                Assert.IsTrue((bool)Production("TitleText").Invoke(null, title));
                Assert.AreEqual("", title[2]);
                Assert.AreEqual("Authored", fixture.Name((int)Enums.AILords.SK_X1 + 2, 0));

                foreach (int secondSlot in new[] { 3, 4 }) foreach (bool reverse in new[] { false, true })
                {
                    var claims = new[] { entry, new PackagedLordPresentationEntry(secondSlot, entry.InternalName, entry.AuthoredName, "{}") };
                    fixture.SetEntries(reverse ? claims.Reverse().ToArray() : claims);
                    Assert.IsTrue((bool)Production("IsSupported").Invoke(null, new object[] { null, entry.InternalName }));
                    object[] text = { null, entry.InternalName, null };
                    Assert.IsTrue((bool)Production("Display").Invoke(null, text)); Assert.AreEqual("Authored", text[2]);
                    Assert.IsTrue((bool)Production("DetailText").Invoke(null, new object[] { null, entry.InternalName, null }));
                    Assert.IsFalse((bool)Production("Portrait").Invoke(null, new object[] { null, entry.InternalName, null }));
                    Production("Speech").Invoke(null, new object[] { null, entry.InternalName, null });
                    foreach (string callback in new[] { "Join", "Leave" })
                    {
                        text[2] = null;
                        Assert.IsFalse((bool)Production(callback).Invoke(null, text)); Assert.AreEqual("", text[2]);
                    }
                    Assert.IsFalse((bool)Production("LuaPath").Invoke(null, new object[] { fixture.Ai, entry.InternalName, null }));
                    Assert.IsFalse((bool)Production("TitleText").Invoke(null, title)); Assert.AreEqual("", title[2]);
                    fixture.LegacyText = ", original custom text";
                    Assert.AreEqual(fixture.LegacyText, Production("Custom").Invoke(null,
                        new object[] { null, entry.InternalName.ToLowerInvariant(), 34 }),
                        "A title-policy conflict must retain the original custom-text result rather than choose either claim.");
                }
                object[] unclaimedTitle = { null, "unclaimed", null, 0 };
                Assert.IsTrue((bool)Production("TitleText").Invoke(null, unclaimedTitle));
                Assert.AreEqual("original-title", unclaimedTitle[2], "An unrelated identity must retain the original title resolver.");
            }
        }

        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private static MethodInfo Production(string name) => typeof(PackagedLordPresentation).GetMethod(name, PrivateStatic);
        private static MethodInfo FixtureMethod(string name) => typeof(LegacyTitleFixture).GetMethod(name, PrivateStatic);

        // Execute copies of the actual runtime callbacks and installed Vanilla resolver. Only
        // Unity singletons, translation content and the pre-existing custom-text trampoline are doubled.
        // No live hooks are installed, and every modified process-wide field is restored.
        private sealed class LegacyTitleFixture : IDisposable
        {
            private static LegacyTitleFixture current;
            private readonly FieldInfo entries = typeof(PackagedLordPresentation).GetField("entries", PrivateStatic);
            private readonly object previousEntries;
            private readonly List<Tuple<PropertyInfo, object, object>> originals = new List<Tuple<PropertyInfo, object, object>>();
            private readonly bool previousMedia = CustomisationFileManager.CustomMediaExists;
            private readonly Func<int, int, string, string> vanilla;
            private readonly Func<PackagedLordPresentationEntry, int, string> runtime;
            private readonly Func<Platform_Multiplayer, int, bool, bool, string> player;
            private readonly GameData gameData = (GameData)FormatterServices.GetUninitializedObject(typeof(GameData));
            private readonly Platform_Multiplayer platform = (Platform_Multiplayer)FormatterServices.GetUninitializedObject(typeof(Platform_Multiplayer));
            private readonly EngineInterface.PlayState state = new EngineInterface.PlayState { computer_names = new short[9] };
            internal readonly Func<int, int, string> Name;
            internal string LegacyText;
            internal int LegacyLookups;
            internal readonly GameAIManagerAPI Ai = (GameAIManagerAPI)FormatterServices.GetUninitializedObject(typeof(GameAIManagerAPI));

            internal LegacyTitleFixture()
            {
                previousEntries = entries.GetValue(null); current = this;
                try
                {
                    SetOriginal("customText", nameof(OriginalCustom));
                    SetOriginal("names", nameof(OriginalName));
                    SetOriginal("playerNames", nameof(OriginalPlayer));
                    SetOriginal("titles", nameof(OriginalTitle));
                    SetOriginal("supported", nameof(ForbiddenSupported));
                    foreach (string field in new[] { "display", "speech", "join", "leave", "lua" })
                        SetOriginal(field, nameof(ForbiddenOptionalText));
                    SetOriginal("details", nameof(ForbiddenDetails));
                    SetOriginal("faces", nameof(ForbiddenFace));
                    typeof(GameAIManagerAPI).GetProperty("LordDataDict").GetSetMethod(true).Invoke(Ai,
                        new object[] { new Dictionary<string, SHCDESE.API.Components.AI.CustomLordEntry>() });
                    CustomisationFileManager.CustomMediaExists = true;
                    platform.gameMembers = new List<Platform_Multiplayer.MPGameMember> {
                        new Platform_Multiplayer.MPGameMember { playerID = 3, skirmishAI = true } };
                    typeof(GameData).GetField("_lastGameState", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(gameData, state);
                    vanilla = Copy<Func<int, int, string, string>>(typeof(Platform_Multiplayer.MPLobbyMember).GetMethod("GetName"), method =>
                    {
                        if (method.DeclaringType.FullName == typeof(Translate).FullName)
                            return FixtureMethod(method.Name == "get_Instance" ? nameof(TranslationInstance) :
                                method.Parameters[1].ParameterType.FullName == typeof(Enums.eTextValues).FullName ? nameof(TranslationValue) : nameof(TranslationIndex));
                        if (method.DeclaringType.FullName == typeof(CustomisationFileManager).FullName)
                            return FixtureMethod(method.Name == "get_Instance" ? nameof(CustomInstance) : nameof(CustomText));
                        return null;
                    });
                    runtime = Copy<Func<PackagedLordPresentationEntry, int, string>>(Production("RuntimeName"), method =>
                        method.DeclaringType.FullName == typeof(Platform_Multiplayer.MPLobbyMember).FullName.Replace('+', '/') && method.Name == "GetName"
                            ? FixtureMethod(nameof(VanillaTitle)) : null);
                    Name = Copy<Func<int, int, string>>(Production("Name"), RuntimeBoundary);
                    player = Copy<Func<Platform_Multiplayer, int, bool, bool, string>>(Production("Player"), method =>
                        method.DeclaringType.FullName == typeof(GameData).FullName && method.Name == "get_Instance"
                            ? FixtureMethod(nameof(GameInstance)) : RuntimeBoundary(method));
                }
                catch { Dispose(); throw; }
            }
            internal void SetEntries(params PackagedLordPresentationEntry[] value) => entries.SetValue(null, value);
            internal string PlayerName(int id, int nickname, bool exclude = false)
            { state.computer_names[id] = (short)nickname; return player(platform, id, false, exclude); }
            private static MethodInfo RuntimeBoundary(MethodReference method) =>
                method.DeclaringType.FullName == typeof(PackagedLordPresentation).FullName && method.Name == "RuntimeName"
                    ? FixtureMethod(nameof(RuntimeName)) : null;
            private static T Copy<T>(MethodInfo method, Func<MethodReference, MethodInfo> boundary) where T : Delegate
            {
                using (var copy = new DynamicMethodDefinition(method))
                {
                    foreach (var instruction in copy.Definition.Body.Instructions)
                        if (instruction.Operand is MethodReference called)
                        {
                            var replacement = boundary(called); if (replacement == null) continue;
                            instruction.OpCode = OpCodes.Call; instruction.Operand = copy.Definition.Module.ImportReference(replacement);
                        }
                    return (T)copy.Generate().CreateDelegate(typeof(T));
                }
            }
            private void SetOriginal(string field, string method)
            {
                object hook = typeof(PackagedLordPresentation).GetField(field, PrivateStatic).GetValue(null);
                var property = hook.GetType().GetProperty("Original", BindingFlags.Instance | BindingFlags.NonPublic);
                originals.Add(Tuple.Create(property, hook, property.GetValue(hook)));
                property.SetValue(hook, Delegate.CreateDelegate(property.PropertyType, FixtureMethod(method)));
            }
            private static string RuntimeName(PackagedLordPresentationEntry entry, int nickname) => current.runtime(entry, nickname);
            private static string VanillaTitle(int lord, int nickname, string name) => current.vanilla(lord, nickname, name);
            private static GameData GameInstance() => current.gameData;
            private static Translate TranslationInstance() => null;
            private static CustomisationFileManager CustomInstance() => null;
            private static string TranslationIndex(Translate self, Enums.eTextSections section, int index)
            { Assert.AreEqual(Enums.eTextSections.TEXT_CUSTOMISATION, section); return "Vanilla " + (index - 23); }
            private static string TranslationValue(Translate self, Enums.eTextSections section, Enums.eTextValues index) => TranslationIndex(self, section, (int)index);
            private static string CustomText(CustomisationFileManager self, string name, int id) => (string)Production("Custom").Invoke(null, new object[] { self, name, id });
            private static string OriginalCustom(CustomisationFileManager self, string name, int id)
            { current.LegacyLookups++; Assert.AreEqual("peer\\donor", name); Assert.IsTrue(id >= 34 && id <= 41); return current.LegacyText; }
            private static string OriginalName(int lord, int nickname) => "original-name";
            private static string OriginalPlayer(Platform_Multiplayer self, int id, bool active, bool exclude) => "original-player";
            private static bool OriginalTitle(GameAIManagerAPI self, string name, out string title, int nickname)
            {
                Assert.AreEqual("unclaimed", name, "A claimed identity consulted peer-local SE metadata.");
                title = "original-title";
                return true;
            }
            private static bool ForbiddenSupported(GameAIManagerAPI self, string name) => throw new AssertFailedException("A title-policy difference released a supported claim.");
            private static bool ForbiddenOptionalText(GameAIManagerAPI self, string name, out string text)
            { text = null; throw new AssertFailedException("A title-policy difference released a non-title text claim."); }
            private static bool ForbiddenDetails(GameAIManagerAPI self, string name, out SHCDESE.API.Components.AI.LordDetails details)
            { details = null; throw new AssertFailedException("A title-policy difference released a details claim."); }
            private static bool ForbiddenFace(GameAIManagerAPI self, string name, out Noesis.ImageSource face)
            { face = null; throw new AssertFailedException("A title-policy difference released a portrait claim."); }
            public void Dispose()
            {
                foreach (var item in originals) item.Item1.SetValue(item.Item2, item.Item3);
                entries.SetValue(null, previousEntries); CustomisationFileManager.CustomMediaExists = previousMedia; current = null;
            }
        }
    }
}
