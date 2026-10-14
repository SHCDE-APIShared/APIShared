using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace APISharedTests
{
    // Where the cost of one registration receiving a body paint goes, compared with one consumer hook doing
    // the same work. Each variant is a static hook on its own stand-in and adds one element of the broker's
    // observed path: an out-of-line call with the paint's arguments, a by-ref value of 16 bytes, the real
    // 56-byte BodySpritePaint, then delegate invocation with failure isolation (the shape of a dedicated
    // single-registration path), then the production site. Two alternatives keep the by-ref value at
    // 16 bytes by holding the request in a frame object (static, or per thread). Reported, not asserted;
    // .NET Framework only, see docs/PAINT.md.
    [TestClass, DoNotParallelize]
    public sealed class PaintCostBreakdownTests
    {
        public TestContext TestContext { get; set; }
        private static int sink, hits;
        private static readonly Dictionary<SpriteRenderer, object> Bindings = new Dictionary<SpriteRenderer, object>(new ReferenceComparer());
        private static readonly bool[] Covered = new bool[1024];
        private sealed class ReferenceComparer : IEqualityComparer<SpriteRenderer>
        {
            public bool Equals(SpriteRenderer a, SpriteRenderer b) => ReferenceEquals(a, b);
            public int GetHashCode(SpriteRenderer value) => RuntimeHelpers.GetHashCode(value);
        }
        private static void Work(SpriteRenderer renderer, int file)
        {
            if (!ReferenceEquals(renderer, null) && Bindings.TryGetValue(renderer, out object binding)) GC.KeepAlive(binding);
            if ((uint)file >= (uint)Covered.Length || !Covered[file]) return;
            hits++;
        }

        private delegate void BodyCall(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency);
        private struct Value16 { internal SpriteRenderer Renderer; internal int File, Image; }
        private sealed class Frame { internal SpriteRenderer Renderer; internal int File, Image, Colour, Chop, Transparency, Generation; internal bool Alt; }
        private struct FrameValue { internal Frame Frame; internal int Generation, Caller; }
        private delegate void FrameHandler(ref FrameValue value);
        private static readonly PaintHandler<BodySpritePaint> PaintWork = (ref BodySpritePaint paint) => Work(paint.Renderer, paint.File);
        private static readonly FrameHandler FrameWork = (ref FrameValue value) => Work(value.Frame.Renderer, value.Frame.File);
        private static readonly Frame staticFrame = new Frame();
        [ThreadStatic] private static Frame threadFrame;
        private static PaintEntry<BodySpritePaint>[] entries;

        [MethodImpl(MethodImplOptions.NoInlining)] private static void Consumer(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Call(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Small(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Paint(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Single(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Production(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void StaticFrame(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void ThreadFrame(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => sink += file;
        private static BodyCall consumer, call, small, paint, single, frameStatic, frameThread;

        private static void ConsumerHook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { consumer(r, file, image, colour, alt, chop, transparency); Work(r, file); }
        private static void CallHook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { call(r, file, image, colour, alt, chop, transparency); if (entries.Length != 0) CallNotify(r, file, image, colour, alt, chop, transparency); }
        private static void SmallHook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { small(r, file, image, colour, alt, chop, transparency); if (entries.Length != 0) SmallNotify(r, file, image, colour, alt, chop, transparency); }
        private static void PaintHook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { paint(r, file, image, colour, alt, chop, transparency); if (entries.Length != 0) PaintNotify(r, file, image, colour, alt, chop, transparency); }
        private static void SingleHook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { single(r, file, image, colour, alt, chop, transparency); if (entries.Length != 0) SingleNotify(r, file, image, colour, alt, chop, transparency); }
        private static void StaticFrameHook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { frameStatic(r, file, image, colour, alt, chop, transparency); if (entries.Length != 0) StaticFrameNotify(r, file, image, colour, alt, chop, transparency); }
        private static void ThreadFrameHook(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { frameThread(r, file, image, colour, alt, chop, transparency); if (entries.Length != 0) ThreadFrameNotify(r, file, image, colour, alt, chop, transparency); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CallNotify(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency) => Work(r, file);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SmallNotify(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { Value16 value; value.Renderer = r; value.File = file; value.Image = image; SmallWork(ref value); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void SmallWork(ref Value16 value) => Work(value.Renderer, value.File);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PaintNotify(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        { var value = new BodySpritePaint(r, file, image, colour, alt, chop, transparency, entries); PaintDirect(ref value); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void PaintDirect(ref BodySpritePaint value) => Work(value.Renderer, value.File);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SingleNotify(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        {
            var value = new BodySpritePaint(r, file, image, colour, alt, chop, transparency, entries);
            try { PaintWork(ref value); } catch (Exception error) { GC.KeepAlive(error); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StaticFrameNotify(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        {
            Frame frame = staticFrame;
            frame.Renderer = r; frame.File = file; frame.Image = image; frame.Colour = colour; frame.Alt = alt; frame.Chop = chop; frame.Transparency = transparency;
            FrameValue value; value.Frame = frame; value.Generation = ++frame.Generation; value.Caller = 1;
            try { FrameWork(ref value); } catch (Exception error) { GC.KeepAlive(error); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThreadFrameNotify(SpriteRenderer r, int file, int image, int colour, bool alt, int chop, int transparency)
        {
            Frame frame = threadFrame ?? (threadFrame = new Frame());
            frame.Renderer = r; frame.File = file; frame.Image = image; frame.Colour = colour; frame.Alt = alt; frame.Chop = chop; frame.Transparency = transparency;
            FrameValue value; value.Frame = frame; value.Generation = ++frame.Generation; value.Caller = 1;
            try { FrameWork(ref value); } catch (Exception error) { GC.KeepAlive(error); }
        }

        private struct ProductionSlot { }
        private static readonly List<object> Roots = new List<object>();
        private static MethodInfo Target(string name) => typeof(PaintCostBreakdownTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        private static BodyCall Hook(string target, BodyCall hook)
        {
            var installed = new Hook(Target(target), hook);
            Roots.Add(installed);
            return installed.GenerateTrampoline<BodyCall>();
        }

        [TestMethod]
        public void OneReceivingRegistrationCostBreakdown()
        {
            for (int i = 0; i < 256; i++) Bindings[PaintEventsTests.Fake<SpriteRenderer>()] = null;
            BodySpriteSite<ProductionSlot>.Bind(PaintBinding.StandIn(() => Target(nameof(Production)), typeof(void), PaintCalls.BodySignature), null);
            Assert.IsTrue(BodySpriteSite<ProductionSlot>.Registry.TryRegister("bench", "one", PaintWork, 0, null, false, out string reason), reason);
            entries = BodySpriteSite<ProductionSlot>.Registry.ForFile(17);
            consumer = Hook(nameof(Consumer), ConsumerHook); call = Hook(nameof(Call), CallHook); small = Hook(nameof(Small), SmallHook);
            paint = Hook(nameof(Paint), PaintHook); single = Hook(nameof(Single), SingleHook);
            frameStatic = Hook(nameof(StaticFrame), StaticFrameHook); frameThread = Hook(nameof(ThreadFrame), ThreadFrameHook);
            SpriteRenderer renderer = PaintEventsTests.Fake<SpriteRenderer>();
            var variants = new (string Name, Action<int> Run)[] {
                ("consumer hook", n => { for (int i = 0; i < n; i++) Consumer(renderer, 17, 1, 0, false, 0, 0); }),
                ("+ out-of-line call", n => { for (int i = 0; i < n; i++) Call(renderer, 17, 1, 0, false, 0, 0); }),
                ("+ 16-byte value", n => { for (int i = 0; i < n; i++) Small(renderer, 17, 1, 0, false, 0, 0); }),
                ("+ 56-byte BodySpritePaint", n => { for (int i = 0; i < n; i++) Paint(renderer, 17, 1, 0, false, 0, 0); }),
                ("+ delegate and try (single path)", n => { for (int i = 0; i < n; i++) Single(renderer, 17, 1, 0, false, 0, 0); }),
                ("production site", n => { for (int i = 0; i < n; i++) Production(renderer, 17, 1, 0, false, 0, 0); }),
                ("alternative: static frame", n => { for (int i = 0; i < n; i++) StaticFrame(renderer, 17, 1, 0, false, 0, 0); }),
                ("alternative: thread-static frame", n => { for (int i = 0; i < n; i++) ThreadFrame(renderer, 17, 1, 0, false, 0, 0); }) };
            var samples = variants.ToDictionary(v => v.Name, v => new List<double>());
            foreach (var variant in variants) variant.Run(50000);
            for (int round = 0; round < 15; round++)
                for (int step = 0; step < variants.Length; step++)
                {
                    var variant = variants[(step + round) % variants.Length];
                    long start = Stopwatch.GetTimestamp();
                    variant.Run(200000);
                    samples[variant.Name].Add((Stopwatch.GetTimestamp() - start) * (1e9 / Stopwatch.Frequency) / 200000);
                }
            foreach (var variant in variants)
            {
                List<double> values = samples[variant.Name].OrderBy(v => v).ToList();
                TestContext.WriteLine("PAINT_BREAKDOWN {0}: medianNs={1:F2}, minNs={2:F2}", variant.Name, values[values.Count / 2], values[0]);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            variants[5].Run(20000);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before, "The production path allocates.");
            GC.KeepAlive(sink); GC.KeepAlive(hits);
        }
    }
}
