using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace APISharedTests
{
    // A/B of the broker against today's consumer hooks on the .NET Framework test host. Each variant hooks
    // its own static stand-in with the game method's parameters; the stand-in body is identical everywhere.
    // "Consumer" variants reimplement the per-paint path the consumers run today for a paint that is not
    // theirs (UnitSprites.cs: trampoline, renderer-binding lookup, covered-file check; RuinSpriteSwap.cs and
    // TileSwap.cs for tiles; FogRenderer.cs for tile colour). "Broker" variants run the same consumer work as
    // registrations on a site bound through the production site code. Variants alternate within each round.
    // Mono (the game) is not measured here; see docs/PAINT.md for the limits.
    [TestClass, DoNotParallelize]
    public sealed class PaintPerformanceTests
    {
        public TestContext TestContext { get; set; }
        private const int Calls = 200000, Rounds = 11;
        private static int sink, hits;
        private static volatile bool fogEnabled = true;
        private static readonly bool[] Covered = new bool[1024];
        private static readonly bool[] Visible = new bool[800 * 800];
        private static readonly Dictionary<SpriteRenderer, Binding> Bindings = new Dictionary<SpriteRenderer, Binding>(ReferenceComparer.Instance);
        private static readonly SpriteRenderer Renderer = PaintEventsTests.Fake<SpriteRenderer>();
        private static readonly GameMapTile Tile = PaintEventsTests.Fake<GameMapTile>();
        private sealed class Binding { internal int File; }
        private sealed class ReferenceComparer : IEqualityComparer<SpriteRenderer>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(SpriteRenderer a, SpriteRenderer b) => ReferenceEquals(a, b);
            public int GetHashCode(SpriteRenderer value) => RuntimeHelpers.GetHashCode(value);
        }

        private delegate void BodyCall(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency);
        private delegate void TileCall(GameMapTile tile, int file, int image, int light);
        private delegate void ColourCall(gameTile self, GameMapTile tile, Vector3Int location, int light);

        // Stand-ins for the game methods: same parameters, same trivial body.
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyPlain(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyDormant(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyConsumer1(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyConsumerInstance1(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyBroker1(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyBrokerFiltered1(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyConsumer8(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BodyBroker8(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void TileConsumer2(GameMapTile tile, int file, int image, int light) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void TileBroker2(GameMapTile tile, int file, int image, int light) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void ColourConsumer1(gameTile self, GameMapTile tile, Vector3Int location, int light) => sink += light;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void ColourBroker1(gameTile self, GameMapTile tile, Vector3Int location, int light) => sink += light;

        // Consumer work shared by both sides, for a paint that is not the consumer's.
        private static void BodyWork(SpriteRenderer renderer, int file)
        {
            if (!ReferenceEquals(renderer, null) && Bindings.TryGetValue(renderer, out Binding binding)) binding.File = file;
            if ((uint)file >= (uint)Covered.Length || !Covered[file]) return;
            hits++;
        }
        private static void TileWork(GameMapTile tile, int file, int image)
        {
            if (ReferenceEquals(tile, null)) return;
            if (file == 149 && (uint)(image - 25) < 4) hits++;
        }
        private static void ColourWork(GameMapTile tile, int x)
        {
            if (!fogEnabled || ReferenceEquals(tile, null)) return;
            if (!Visible[x]) hits++;
        }

        // UnitSprites shape: static hook method, static trampoline field.
        private static BodyCall consumer1Original;
        private static void Consumer1Hook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { consumer1Original(r, file, image, colour, alt, chop, transparency); BodyWork(r, file); }
        // MediumWorldArt shape: instance hook method, readonly trampoline field.
        private sealed class InstanceConsumer
        {
            internal BodyCall Original;
            internal void Hook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
            { Original(r, file, image, colour, alt, chop, transparency); BodyWork(r, file); }
        }
        private sealed class TileConsumer
        {
            internal TileCall Original;
            internal void Hook(GameMapTile tile, int file, int image, int light) { Original(tile, file, image, light); TileWork(tile, file, image); }
        }
        private static ColourCall colourOriginal;
        private static void ColourHook(gameTile self, GameMapTile tile, Vector3Int location, int light)
        { colourOriginal(self, tile, location, light); ColourWork(tile, location.x); }

        private static readonly PaintHandler<BodySpritePaint> BrokerBody = (ref BodySpritePaint paint) => BodyWork(paint.Renderer, paint.File);
        private static readonly PaintHandler<TileGraphicPaint> BrokerTile = (ref TileGraphicPaint paint) => TileWork(paint.Tile, paint.File, paint.Image);
        private static readonly PaintHandler<TileColourPaint> BrokerColour = (ref TileColourPaint paint) => ColourWork(paint.Tile, paint.Location.x);

        private static readonly List<object> Roots = new List<object>();
        private static bool prepared;
        private static MethodInfo Target(string name) => typeof(PaintPerformanceTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

        private static void Prepare()
        {
            if (prepared) return;
            prepared = true;
            Covered[40] = true;
            for (int i = 0; i < 256; i++) Bindings[PaintEventsTests.Fake<SpriteRenderer>()] = new Binding();
            Hook hook = new Hook(Target(nameof(BodyConsumer1)), (BodyCall)Consumer1Hook);
            consumer1Original = hook.GenerateTrampoline<BodyCall>(); Roots.Add(hook);
            var instance = new InstanceConsumer();
            hook = new Hook(Target(nameof(BodyConsumerInstance1)), (BodyCall)instance.Hook);
            instance.Original = hook.GenerateTrampoline<BodyCall>(); Roots.Add(hook);
            for (int i = 0; i < 8; i++)
            {
                var stacked = new InstanceConsumer();
                hook = new Hook(Target(nameof(BodyConsumer8)), (BodyCall)stacked.Hook);
                stacked.Original = hook.GenerateTrampoline<BodyCall>(); Roots.Add(hook);
            }
            for (int i = 0; i < 2; i++)
            {
                var tile = new TileConsumer();
                hook = new Hook(Target(nameof(TileConsumer2)), (TileCall)tile.Hook);
                tile.Original = hook.GenerateTrampoline<TileCall>(); Roots.Add(hook);
            }
            hook = new Hook(Target(nameof(ColourConsumer1)), (ColourCall)ColourHook);
            colourOriginal = hook.GenerateTrampoline<ColourCall>(); Roots.Add(hook);

            Body<DormantSlot>(nameof(BodyDormant));
            Require(Body<Broker1Slot>(nameof(BodyBroker1)).TryRegister("bench", "1", BrokerBody, 0, null, false, out string reason), reason);
            Require(Body<FilteredSlot>(nameof(BodyBrokerFiltered1)).TryRegister("bench", "40", BrokerBody, 0, new[] { 40 }, false, out reason), reason);
            PaintRegistry<BodySpritePaint> eight = Body<Broker8Slot>(nameof(BodyBroker8));
            for (int i = 0; i < 8; i++) Require(eight.TryRegister("bench", i.ToString(), BrokerBody, 0, null, false, out reason), reason);
            TileGraphicSite<TileSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(TileBroker2)), typeof(void), PaintCalls.TileGraphicSignature));
            Require(TileGraphicSite<TileSlot>.Registry.TryRegister("garrison", "ruins", BrokerTile, -1, new[] { 149 }, false, out reason), reason);
            Require(TileGraphicSite<TileSlot>.Registry.TryRegister("theme", "all", BrokerTile, 1, null, false, out reason), reason);
            TileColourSite<ColourSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(ColourBroker1)), typeof(void), PaintCalls.TileColourSignature));
            Require(TileColourSite<ColourSlot>.Registry.TryRegister("fog", "tint", BrokerColour, 0, null, false, out reason), reason);
        }
        private struct DormantSlot { }
        private struct Broker1Slot { }
        private struct FilteredSlot { }
        private struct Broker8Slot { }
        private struct TileSlot { }
        private struct ColourSlot { }        private static PaintRegistry<BodySpritePaint> Body<TSlot>(string name) where TSlot : struct
        {
            BodySpriteSite<TSlot>.Bind(PaintBinding.StandIn(() => Target(name), typeof(void), PaintCalls.BodySignature), null);
            return BodySpriteSite<TSlot>.Registry;
        }
        private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

        private enum Variant { BodyPlain, BodyDormant, BodyConsumer1, BodyConsumerInstance1, BodyBroker1, BodyBrokerFiltered1, BodyConsumer8, BodyBroker8, TileConsumer2, TileBroker2, ColourConsumer1, ColourBroker1 }

        // One loop per variant, so no delegate sits between the loop and the hooked call. File 17 is not
        // covered by any consumer; tile file 10 is not a ruin.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(Variant variant, int count)
        {
            SpriteRenderer r = Renderer; GameMapTile tile = Tile; var cell = new Vector3Int(5, 6, 0);
            switch (variant)
            {
                case Variant.BodyPlain: for (int i = 0; i < count; i++) BodyPlain(r, 17, 1, 0, false, 0, 0); break;
                case Variant.BodyDormant: for (int i = 0; i < count; i++) BodyDormant(r, 17, 1, 0, false, 0, 0); break;
                case Variant.BodyConsumer1: for (int i = 0; i < count; i++) BodyConsumer1(r, 17, 1, 0, false, 0, 0); break;
                case Variant.BodyConsumerInstance1: for (int i = 0; i < count; i++) BodyConsumerInstance1(r, 17, 1, 0, false, 0, 0); break;
                case Variant.BodyBroker1: for (int i = 0; i < count; i++) BodyBroker1(r, 17, 1, 0, false, 0, 0); break;
                case Variant.BodyBrokerFiltered1: for (int i = 0; i < count; i++) BodyBrokerFiltered1(r, 17, 1, 0, false, 0, 0); break;
                case Variant.BodyConsumer8: for (int i = 0; i < count; i++) BodyConsumer8(r, 17, 1, 0, false, 0, 0); break;
                case Variant.BodyBroker8: for (int i = 0; i < count; i++) BodyBroker8(r, 17, 1, 0, false, 0, 0); break;
                case Variant.TileConsumer2: for (int i = 0; i < count; i++) TileConsumer2(tile, 10, 1, 0); break;
                case Variant.TileBroker2: for (int i = 0; i < count; i++) TileBroker2(tile, 10, 1, 0); break;
                case Variant.ColourConsumer1: for (int i = 0; i < count; i++) ColourConsumer1(null, tile, cell, 0); break;
                case Variant.ColourBroker1: for (int i = 0; i < count; i++) ColourBroker1(null, tile, cell, 0); break;
            }
        }

        [TestMethod]
        public void BrokerVersusConsumerHooksAlternatingRuns()
        {
            Prepare();
            var variants = (Variant[])Enum.GetValues(typeof(Variant));
            foreach (Variant variant in variants) Run(variant, 20000);
            var samples = variants.ToDictionary(v => v, v => new List<double>());
            var bytes = variants.ToDictionary(v => v, v => long.MaxValue);
            for (int round = 0; round < Rounds; round++)
                for (int step = 0; step < variants.Length; step++)
                {
                    Variant variant = variants[(step + round) % variants.Length];
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    Run(variant, Calls);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    bytes[variant] = Math.Min(bytes[variant], GC.GetAllocatedBytesForCurrentThread() - allocated);
                    samples[variant].Add(elapsed * (1e9 / Stopwatch.Frequency) / Calls);
                }
            var median = new Dictionary<Variant, double>();
            foreach (Variant variant in variants)
            {
                List<double> values = samples[variant].OrderBy(v => v).ToList();
                median[variant] = values[values.Count / 2];
                TestContext.WriteLine("PAINT_AB {0}: medianNs={1:F2}, minNs={2:F2}, maxNs={3:F2}, bytesPerCall={4:F2}",
                    variant, median[variant], values[0], values[values.Count - 1], bytes[variant] / (double)Calls);
            }
            foreach (Variant variant in variants)
                if (variant.ToString().Contains("Broker") || variant == Variant.BodyDormant)
                    Assert.AreEqual(0L, bytes[variant], variant + " allocates after warm-up.");
            GC.KeepAlive(hits); GC.KeepAlive(sink);
            // APIShared.csproj sets no Optimize property, so ordinary builds mark the assembly
            // "optimizer disabled" and the JIT compiles the broker unoptimized while the consumer stand-ins
            // here are optimized. Timing bounds apply only to an optimized build (-p:Optimize=true).
            bool optimized = !(typeof(ApiShared).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false);
            TestContext.WriteLine("PAINT_AB apisharedOptimized={0}", optimized);
            if (!optimized) return;
            // Generous regression bounds; the reported medians are the measurement.
            Assert.IsTrue(median[Variant.BodyDormant] <= median[Variant.BodyPlain] * 1.5 + 1, "A dormant site must cost a plain call.");
            Assert.IsTrue(median[Variant.BodyBrokerFiltered1] <= median[Variant.BodyConsumer1], "A paint no registration asked for must cost no more than one consumer hook.");
            Assert.IsTrue(median[Variant.BodyBroker8] < median[Variant.BodyConsumer8], "Eight registrations must cost less than eight hooks.");
            Assert.IsTrue(median[Variant.BodyBroker1] <= median[Variant.BodyConsumer1] + 40, "One matching registration regressed.");
        }

        [TestMethod]
        public void ObservedPaintPathsAllocateNothingAfterWarmup()
        {
            Prepare();
            foreach (Variant variant in new[] { Variant.BodyDormant, Variant.BodyBroker1, Variant.BodyBrokerFiltered1, Variant.BodyBroker8, Variant.TileBroker2, Variant.ColourBroker1 })
            {
                Run(variant, 2000);
                long best = long.MaxValue;
                for (int sample = 0; sample < 5; sample++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    Run(variant, 20000);
                    best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
                }
                Assert.AreEqual(0L, best, variant + " allocates per paint.");
            }
        }

        [TestMethod]
        public void CompiledDispatchPathsCreateNoObjectsDelegatesOrBoxes()
        {
            // A captured lambda allocates its closure at method entry, before any guard; check the compiler output.
            using (var module = ModuleDefinition.ReadModule(typeof(ApiShared).Assembly.Location))
            {
                var targets = new Dictionary<string, string[]> {
                    { "APIShared.Presentation.BodySpriteSite`1", new[] { "Paint", "Notify", "CallOriginal" } },
                    { "APIShared.Presentation.TileGraphicSite`1", new[] { "Paint", "Notify" } },
                    { "APIShared.Presentation.ChevronSite`1", new[] { "Paint", "Notify" } },
                    { "APIShared.Presentation.TileColourSite`1", new[] { "Paint", "Notify" } },
                    { "APIShared.Presentation.PixieSite`1", new[] { "Update", "Notify" } },
                    { "APIShared.Presentation.FlySite`1", new[] { "Update", "Notify" } },
                    { "APIShared.Presentation.OrgSite`1", new[] { "Update", "Notify" } },
                    { "APIShared.Presentation.BuildingAnimSite`1", new[] { "Update", "Notify" } },
                    { "APIShared.Presentation.PaintScope", new[] { "Enter", "Exit", "get_Current", "get_Tracked" } },
                    { "APIShared.Presentation.PaintRegistry`1", new[] { "ForFile", "ForAll", "Notify" } },
                    { "APIShared.Presentation.PaintClaimState", new[] { "TryClaim", "ClaimOver", "Enter", "Rollback", "get_ClaimedBy" } } };
                int checkedMethods = 0;
                foreach (var target in targets)
                {
                    TypeDefinition type = module.GetTypes().Single(t => t.FullName == target.Key);
                    foreach (string name in target.Value)
                    {
                        MethodDefinition method = type.Methods.Single(m => m.Name == name);
                        checkedMethods++;
                        foreach (Instruction instruction in method.Body.Instructions)
                        {
                            Assert.IsFalse(instruction.OpCode == OpCodes.Ldftn || instruction.OpCode == OpCodes.Ldvirtftn ||
                                instruction.OpCode == OpCodes.Newarr || instruction.OpCode == OpCodes.Box,
                                target.Key + "." + name + ": " + instruction);
                            if (instruction.OpCode == OpCodes.Newobj)
                                Assert.IsTrue(((MethodReference)instruction.Operand).DeclaringType.Resolve().IsValueType,
                                    target.Key + "." + name + " allocates " + ((MethodReference)instruction.Operand).DeclaringType.FullName);
                        }
                    }
                }
                Assert.AreEqual(29, checkedMethods);
            }
        }
    }
}
