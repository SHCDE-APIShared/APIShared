using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace APISharedTests
{
    // Each test binds a fresh site to its own static stand-in with the game method's parameters, through the
    // production site code. Hooks on these stand-ins are permanent, as in the game.
    [TestClass, DoNotParallelize]
    public sealed class PaintEventsTests
    {
        private static readonly List<string> Trace = new List<string>();
        private static Sprite nativeChevron;
        internal static T Fake<T>() where T : class => (T)FormatterServices.GetUninitializedObject(typeof(T));
        private static MethodInfo Target(string name) =>
            typeof(PaintEventsTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        // One slot per test: each closed site type has its own statics and hook.
        internal static PaintRegistry<BodySpritePaint> Body<TSlot>(string name) where TSlot : struct
        {
            BodySpriteSite<TSlot>.Bind(PaintBinding.StandIn(() => Target(name), typeof(void), PaintCalls.BodySignature),
                (r, f, i, c, a, ch, t) => Target(name).Invoke(null, new object[] { r, f, i, c, a, ch, t }));
            return BodySpriteSite<TSlot>.Registry;
        }
        private struct OrderSlot { }
        private struct DormantSlot { }
        private struct ClaimSlot { }
        private struct FailureSlot { }
        private struct BeneathSlot { }
        private struct ChainedSlot { }
        private struct ChevronSlot { }
        private struct TileSlot { }
        private struct ColourSlot { }
        private struct InLayerSlot { }
        private struct PixieSlot { }
        private struct FlySlot { }
        private struct OrgSlot { }
        private struct BuildingSlot { }
        private struct WrongShapeSlot { }
        private struct UnboundSlot { }
        private struct RefusedSlot { }
        private struct VetoedSlot { }

        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyOrder(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("original:" + file);
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyClaims(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("original");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyFailure(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("original");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyBeneath(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("original");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyInLayer(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("body");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyRefused(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("original");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyVetoed(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("original");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyDormant(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("original");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyChained(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Trace.Add("original");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyWrongShape(SpriteRenderer r, int file, int image, int colour, bool alt, int chop) => Trace.Add("original");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void TileGraphic(GameMapTile tile, int file, int image, int light) => Trace.Add("original:" + file);
        [MethodImpl(MethodImplOptions.NoInlining)] private static Sprite Chevron(GameMapTile tile, int file, int image) { Trace.Add("original"); return nativeChevron; }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void TileColour(gameTile self, GameMapTile tile, Vector3Int location, int light) => Trace.Add("original:" + location.x);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Pixie(GameMap map, int objectId, int x, int y, int tileX, int tileY, float height, int file, int image, bool hiMode, int transparency, int delay, int colour)
        {
            Trace.Add("pixie");
            if (objectId < 0) throw new InvalidOperationException("original failed");
            BodyInLayer(null, file, image, colour, false, 0, transparency);
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Fly(GameMap map, int objectId, int x, int y, int tileX, int tileY, float height, int file, int image, int colour, int transparency, int delay, int gameObjectType, int infoType, int infoData) => Trace.Add("fly");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Org(GameMap map, int objectId, int x, int y, float height, int file, int image, int colour, int transparency) => Trace.Add("org");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BuildingAnim(GameMap map, int objectId, int x, int y, int tileX, int tileY, int animLayer, int file, int image, int colour, int transparency, int delay, bool hasSubSpecial, int halfPixelX, int halfPixelY) => Trace.Add("building");

        [TestInitialize] public void Clear() => Trace.Clear();

        private static PaintHandler<BodySpritePaint> Record(string label) => (ref BodySpritePaint paint) =>
            Trace.Add(label + ":" + paint.File);

        [TestMethod]
        public void BodySpriteRunsAfterOriginalInOrderAndHonoursFileFilters()
        {
            PaintRegistry<BodySpritePaint> site = Body<OrderSlot>(nameof(BodyOrder));
            Assert.AreEqual("Dormant", site.Status);
            Assert.IsTrue(site.TryRegister("owner.b", "all", Record("b/all"), 0, null, false, out string reason), reason);
            Assert.IsTrue(site.TryRegister("owner.a", "forty", Record("a/forty"), 0, new[] { 40, 40 }, false, out reason), reason);
            Assert.IsTrue(site.TryRegister("owner.c", "early", Record("c/early"), -5, null, false, out reason), reason);
            Assert.IsTrue(site.TryRegister("owner.a", "late41", Record("a/late41"), 7, new[] { 41 }, false, out reason), reason);
            Assert.IsFalse(site.TryRegister("owner.a", "late41", Record("again"), 0, null, false, out reason), "Owner/ID pairs are unique.");
            Assert.AreEqual("Installed", site.Status);

            foreach (int file in new[] { 40, 17, 41, 5000, -1 }) BodyOrder(null, file, 1, 0, false, 0, 0);
            CollectionAssert.AreEqual(new[] {
                "original:40", "c/early:40", "a/forty:40", "b/all:40",
                "original:17", "c/early:17", "b/all:17",
                "original:41", "c/early:41", "b/all:41", "a/late41:41",
                "original:5000", "c/early:5000", "b/all:5000",
                "original:-1", "c/early:-1", "b/all:-1" }, Trace);
        }

        [TestMethod]
        public void RegistrationRejectsInvalidInputWithoutInstalling()
        {
            PaintRegistry<BodySpritePaint> site = Body<DormantSlot>(nameof(BodyDormant));
            Assert.IsFalse(site.TryRegister("", "id", Record("x"), 0, null, false, out string reason));
            Assert.IsFalse(site.TryRegister("owner", "id", null, 0, null, false, out reason));
            Assert.IsFalse(site.TryRegister("owner", "id", Record("x"), 0, new int[0], false, out reason));
            Assert.IsFalse(site.TryRegister("owner", "id", Record("x"), 0, new[] { 1024 }, false, out reason));
            StringAssert.Contains(reason, "outside");
            Assert.IsFalse(PaintEvents.TryRegisterLayer(PaintLayer.None, "owner", "id", null, out reason));
            Assert.AreEqual("Dormant", site.Status);
            Assert.IsFalse(site.IsInstalled);
            Assert.AreEqual(0, ManagedEntry.CountMonoModDetours(Target(nameof(BodyDormant))), "A dormant site leaves the method unhooked.");
            BodyDormant(null, 1, 1, 0, false, 0, 0);
            CollectionAssert.AreEqual(new[] { "original" }, Trace);
            foreach (PaintSite production in Enum.GetValues(typeof(PaintSite)))
            {
                Assert.AreEqual("Dormant", PaintEvents.GetStatus(production), production + " must stay unhooked without registrations.");
                Assert.IsFalse(PaintEvents.IsInstalled(production));
            }
            Assert.AreEqual("Unknown", PaintEvents.GetStatus((PaintSite)99));
        }

        [TestMethod]
        public void FirstClaimWinsAndClaimOverNeedsTheExactOwner()
        {
            PaintRegistry<BodySpritePaint> site = Body<ClaimSlot>(nameof(BodyClaims));
            Assert.IsTrue(site.TryRegister("identity", "unit", (ref BodySpritePaint p) => Trace.Add("identity:" + p.TryClaim() + ":" + p.ClaimedBy), -10, null, false, out _));
            Assert.IsTrue(site.TryRegister("theme", "file", (ref BodySpritePaint p) => Trace.Add("theme:" + p.TryClaim() + ":" + p.ClaimedBy), 10, null, false, out _));
            Assert.IsTrue(site.TryRegister("addon", "layer", (ref BodySpritePaint p) =>
                Trace.Add("addon:" + p.ClaimOver("someone") + ":" + p.ClaimOver("identity") + ":" + p.ClaimedBy), 20, null, false, out _));
            Assert.IsTrue(site.TryRegister("tint", "colour", (ref BodySpritePaint p) => Trace.Add("tint:" + p.IsClaimed), 30, null, false, out _));
            BodyClaims(null, 9, 1, 0, false, 0, 0);
            BodyClaims(null, 9, 1, 0, false, 0, 0);
            var once = new[] { "original", "identity:True:identity", "theme:False:identity", "addon:False:True:addon", "tint:True" };
            CollectionAssert.AreEqual(once.Concat(once).ToArray(), Trace, "Each paint starts unclaimed.");
        }

        [TestMethod]
        public void FailingCallbackRollsBackItsClaimAndLaterRegistrationsRun()
        {
            PaintRegistry<BodySpritePaint> site = Body<FailureSlot>(nameof(BodyFailure));
            Assert.IsTrue(site.TryRegister("broken", "a", (ref BodySpritePaint p) => { p.TryClaim(); throw new InvalidOperationException("boom"); }, 0, null, false, out _));
            Assert.IsTrue(site.TryRegister("next", "b", (ref BodySpritePaint p) => Trace.Add("next:" + p.TryClaim() + ":" + p.ClaimedBy), 1, null, false, out _));
            BodyFailure(null, 3, 1, 0, false, 0, 0);
            BodyFailure(null, 3, 1, 0, false, 0, 0);
            CollectionAssert.AreEqual(new[] { "original", "next:True:next", "original", "next:True:next" }, Trace);
            Assert.AreEqual(2L, site.CallbackFailures);
        }

        [TestMethod]
        public void CallOriginalRunsBeneathTheBrokerWithoutNotifications()
        {
            PaintRegistry<BodySpritePaint> site = Body<BeneathSlot>(nameof(BodyBeneath));
            BodySpriteSite<BeneathSlot>.CallOriginal(null, 1, 1, 0, false, 0, 0);
            Assert.IsTrue(site.TryRegister("owner", "id", Record("callback"), 0, null, false, out _));
            BodySpriteSite<BeneathSlot>.CallOriginal(null, 2, 1, 0, false, 0, 0);
            BodyBeneath(null, 3, 1, 0, false, 0, 0);
            CollectionAssert.AreEqual(new[] { "original", "original", "original", "callback:3" }, Trace);
        }

        [TestMethod]
        public void BrokerComposesWithAConsumerHookInstalledBeforeIt()
        {
            MethodInfo target = Target(nameof(BodyChained));
            BodyCall inner = null;
            var consumer = new Hook(target, (BodyCall)((r, f, i, c, a, ch, t) => { inner(r, f, i, c, a, ch, t); Trace.Add("consumer"); }));
            inner = consumer.GenerateTrampoline<BodyCall>();
            Assert.AreEqual(1, ManagedEntry.CountMonoModDetours(target));
            PaintRegistry<BodySpritePaint> site = Body<ChainedSlot>(nameof(BodyChained));
            Assert.IsTrue(site.TryRegister("owner", "id", Record("broker"), 0, null, false, out string reason), reason);
            Assert.AreEqual(2, ManagedEntry.CountMonoModDetours(target));
            BodyChained(null, 4, 1, 0, false, 0, 0);
            CollectionAssert.AreEqual(new[] { "original", "consumer", "broker:4" }, Trace, "The earlier hook runs beneath the broker.");
            GC.KeepAlive(consumer);
        }

        private delegate void BodyCall(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency);

        [TestMethod]
        public void ChevronReturnsTheComposedResultAndRollsBackAFailedReplacement()
        {
            nativeChevron = Fake<Sprite>();
            Sprite b = Fake<Sprite>(), c = Fake<Sprite>(), d = Fake<Sprite>();
            ChevronSite<ChevronSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(Chevron)), typeof(Sprite), PaintCalls.ChevronSignature));
            PaintRegistry<ChevronPaint> site = ChevronSite<ChevronSlot>.Registry;
            Assert.IsTrue(site.TryRegister("first", "x", (ref ChevronPaint p) => Trace.Add("first:" + p.TryReplace(b)), 0, null, false, out _));
            Assert.IsTrue(site.TryRegister("second", "x", (ref ChevronPaint p) => Trace.Add("second:" + p.TryReplace(c)), 1, null, false, out _));
            Assert.IsTrue(site.TryRegister("third", "x", (ref ChevronPaint p) => Trace.Add("third:" + p.ReplaceOver("first", c)), 2, null, false, out _));
            Assert.IsTrue(site.TryRegister("fourth", "x", (ref ChevronPaint p) => { p.ReplaceOver("third", d); throw new InvalidOperationException("boom"); }, 3, null, false, out _));
            Assert.IsTrue(site.TryRegister("observer", "x", (ref ChevronPaint p) =>
                Trace.Add("observer:" + ReferenceEquals(p.NativeResult, nativeChevron) + ":" + ReferenceEquals(p.Result, c) + ":" + p.ClaimedBy), 4, null, false, out _));
            Sprite returned = Chevron(null, 5, 1);
            Assert.AreSame(c, returned);
            CollectionAssert.AreEqual(new[] { "original", "first:True", "second:False", "third:True", "observer:True:True:third" }, Trace);
            Assert.AreEqual(1L, site.CallbackFailures);
        }

        [TestMethod]
        public void TileGraphicFiltersFilesAndTileColourReachesEveryRegistration()
        {
            TileGraphicSite<TileSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(TileGraphic)), typeof(void), PaintCalls.TileGraphicSignature));
            PaintRegistry<TileGraphicPaint> tile = TileGraphicSite<TileSlot>.Registry;
            Assert.IsTrue(tile.TryRegister("garrison", "ruins", (ref TileGraphicPaint p) => Trace.Add("ruins:" + p.File + ":" + p.Image + ":" + p.Light + ":" + p.TryClaim()), -1, new[] { 149 }, false, out _));
            Assert.IsTrue(tile.TryRegister("theme", "all", (ref TileGraphicPaint p) => Trace.Add("theme:" + p.File + ":" + p.TryClaim()), 1, null, false, out _));
            TileGraphic(null, 149, 25, 3);
            TileGraphic(null, 10, 1, 0);
            CollectionAssert.AreEqual(new[] { "original:149", "ruins:149:25:3:True", "theme:149:False", "original:10", "theme:10:True" }, Trace);

            Trace.Clear();
            TileColourSite<ColourSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(TileColour)), typeof(void), PaintCalls.TileColourSignature));
            Assert.IsTrue(TileColourSite<ColourSlot>.Registry.TryRegister("fog", "tint", (ref TileColourPaint p) => Trace.Add("fog:" + p.Location.x + ":" + p.Location.y + ":" + p.Light), 0, null, false, out _));
            TileColour(null, null, new Vector3Int(7, 8, 0), 2);
            CollectionAssert.AreEqual(new[] { "original:7", "fog:7:8:2" }, Trace);
        }

        [TestMethod]
        public void LayerScopeReachesNestedBodyPaintsAndUnwindsOnException()
        {
            PaintRegistry<BodySpritePaint> body = Body<InLayerSlot>(nameof(BodyInLayer));
            Assert.IsTrue(body.TryRegister("medium", "rack", (ref BodySpritePaint p) =>
                Trace.Add("body:" + p.Layer.Layer + ":" + p.Layer.ObjectId + ":" + p.Layer.File + ":" + p.Layer.Image + ":" + PaintEvents.CurrentLayer.IsActive), 0, null, false, out _));
            PixieSite<PixieSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(Pixie)), typeof(void), PaintCalls.PixieSignature));
            PaintRegistry<LayerPaint> pixie = PixieSite<PixieSlot>.Registry;
            BodyInLayer(null, 1, 1, 0, false, 0, 0);
            Assert.IsTrue(pixie.TryRegister("trees", "scope", null, 0, null, true, out string reason), reason);
            Assert.IsTrue(pixie.TryRegister("trees", "after", (ref LayerPaint p) =>
                Trace.Add("after:" + p.Layer + ":" + p.ObjectId + ":" + p.HiMode + ":" + p.Delay + ":" + PaintEvents.CurrentLayer.IsActive), 0, new[] { 31 }, true, out reason), reason);
            Pixie(null, 12, 0, 0, 0, 0, 0f, 31, 4, true, 0, 9, 0);
            Pixie(null, 13, 0, 0, 0, 0, 0f, 30, 5, false, 0, 0, 0);
            Assert.ThrowsExactly<InvalidOperationException>(() => Pixie(null, -1, 0, 0, 0, 0, 0f, 31, 4, false, 0, 0, 0));
            Assert.IsFalse(PaintEvents.CurrentLayer.IsActive, "An exception from the original unwinds the scope.");
            CollectionAssert.AreEqual(new[] {
                "body", "body:None:0:0:0:False",
                "pixie", "body", "body:Pixie:12:31:4:True", "after:Pixie:12:True:9:False",
                "pixie", "body", "body:Pixie:13:30:5:True",
                "pixie" }, Trace);
        }

        [TestMethod]
        public void FlyOrgAndBuildingAnimMapEveryArgument()
        {
            FlySite<FlySlot>.Bind(PaintBinding.StandIn(() => Target(nameof(Fly)), typeof(void), PaintCalls.FlySignature));
            OrgSite<OrgSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(Org)), typeof(void), PaintCalls.OrgSignature));
            BuildingAnimSite<BuildingSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(BuildingAnim)), typeof(void), PaintCalls.BuildingAnimSignature));
            PaintHandler<LayerPaint> record = (ref LayerPaint p) => Trace.Add(string.Join(",", p.Layer, p.ObjectId, p.X, p.Y, p.TileX, p.TileY,
                p.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
                p.File, p.Image, p.Colour, p.Transparency, p.Delay, p.HiMode, p.GameObjectType, p.InfoType, p.InfoData, p.AnimLayer, p.HasSubSpecial, p.HalfPixelX, p.HalfPixelY));
            Assert.IsTrue(FlySite<FlySlot>.Registry.TryRegister("o", "fly", record, 0, null, true, out _));
            Assert.IsTrue(OrgSite<OrgSlot>.Registry.TryRegister("o", "org", record, 0, null, true, out _));
            Assert.IsTrue(BuildingAnimSite<BuildingSlot>.Registry.TryRegister("o", "building", record, 0, null, true, out _));
            Fly(null, 1, 2, 3, 4, 5, 6.5f, 7, 8, 9, 10, 11, 12, 13, 14);
            Org(null, 1, 2, 3, 4.5f, 5, 6, 7, 8);
            BuildingAnim(null, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, true, 13, 14);
            CollectionAssert.AreEqual(new[] {
                "fly", "Fly,1,2,3,4,5,6.5,7,8,9,10,11,False,12,13,14,0,False,0,0",
                "org", "Org,1,2,3,0,0,4.5,5,6,7,8,0,False,0,0,0,0,False,0,0",
                "building", "BuildingAnim,1,2,3,4,5,0,7,8,9,10,11,False,0,0,0,6,True,13,14" }, Trace);
        }

        [TestMethod]
        public void ChangedContractIsRefusedAndLeavesTheMethodUnhooked()
        {
            BodySpriteSite<WrongShapeSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(BodyWrongShape)), typeof(void), PaintCalls.BodySignature), null);
            PaintRegistry<BodySpritePaint> site = BodySpriteSite<WrongShapeSlot>.Registry;
            Assert.IsFalse(site.TryRegister("owner", "id", Record("x"), 0, null, false, out string reason));
            StringAssert.Contains(reason, "parameter count changed");
            StringAssert.StartsWith(site.Status, "Unavailable: ");
            Assert.IsFalse(site.IsInstalled);
            Assert.AreEqual(0, ManagedEntry.CountMonoModDetours(Target(nameof(BodyWrongShape))));
            BodySpriteSite<WrongShapeSlot>.Bind(PaintBinding.StandIn(() => null, typeof(void), PaintCalls.BodySignature), null);
            Assert.IsFalse(site.TryRegister("owner", "id", Record("x"), 0, null, false, out reason));
            StringAssert.Contains(reason, "not found");
            Assert.IsFalse(BodySpriteSite<UnboundSlot>.Registry.TryRegister("owner", "id", Record("x"), 0, null, false, out reason));
            StringAssert.Contains(reason, "not bound");
            Assert.IsNotNull(PaintSiteGuard.Check(Target(nameof(BodyOrder)), typeof(SpriteMapping), nameof(SpriteMapping.SetBodySprite), true, typeof(void), PaintCalls.BodySignature, out _),
                "A production binding refuses a method on another type.");
        }

        [TestMethod]
        public void ForeignEntryIsRefusedAndALaterRegistrationRetries()
        {
            Func<MethodBase, ManagedEntry> production = PaintSiteGuard.ReadEntry;
            MethodInfo target = Target(nameof(BodyRefused));
            PaintRegistry<BodySpritePaint> site = Body<RefusedSlot>(nameof(BodyRefused));
            try
            {
                PaintSiteGuard.ReadEntry = method => new ManagedEntry(0, true, new byte[] { 0xE9, 1, 2, 3, 4, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
                Assert.IsFalse(site.TryRegister("owner", "id", Record("x"), 0, null, false, out string reason));
                StringAssert.Contains(reason, "foreign detour");
                Assert.AreEqual(0, ManagedEntry.CountMonoModDetours(target), "Nothing is written over a foreign entry.");
                BodyRefused(null, 1, 1, 0, false, 0, 0);
                CollectionAssert.AreEqual(new[] { "original" }, Trace);
            }
            finally { PaintSiteGuard.ReadEntry = production; }
            Assert.IsTrue(site.TryRegister("owner", "id", Record("x"), 0, null, false, out string retry), retry);
            Assert.AreEqual("Installed", site.Status);
        }

        [TestMethod]
        public void OwnershipVetoSeamRefusesInstallation()
        {
            PaintRegistry<BodySpritePaint> site = Body<VetoedSlot>(nameof(BodyVetoed));
            try
            {
                PaintSiteGuard.OwnershipVeto = method => method.Name == nameof(BodyVetoed) ? "exclusively owned by test.registry" : null;
                Assert.IsFalse(site.TryRegister("owner", "id", Record("x"), 0, null, false, out string reason));
                StringAssert.Contains(reason, "exclusively owned by test.registry");
            }
            finally { PaintSiteGuard.OwnershipVeto = null; }
        }

        [TestMethod]
        public void EntryDecisionTable()
        {
            byte[] Pad(params byte[] bytes) { var head = new byte[16]; for (int i = 0; i < head.Length; i++) head[i] = 0x90; Array.Copy(bytes, head, bytes.Length); return head; }
            byte[] relJump = Pad(0xE9, 0x10, 0, 0, 0);
            byte[] shortJump = Pad(0xEB, 0x10);
            byte[] memJump = Pad(0xFF, 0x25, 0, 0, 0, 0);
            byte[] raxJump = Pad(0x48, 0xB8, 1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0xE0);
            byte[] r11Jump = Pad(0x49, 0xBB, 1, 2, 3, 4, 5, 6, 7, 8, 0x41, 0xFF, 0xE3);
            byte[] pushRet = Pad(0x68, 1, 2, 3, 4, 0xC3);
            byte[] prologue = Pad(0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0xC1);
            byte[] pushRbp = Pad(0x55, 0x48, 0x8B, 0xEC);
            foreach (byte[] jump in new[] { relJump, shortJump, memJump, raxJump, r11Jump, pushRet })
            {
                StringAssert.Contains(new ManagedEntry(0, true, jump).Refusal(), "foreign detour");
                StringAssert.Contains(new ManagedEntry(-1, true, jump).Refusal(), "unreadable");
                Assert.IsNull(new ManagedEntry(1, true, jump).Refusal(), "A MonoMod chain owns its entry.");
                Assert.IsNull(new ManagedEntry(0, false, jump).Refusal(), "CLR precode entries are not inspected.");
            }
            Assert.IsNull(new ManagedEntry(0, true, prologue).Refusal());
            Assert.IsNull(new ManagedEntry(0, true, pushRbp).Refusal());
            Assert.IsNull(new ManagedEntry(-1, true, prologue).Refusal());
            StringAssert.Contains(new ManagedEntry(0, true, null, "denied").Refusal(), "cannot read");
            Assert.IsFalse(ManagedEntry.StartsWithJump(Pad(0x48, 0xB8, 1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0xE1)), "Jump through another register is not this form.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static int InventoryHook(int value) => value + 1;
        [MethodImpl(MethodImplOptions.NoInlining)] private static int InventoryIl(int value) => value + 2;
        [MethodImpl(MethodImplOptions.NoInlining)] private static int InventoryNone(int value) => value + 3;

        [TestMethod]
        public void MonoModInventoryCountsHooksAndIlHooks()
        {
            MethodInfo hooked = Target(nameof(InventoryHook)), il = Target(nameof(InventoryIl)), none = Target(nameof(InventoryNone));
            Assert.AreEqual(0, ManagedEntry.CountMonoModDetours(hooked));
            var hook = new Hook(hooked, (Func<Func<int, int>, int, int>)((orig, value) => orig(value) * 10));
            var ilHook = new ILHook(il, context => { });
            Assert.AreEqual(1, ManagedEntry.CountMonoModDetours(hooked));
            Assert.IsTrue(ManagedEntry.CountMonoModDetours(il) >= 1, "An ILHook (HarmonyX's patch form) applies a MonoMod detour.");
            Assert.AreEqual(0, ManagedEntry.CountMonoModDetours(none));
            Assert.AreEqual(20, InventoryHook(1));
            ManagedEntry entry = ManagedEntry.Read(hooked);
            Assert.IsNotNull(entry.Head);
            Assert.AreEqual(1, entry.MonoModDetours);
            Assert.IsNull(entry.Refusal());
            GC.KeepAlive(hook); GC.KeepAlive(ilHook);
        }
    }
}
