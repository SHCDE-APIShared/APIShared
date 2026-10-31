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
                    object[] call = { null, "Lord", AILordMessageType.Taunt1, null, null };
                    bool available = (bool)type.GetMethod("Message", flags).Invoke(null, call);
                    Assert.AreEqual(provider != null, available);
                    object[] subtitle = { null, "Lord", null };
                    Assert.IsTrue((bool)type.GetMethod("Speech", flags).Invoke(null, subtitle));
                    if (selectedText == null) selectedText = (string)subtitle[2]; else Assert.AreEqual(selectedText, subtitle[2]);
                    var reference = new Random(123); reference.Next(0, 2);
                    Assert.AreEqual(reference.Next(), random.Next(), "Local file availability changed synchronized RNG consumption.");
                    if (available) StringAssert.StartsWith((string)call[4], "_SLA_/private.owner/");
                }
            }
            finally
            {
                entries.SetValue(null, previousEntries); rng.SetValue(null, previousRng); spoken.Clear();
                foreach (var pair in previousSpeech) spoken.Add(pair.Key, pair.Value);
            }
        }
    }
}
