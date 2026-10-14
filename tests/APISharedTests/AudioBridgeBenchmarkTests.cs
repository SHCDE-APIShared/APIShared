using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using APIShared.Audio;
using APIShared;
using BepInEx.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    // Production hooks (SoundRuntime.CreateHooks: real bridges, demand bytes, OnWrapper/OnEmit and
    // RedBird trampolines) on private copies of the real wrapper and emitter. A native loop calls the
    // copied wrapper, so each sample has one managed->native transition for 20000 wrapper calls.
    // Desktop CLR on the test host, not Unity Mono: transition costs differ in game.
    [TestClass]
    [DoNotParallelize]
    public sealed unsafe class AudioBridgeBenchmarkTests
    {
        public TestContext TestContext { get; set; }
        private static readonly List<object> roots = new List<object>();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Loop(int count);
        private const int Calls = 20000, Rounds = 31;

        [TestMethod]
        public void DormantAndObservedWrapperCostOnCopiedNativeCode()
        {
            string game = Environment.GetEnvironmentVariable("SHCDE_GAME_DIR") ?? @"E:\ProgrammeE\Steam\steamapps\common\Stronghold Crusader Definitive Edition";
            var files = new InstalledAudioFiles(game); files.Validate();
            byte[] image = File.ReadAllBytes(files.Native);
            IntPtr vanilla = Page(image, out IntPtr vanillaManager);
            var vanillaLoop = LoopOver(vanilla, out _);
            // Unattributed origin: the loop's return PC maps to a small non-catalog RVA, so the
            // catalog lookups still run. Catalog origin: it maps to 0x1700D2 (Arabian draw, effect
            // 180, flag 0) with current unit 0, the rejected-attribution path minus UnitAccess work.
            var plainLoop = Install(image, null, out IntPtr plainManager);
            var catalogLoop = Install(image, 0x1700D2, out IntPtr catalogManager);

            var status = typeof(SoundRuntime).GetField("playbackStatus", BindingFlags.Static | BindingFlags.NonPublic); var old = status.GetValue(null);
            bool wasReady = SoundContextPolicy.Active; SoundRegistration source = null, playback = null;
            var process = Process.GetCurrentProcess(); var priority = process.PriorityClass; var threadPriority = System.Threading.Thread.CurrentThread.Priority;
            try
            {
                Assert.AreEqual(0, SoundEvents.Demand.ActiveCount); Assert.AreEqual(0, SoundSourceEvents.Demand.ActiveCount);
                status.SetValue(null, "Ready"); SoundContextPolicy.SetReady(true);
                string owner = "bridge.bench." + Guid.NewGuid();
                Assert.IsTrue(SoundSourceEvents.TryRegister(owner, "source", (ref SoundSourceArgs a) => { }, out source, out _));
                Assert.IsTrue(SoundEvents.TryRegister(owner, "playback", a => { }, null, out playback, out _));
                var modes = new (string Name, Action Apply, Loop Loop, IntPtr Manager)[] {
                    ("vanilla-unhooked", () => { source.SetEnabled(false); playback.SetEnabled(false); }, vanillaLoop, vanillaManager),
                    ("dormant", () => { source.SetEnabled(false); playback.SetEnabled(false); }, plainLoop, plainManager),
                    ("source-demand-unattributed", () => { playback.SetEnabled(false); source.SetEnabled(true); }, plainLoop, plainManager),
                    ("playback-demand-unattributed", () => { source.SetEnabled(false); playback.SetEnabled(true); }, plainLoop, plainManager),
                    ("playback-demand-catalog-rejected", () => { source.SetEnabled(false); playback.SetEnabled(true); }, catalogLoop, catalogManager),
                };
                var samples = modes.Select(_ => new List<double>()).ToArray(); var bytes = new long[modes.Length];
                var wrapperBytes = new byte[modes.Length]; var emitterBytes = new byte[modes.Length];
                // Raised priority reduces scheduler noise on a shared machine; restored below.
                process.PriorityClass = ProcessPriorityClass.High; System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;
                for (int round = -2; round < Rounds; round++)
                    for (int index = 0; index < modes.Length; index++)
                    {
                        // Alternate the mode order each round so drift affects every mode alike.
                        int mode = (round & 1) == 0 ? index : modes.Length - 1 - index;
                        modes[mode].Apply();
                        wrapperBytes[mode] = SoundNativeDemand.WrapperValue; emitterBytes[mode] = SoundNativeDemand.EmitterValue;
                        double ns = Sample(modes[mode].Loop, modes[mode].Manager, out long allocated);
                        if (round < 0) continue;
                        samples[mode].Add(ns); bytes[mode] += allocated;
                    }
                process.PriorityClass = priority; System.Threading.Thread.CurrentThread.Priority = threadPriority;
                for (int mode = 0; mode < modes.Length; mode++)
                {
                    var sorted = samples[mode].OrderBy(value => value).ToArray();
                    Assert.AreEqual(0L, bytes[mode], modes[mode].Name + " allocates.");
                    TestContext.WriteLine("AUDIO_BRIDGE_BENCH mode={0},wrapperByte={1},emitterByte={2},bytesPerCall={3:F1},nsPerCallMin={4:F2},nsPerCallMedian={5:F2},samples={6}x{7}",
                        modes[mode].Name, wrapperBytes[mode], emitterBytes[mode], bytes[mode] / (double)(Rounds * Calls), sorted[0], sorted[sorted.Length / 2], Rounds, Calls);
                }
                Assert.AreEqual(0, (int)wrapperBytes[1]); Assert.AreEqual(0, (int)emitterBytes[1]);
                Assert.AreEqual(1, (int)wrapperBytes[2]); Assert.AreEqual(0, (int)emitterBytes[2]);
                Assert.AreEqual(1, (int)wrapperBytes[3]); Assert.AreEqual(1, (int)emitterBytes[3]);
            }
            finally
            {
                process.PriorityClass = priority; System.Threading.Thread.CurrentThread.Priority = threadPriority;
                source?.SetEnabled(false); playback?.SetEnabled(false); status.SetValue(null, old); SoundContextPolicy.SetReady(wasReady);
            }
        }

        // One SoundRuntime with production hooks on its own copied page; returns the loop over it.
        private static Loop Install(byte[] image, int? catalogReturnRva, out IntPtr manager)
        {
            IntPtr page = Page(image, out manager);
            var runtime = new SoundRuntime(new ManualLogSource("audio-bench"), new NativeOwnershipRegistry());
            ulong wrapper = unchecked((ulong)page.ToInt64()), flag = wrapper + 64, emit = wrapper + 256;
            runtime.CreateHooks(wrapper, flag, emit, suspendThreads: false);
            runtime.ValidateHooks(wrapper, flag, emit);
            runtime.WrapperHook.Enable(); runtime.FlagWrapperHook.Enable(); runtime.EmitHook.Enable();
            runtime.ValidateHooks(wrapper, flag, emit);
            roots.Add(runtime);
            var loop = LoopOver(page, out IntPtr entry);
            var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(ReadCode(entry, 64)));
            decoder.IP = unchecked((ulong)entry.ToInt64());
            Iced.Intel.Instruction call;
            do call = decoder.Decode(); while (call.Mnemonic != Iced.Intel.Mnemonic.Call);
            ulong moduleBase = catalogReturnRva.HasValue ? call.NextIP - (ulong)catalogReturnRva.Value : call.NextIP - 0x10000;
            typeof(SoundRuntime).GetField("moduleBase", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, moduleBase);
            typeof(SoundRuntime).GetField("killReady", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, true);
            IntPtr unit = Marshal.AllocHGlobal(8); Marshal.WriteInt64(unit, 0); roots.Add(unit);
            typeof(SoundRuntime).GetField("currentUnit", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, Pointer.Box(unit.ToPointer(), typeof(int*)));
            return loop;
        }
        private static byte[] ReadCode(IntPtr pointer, int length) { var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return bytes; }

        private static double Sample(Loop loop, IntPtr manager, out long bytes)
        {
            Marshal.WriteInt32(manager, 8, 0);
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            loop(Calls);
            long elapsed = Stopwatch.GetTimestamp() - start; bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            IntPtr records = Marshal.ReadIntPtr(manager);
            Assert.AreEqual(Calls, Marshal.ReadInt32(manager, 8), "Each wrapper call appends exactly one record.");
            Assert.AreEqual((short)180, Marshal.ReadInt16(records, (Calls - 1) * 42 + 6));
            return elapsed * (1e9 / Stopwatch.Frequency) / Calls;
        }

        // Copies of wrapper 0x2960 (+0), flagged wrapper 0x2990 (+64) and emitter 0x1A41E0 (+256) with
        // their manager LEA and emitter CALL relocated into the page; manager at +1024.
        private static IntPtr Page(byte[] image, out IntPtr manager)
        {
            IntPtr page = VirtualAlloc(IntPtr.Zero, new UIntPtr(4096), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, page);
            Marshal.Copy(image, Offset(image, NativeContract.WrapperRva), page, 40);
            Marshal.Copy(image, Offset(image, NativeContract.FlagWrapperRva), page + 64, 40);
            Marshal.Copy(image, Offset(image, NativeContract.EmitRva), page + 256, 137);
            foreach (int at in new[] { 0, 64 }) { Marshal.WriteInt32(page, at + 21, 1024 - (at + 25)); Marshal.WriteInt32(page, at + 31, 256 - (at + 35)); }
            IntPtr records = Marshal.AllocHGlobal(42 * (Calls + 64));
            manager = page + 1024; Marshal.WriteIntPtr(manager, records); Marshal.WriteInt32(manager, 8, 0);
            roots.Add(page); roots.Add(records);
            return page;
        }
        private static Loop LoopOver(IntPtr target, out IntPtr entry)
        {
            var code = RedBird.X64.Assembly.ExecutableFunction.Create(asm => {
                var next = asm.CreateLabel();
                asm.push(rbx); asm.push(rbp); asm.sub(rsp, 0x28);
                asm.mov(ebx, ecx);
                asm.Label(ref next);
                asm.mov(ecx, 123); asm.mov(edx, -17); asm.mov(r8d, 300); asm.mov(r9d, 180);
                asm.mov(rax, unchecked((ulong)target.ToInt64()));
                asm.call(rax);
                asm.dec(ebx); asm.jne(next);
                asm.add(rsp, 0x28); asm.pop(rbp); asm.pop(rbx); asm.ret();
            });
            var loop = Marshal.GetDelegateForFunctionPointer<Loop>(code.EntryPoint);
            roots.Add(code); roots.Add(loop); entry = code.EntryPoint;
            return loop;
        }
        private static int Offset(byte[] image, int rva)
        {
            int pe = BitConverter.ToInt32(image, 0x3C), count = BitConverter.ToUInt16(image, pe + 6), optional = BitConverter.ToUInt16(image, pe + 20);
            for (int i = 0; i < count; i++) { int h = pe + 24 + optional + i * 40, start = BitConverter.ToInt32(image, h + 12), size = BitConverter.ToInt32(image, h + 16);
                if (rva >= start && rva < start + size) return BitConverter.ToInt32(image, h + 20) + rva - start; }
            throw new InvalidOperationException("Unmapped test RVA");
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protection);
    }
}
