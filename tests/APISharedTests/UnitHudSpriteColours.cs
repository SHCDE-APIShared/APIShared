using APIShared;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        // Vanilla's Platform_Multiplayer.UIColourRemap (material palette -> UI/portrait order) and
        // SpriteMapping.shcRemapColours (UI order -> palette); copied because both are Vanilla data contracts.
        private static readonly int[] VanillaUiColourRemap = { 0, 1, 4, 2, 3, 6, 5, 7, 8 };
        private static readonly int[] VanillaShcRemapColours = { 0, 1, 3, 4, 2, 6, 5, 7, 8 };

        [TestMethod]
        [TestCategory("Presentation")]
        public void PlayerUiColoursConvertVanillaPaletteToPortraitOrder()
        {
            string[] palette = { null, "red", "blue", "orange", "yellow", "purple", "gray", "lightblue", "green" };
            string[] ui = { null, "red", "orange", "yellow", "blue", "gray", "purple", "lightblue", "green" };
            int[] identity = Enumerable.Range(0, 9).ToArray();
            for (int slot = 1; slot <= 8; slot++)
            {
                Assert(PlayerColors.TryMapUiColour(slot, identity, VanillaUiColourRemap, out int colour) &&
                    ui[colour] == palette[slot], "palette " + palette[slot] + " keeps its colour in UI order");
                // A remapped player slot follows the remap table, and Vanilla's default SHC table round-trips.
                Assert(PlayerColors.TryMapUiColour(slot, VanillaShcRemapColours, VanillaUiColourRemap, out colour) && colour == slot,
                    "SHC default mapping gives player " + slot + " UI colour " + slot);
            }
            int[] swapped = { 0, 4, 2, 3, 1, 5, 6, 7, 8 };
            Assert(PlayerColors.TryMapUiColour(1, swapped, VanillaUiColourRemap, out int yellow) && ui[yellow] == "yellow",
                "player 1 with the yellow palette resolves to UI yellow");

            Assert(!PlayerColors.TryMapUiColour(1, null, VanillaUiColourRemap, out int none) && none == 0, "missing remap");
            Assert(!PlayerColors.TryMapUiColour(1, identity, null, out none) && none == 0, "missing UI remap");
            Assert(!PlayerColors.TryMapUiColour(9, identity, VanillaUiColourRemap, out none) && none == 0, "slot outside remap");
            Assert(!PlayerColors.TryMapUiColour(-1, identity, VanillaUiColourRemap, out none) && none == 0, "negative slot");
            Assert(!PlayerColors.TryMapUiColour(3, new int[9], VanillaUiColourRemap, out none) && none == 0, "unassigned slot");
            Assert(!PlayerColors.TryMapUiColour(3, identity, new int[9], out none) && none == 0, "invalid UI colour");
            Assert(!PlayerColors.TryMapUiColour(3, identity, new[] { 0, 1 }, out none) && none == 0, "short UI remap");

            // The test host is not the Unity main thread; the public lookup rejects it without reading game state.
            Assert(!PlayerColors.TryGetUiColour(1, out none) && none == 0, "public lookup is main-thread only");
            Assert(!PlayerColors.TryGetUiColour(0, out none) && !PlayerColors.TryGetUiColour(9, out none), "player IDs are 1..8");
            Assert(new[] { 1, 2, 6, 7 }.All(PlayerColors.IsArabicLordType) &&
                !new[] { 0, 3, 4, 5, 8 }.Any(PlayerColors.IsArabicLordType), "Arabic lord types");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void UnitHudSpriteColourLeaseAppliesOnceAndRestoresVanilla()
        {
            Type type = typeof(UnitHudPresentationService);
            var service = (UnitHudPresentationService)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single().Invoke(new object[] { "test", null, null, false });
            var original = new SpriteOriginalRecorder();
            FieldInfo originalField = type.GetField("updateSpritesOriginal", BindingFlags.Instance | BindingFlags.NonPublic);
            originalField.SetValue(service, Delegate.CreateDelegate(originalField.FieldType, original,
                typeof(SpriteOriginalRecorder).GetMethod(nameof(SpriteOriginalRecorder.Call))));
            var main = (MainViewModel)FormatterServices.GetUninitializedObject(typeof(MainViewModel));
            MainViewModel target = main;
            service.spriteReplayTarget = () => target;
            // Player 7 is unresolvable; player 6 has a known Arabic lord; others keep Vanilla's Arabic value.
            service.spriteColourResolver = (int player, bool vanillaArabic, out int colour, out bool arabic) =>
            {
                colour = player == 7 ? 0 : 9 - player;
                arabic = player == 6 || vanillaArabic;
                return player != 7;
            };
            MethodInfo hook = type.GetMethod("UpdateSpritesHook", BindingFlags.Instance | BindingFlags.NonPublic);
            void Vanilla(int colour, bool arabic) => hook.Invoke(service, new object[] { main, colour, arabic });
            void Invoke(string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, null);
            Func<string, object> field = name => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);

            Assert(service.TryLeaseSpriteColours("early", 2, out _) && original.Calls.Count == 0,
                "without a Vanilla request a lease waits for Vanilla's sprite update");
            Vanilla(3, false);
            Assert(original.Take() == "7:False" && (int)field("lastSpriteColour") == 3,
                "the first Vanilla update draws the leased player and records Vanilla's request");
            Assert(service.ReleaseSpriteColours("early") && original.Take() == "3:False", "release replays Vanilla once");
            Vanilla(4, true);
            Assert(original.Take() == "4:True", "no lease passes Vanilla's values unchanged");

            Assert(service.TryLeaseSpriteColours("a", 5, out string reason) && reason == null && original.Take() == "4:True",
                "lease applies once; player 5 keeps Vanilla's Arabic value");
            Assert(service.TryLeaseSpriteColours("a", 5, out _) && original.Calls.Count == 0, "unchanged lease does no work");
            Assert(!service.TryLeaseSpriteColours("b", 2, out reason) && reason.Contains("'a'") && original.Calls.Count == 0,
                "another owner is rejected with the holder");
            Assert(!service.ReleaseSpriteColours("b") && original.Calls.Count == 0, "another owner cannot release");
            Assert(service.TryLeaseSpriteColours("a", 6, out _) && original.Take() == "3:True", "player change applies once");
            Vanilla(1, false);
            Assert(original.Take() == "3:True" && (int)field("lastSpriteColour") == 1 && !(bool)field("lastSpriteArabic"),
                "Vanilla updates draw the lease and keep the new restore values");
            Assert(service.TryLeaseSpriteColours("a", 7, out _) && original.Take() == "1:False",
                "an unresolvable player keeps Vanilla's values");
            Assert(service.ReleaseSpriteColours("a") && original.Take() == "1:False" && !service.ReleaseSpriteColours("a"),
                "release restores Vanilla once and ends the lease");

            Assert(service.TryLeaseSpriteColours("b", 2, out _) && original.Take() == "7:False", "a new owner can lease after release");
            Invoke("OnSpriteMissionStarted");
            Assert(original.Take() == "7:False", "mission start re-applies an active lease once");
            Invoke("EndSpriteColourLease");
            Assert(original.Take() == "1:False" && !service.ReleaseSpriteColours("b"), "mission end restores Vanilla and ends the lease");
            Invoke("OnSpriteMissionStarted");
            Invoke("EndSpriteColourLease");
            Assert(original.Calls.Count == 0, "mission events without a lease do no work");

            target = null;
            Assert(service.TryLeaseSpriteColours("c", 4, out _) && original.Calls.Count == 0, "an unready HUD defers to Vanilla's next update");
            Vanilla(2, false);
            Assert(original.Take() == "5:False", "Vanilla's next update applies the deferred lease");
            target = main;
            service.spriteColourResolver = (int player, bool vanillaArabic, out int colour, out bool arabic) =>
                throw new InvalidOperationException("resolver fixture");
            Vanilla(8, true);
            Assert(original.Take() == "8:True", "a failing resolver keeps Vanilla's values");
            Assert(service.ReleaseSpriteColours("c") && original.Take() == "8:True", "release after a resolver failure restores Vanilla");
        }

        private sealed class SpriteOriginalRecorder
        {
            internal readonly List<string> Calls = new List<string>();
            public void Call(MainViewModel self, int colour, bool arabic) => Calls.Add(colour + ":" + arabic);
            internal string Take()
            {
                string result = Calls.Count == 1 ? Calls[0] : "calls=" + string.Join("|", Calls);
                Calls.Clear();
                return result;
            }
        }
    }
}
