using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using APIShared;
using APIShared.Audio;
using BepInEx.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    // Source selections: public creation rules, per-source demand counts, dispatch filtering and the
    // native return filter on private copies of the real wrapper (production CreateHooks bridges).
    [TestClass]
    [DoNotParallelize]
    public sealed unsafe class SoundSourceSelectionTests
    {
        private static readonly List<object> roots = new List<object>();
        private const string Draw = "Unit75.Source04", Swing = "Unit75.Source03";
        private static int Index(string key) => Array.FindIndex(SoundSources.All, source => source.Key == key);
        private static SoundSourceSelection Select(params string[] keys)
        {
            Assert.IsTrue(SoundSourceSelection.TryCreate(keys, null, out var selection, out string reason), reason);
            return selection;
        }

        [TestMethod]
        public void SelectionsResolveKeysAndSituationsAndRejectWhatIsNeverPublished()
        {
            Assert.IsTrue(SoundSourceSelection.All.IsAll);
            Assert.AreEqual(SoundSources.All.Length - 1 + SoundSources.Kills.Length, SoundSourceSelection.SelectableSourceKeys.Count, "every attributable soldier source and kill cue");
            Assert.IsFalse(SoundSourceSelection.SelectableSourceKeys.Contains("Unit55.Source05"), "a source without a named situation is never published");
            CollectionAssert.AreEqual(SoundSourceSelection.SelectableSourceKeys.ToArray(), SoundSourceSelection.All.SourceKeys.ToArray());

            Assert.IsTrue(SoundSourceSelection.TryCreate(null, new[] { SoundSituation.ArabianIdleSwordDraw }, out var draw, out _));
            CollectionAssert.AreEqual(new[] { Draw }, draw.SourceKeys.ToArray());
            Assert.IsFalse(draw.IsAll); Assert.IsTrue(draw.Contains(Draw)); Assert.IsFalse(draw.Contains(Swing)); Assert.IsFalse(draw.Contains(null));
            Assert.IsTrue(SoundSourceSelection.TryCreate(null, new[] { SoundSituation.MeleeKill }, out var kills, out _));
            CollectionAssert.AreEqual(SoundSources.Kills.Select(kill => kill.Key).ToArray(), kills.SourceKeys.ToArray());
            Assert.IsTrue(SoundSourceSelection.TryCreate(new[] { Swing }, new[] { SoundSituation.ArabianIdleSwordDraw }, out var union, out _));
            CollectionAssert.AreEqual(new[] { Swing, Draw }, union.SourceKeys.ToArray(), "union in catalog order");
            var swings = SoundSources.All.Where(source => source.Effects.Any(effect => source.Situation(effect) == SoundSituation.MeleeWeaponSwing)).Select(source => source.Key).ToArray();
            Assert.IsTrue(SoundSourceSelection.TryCreate(null, new[] { SoundSituation.MeleeWeaponSwing }, out var swing, out _));
            CollectionAssert.AreEqual(swings, swing.SourceKeys.ToArray());

            foreach (var failure in new (string[] Keys, SoundSituation[] Situations)[] {
                (new[] { "Unit75.Source99" }, null), (new string[] { null }, null), (new[] { "unit75.source04" }, null), (new[] { "Unit55.Source05" }, null),
                (null, new[] { SoundSituation.Unknown }), (null, new[] { (SoundSituation)999 }), (null, null), (new string[0], new SoundSituation[0]) })
            {
                Assert.IsFalse(SoundSourceSelection.TryCreate(failure.Keys, failure.Situations, out var rejected, out string reason));
                Assert.IsNull(rejected); Assert.AreNotEqual(string.Empty, reason);
            }
        }

        [TestMethod]
        public void RegistrationsCountOnlyTheirSourcesAndPlaybackIgnoresKillCues()
        {
            var status = typeof(SoundRuntime).GetField("playbackStatus", BindingFlags.Static | BindingFlags.NonPublic); var old = status.GetValue(null);
            SoundRegistration playback = null, source = null, all = null;
            int draw = Index(Draw), swing = Index(Swing), kill = SoundSources.Kills[0].DemandIndex;
            try
            {
                status.SetValue(null, "Ready"); string owner = "selection." + Guid.NewGuid();
                Assert.IsTrue(SoundSourceSelection.TryCreate(null, new[] { SoundSituation.MeleeKill }, out var kills, out _));
                Assert.IsFalse(SoundEvents.TryRegister(owner, "kills", a => { }, null, kills, out _, out string reason), "kill cues never reach playback");
                Assert.AreNotEqual(string.Empty, reason);
                Assert.IsFalse(SoundEvents.TryRegister(owner, "null", a => { }, null, null, out _, out _));
                Assert.IsFalse(SoundSourceEvents.TryRegister(owner, "null", (ref SoundSourceArgs a) => { }, null, out _, out _));
                int playbackDraw = SoundNativeDemand.PlaybackCount(draw), playbackSwing = SoundNativeDemand.PlaybackCount(swing), playbackKill = SoundNativeDemand.PlaybackCount(kill);
                int sourceDraw = SoundNativeDemand.SourceCount(draw), sourceKill = SoundNativeDemand.SourceCount(kill);

                Assert.IsTrue(SoundSourceSelection.TryCreate(new[] { Draw }, new[] { SoundSituation.MeleeKill }, out var drawAndKills, out _));
                Assert.IsTrue(SoundEvents.TryRegister(owner, "draw", a => { }, null, drawAndKills, out playback, out _));
                Assert.AreSame(drawAndKills, playback.Sources);
                Assert.AreEqual(playbackDraw + 1, SoundNativeDemand.PlaybackCount(draw));
                Assert.AreEqual(playbackSwing, SoundNativeDemand.PlaybackCount(swing));
                Assert.AreEqual(playbackKill, SoundNativeDemand.PlaybackCount(kill), "playback never counts kill cues");
                Assert.IsTrue(SoundSourceEvents.TryRegister(owner, "kills", (ref SoundSourceArgs a) => { }, kills, out source, out _));
                Assert.AreEqual(sourceKill + 1, SoundNativeDemand.SourceCount(kill)); Assert.AreEqual(sourceDraw, SoundNativeDemand.SourceCount(draw));
                Assert.IsTrue(SoundSourceEvents.TryRegister(owner, "all", (ref SoundSourceArgs a) => { }, out all, out _));
                Assert.AreSame(SoundSourceSelection.All, all.Sources);
                Assert.AreEqual(sourceKill + 2, SoundNativeDemand.SourceCount(kill)); Assert.AreEqual(sourceDraw + 1, SoundNativeDemand.SourceCount(draw));

                playback.SetEnabled(false); source.SetEnabled(false); all.SetEnabled(false);
                Assert.AreEqual(playbackDraw, SoundNativeDemand.PlaybackCount(draw)); Assert.AreEqual(sourceKill, SoundNativeDemand.SourceCount(kill));
                Assert.AreEqual(sourceDraw, SoundNativeDemand.SourceCount(draw));
                // Concurrent transitions of differently selected handles stay balanced per source.
                var handles = new[] { playback, source, all };
                Parallel.For(0, handles.Length, index => { for (int i = 0; i < 1000; i++) { handles[index].SetEnabled(true); handles[index].SetEnabled(false); } });
                Assert.AreEqual(playbackDraw, SoundNativeDemand.PlaybackCount(draw)); Assert.AreEqual(sourceKill, SoundNativeDemand.SourceCount(kill));
                Assert.AreEqual(sourceDraw, SoundNativeDemand.SourceCount(draw));
            }
            finally { playback?.SetEnabled(false); source?.SetEnabled(false); all?.SetEnabled(false); status.SetValue(null, old); }
        }

        [TestMethod]
        public void DispatchReachesOnlyRegistrationsSelectingTheSource()
        {
            var calls = new List<string>();
            SoundSourceEvents.Registration Entry(string id, SoundSourceSelection sources) =>
                new SoundSourceEvents.Registration("dispatch", id, (ref SoundSourceArgs a) => calls.Add(id), 0, new SoundRegistration(sources));
            Assert.IsTrue(SoundSourceSelection.TryCreate(null, new[] { SoundSituation.MeleeKill }, out var kills, out _));
            var snapshot = new[] { Entry("all", SoundSourceSelection.All), Entry("draw", Select(Draw)), Entry("kills", kills) };
            var args = new SoundSourceArgs(180, 1, 2, false, SoundSituation.ArabianIdleSwordDraw, SituationEvidence.InGameConfirmed, Draw, 9, 1u, 75, null, null);
            SoundSourceEvents.Dispatch(ref args, snapshot, Index(Draw));
            CollectionAssert.AreEqual(new[] { "all", "draw" }, calls);
            calls.Clear(); SoundSourceEvents.Dispatch(ref args, snapshot, SoundSources.Kills[2].DemandIndex);
            CollectionAssert.AreEqual(new[] { "all", "kills" }, calls);
            calls.Clear(); SoundSourceEvents.Dispatch(ref args, snapshot, Index(Swing));
            CollectionAssert.AreEqual(new[] { "all" }, calls);
        }

        // Production hooks on two private copies whose loop return addresses map to the Arabian draw
        // (0x1700D2, effect 180) and swing (0x16FEE5, effect 145) call sites. A managed entry is visible as one rejected
        // attribution per call (current unit 0). Selections change only filter bits: the bridge
        // code is byte-identical before and after every transition.
        [TestMethod]
        public void NativeFilterAdmitsOnlySelectedSourcesWithoutRepatchingTheBridge()
        {
            string game = InstalledTestEnvironment.GameDirectory();
            var files = new InstalledAudioFiles(game); files.Validate();
            byte[] image = File.ReadAllBytes(files.Native);
            var drawSite = Install(image, 0x1700D2, 180, out var drawLoop, out var drawRuntime, out ulong drawReturn);
            var swingSite = Install(image, 0x16FEE5, 145, out var swingLoop, out var swingRuntime, out ulong swingReturn);
            var drawFilter = Filter(drawRuntime); var swingFilter = Filter(swingRuntime);
            byte[] drawBridge = Code(drawRuntime), swingBridge = Code(swingRuntime);

            var status = typeof(SoundRuntime).GetField("playbackStatus", BindingFlags.Static | BindingFlags.NonPublic); var old = status.GetValue(null);
            bool wasReady = SoundContextPolicy.Active; SoundRegistration selected = null, all = null;
            try
            {
                Assert.AreEqual(0, SoundEvents.Demand.ActiveCount); Assert.AreEqual(0, SoundSourceEvents.Demand.ActiveCount);
                status.SetValue(null, "Ready"); SoundContextPolicy.SetReady(true);
                Assert.IsFalse(drawFilter.Contains(drawReturn) || swingFilter.Contains(swingReturn), "no demand, no admitted origin");
                string owner = "filter." + Guid.NewGuid();
                Assert.IsTrue(SoundEvents.TryRegister(owner, "draw", a => { }, null, Select(Draw), out selected, out _));
                Assert.IsTrue(drawFilter.Contains(drawReturn) && !swingFilter.Contains(swingReturn), "only the selected origin is admitted");
                Assert.AreEqual(1, SoundNativeDemand.WrapperValue);
                Assert.AreEqual(0L, Run(swingLoop, swingSite, swingRuntime), "unselected catalog origin stays native with active playback demand");
                Assert.AreEqual(Calls, Run(drawLoop, drawSite, drawRuntime), "selected origin enters managed code once per call");

                Assert.IsTrue(SoundSourceEvents.TryRegister(owner, "all", (ref SoundSourceArgs a) => { }, out all, out _));
                Assert.IsTrue(swingFilter.Contains(swingReturn), "an All registration admits every source");
                Assert.AreEqual(Calls, Run(swingLoop, swingSite, swingRuntime));
                all.SetEnabled(false);
                Assert.IsFalse(swingFilter.Contains(swingReturn)); Assert.IsTrue(drawFilter.Contains(drawReturn));
                Assert.AreEqual(0L, Run(swingLoop, swingSite, swingRuntime), "disabling the All handle clears only its extra sources");
                selected.SetEnabled(false);
                Assert.IsFalse(drawFilter.Contains(drawReturn)); Assert.AreEqual(0, SoundNativeDemand.WrapperValue);
                Assert.AreEqual(0L, Run(drawLoop, drawSite, drawRuntime), "dormant again");

                // Concurrent transitions: an enabled selecting handle always finds its bit set.
                Parallel.Invoke(
                    () => { for (int i = 0; i < 2000; i++) { selected.SetEnabled(true); if (!drawFilter.Contains(drawReturn)) throw new InvalidOperationException("False zero for a selected source."); selected.SetEnabled(false); } },
                    () => { for (int i = 0; i < 2000; i++) { all.SetEnabled(true); if (!drawFilter.Contains(drawReturn)) throw new InvalidOperationException("False zero for an All handle."); all.SetEnabled(false); } });
                Assert.IsFalse(drawFilter.Contains(drawReturn) || swingFilter.Contains(swingReturn));
                CollectionAssert.AreEqual(drawBridge, Code(drawRuntime), "bridge code unchanged");
                CollectionAssert.AreEqual(swingBridge, Code(swingRuntime), "bridge code unchanged");
                drawRuntime.ValidateHooks(drawSite, drawSite + 64, drawSite + 256); swingRuntime.ValidateHooks(swingSite, swingSite + 64, swingSite + 256);
            }
            finally { selected?.SetEnabled(false); all?.SetEnabled(false); status.SetValue(null, old); SoundContextPolicy.SetReady(wasReady); }
        }

        private const int Calls = 2000;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Loop(int count);
        private static long Rejections(SoundRuntime runtime) => (long)typeof(SoundRuntime).GetField("rejections", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime);
        private static SoundReturnFilter Filter(SoundRuntime runtime) => (SoundReturnFilter)typeof(SoundRuntime).GetField("returnFilter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime);
        private static byte[] Code(SoundRuntime runtime) => Read(runtime.WrapperHook.HookEntryPointAddress, 96).Concat(Read(runtime.FlagWrapperHook.HookEntryPointAddress, 96)).ToArray();
        private static long Run(Loop loop, ulong site, SoundRuntime runtime)
        {
            IntPtr manager = new IntPtr(unchecked((long)site)) + 1024;
            Marshal.WriteInt32(manager, 8, 0);
            long before = Rejections(runtime);
            loop(Calls);
            Assert.AreEqual(Calls, Marshal.ReadInt32(manager, 8), "every call appends exactly one record");
            return Rejections(runtime) - before;
        }
        // Same page layout as AudioBridgeBenchmarkTests: wrapper +0, flagged wrapper +64, emitter +256, manager +1024.
        private static ulong Install(byte[] image, int returnRva, int sound, out Loop loop, out SoundRuntime runtime, out ulong returnAddress)
        {
            IntPtr page = VirtualAlloc(IntPtr.Zero, new UIntPtr(4096), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, page);
            Marshal.Copy(image, Offset(image, NativeContract.WrapperRva), page, 40);
            Marshal.Copy(image, Offset(image, NativeContract.FlagWrapperRva), page + 64, 40);
            Marshal.Copy(image, Offset(image, NativeContract.EmitRva), page + 256, 137);
            foreach (int at in new[] { 0, 64 }) { Marshal.WriteInt32(page, at + 21, 1024 - (at + 25)); Marshal.WriteInt32(page, at + 31, 256 - (at + 35)); }
            IntPtr records = Marshal.AllocHGlobal(42 * (Calls + 64));
            Marshal.WriteIntPtr(page + 1024, records); Marshal.WriteInt32(page + 1024, 8, 0);
            ulong wrapper = unchecked((ulong)page.ToInt64());
            var code = RedBird.X64.Assembly.ExecutableFunction.Create(asm => {
                var next = asm.CreateLabel();
                asm.push(rbx); asm.push(rbp); asm.sub(rsp, 0x28);
                asm.mov(ebx, ecx);
                asm.Label(ref next);
                asm.mov(ecx, 123); asm.mov(edx, -17); asm.mov(r8d, 300); asm.mov(r9d, sound);
                asm.mov(rax, wrapper);
                asm.call(rax);
                asm.dec(ebx); asm.jne(next);
                asm.add(rsp, 0x28); asm.pop(rbp); asm.pop(rbx); asm.ret();
            });
            loop = Marshal.GetDelegateForFunctionPointer<Loop>(code.EntryPoint);
            var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(Read(code.EntryPoint, 64)));
            decoder.IP = unchecked((ulong)code.EntryPoint.ToInt64());
            Iced.Intel.Instruction call;
            do call = decoder.Decode(); while (call.Mnemonic != Iced.Intel.Mnemonic.Call);
            returnAddress = call.NextIP;
            runtime = new SoundRuntime(new ManualLogSource("audio-selection"), new NativeOwnershipRegistry());
            typeof(SoundRuntime).GetField("moduleBase", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, returnAddress - (ulong)returnRva);
            runtime.CreateHooks(wrapper, wrapper + 64, wrapper + 256, suspendThreads: false);
            runtime.ValidateHooks(wrapper, wrapper + 64, wrapper + 256);
            runtime.WrapperHook.Enable(); runtime.FlagWrapperHook.Enable(); runtime.EmitHook.Enable();
            runtime.ValidateHooks(wrapper, wrapper + 64, wrapper + 256);
            IntPtr unit = Marshal.AllocHGlobal(8); Marshal.WriteInt64(unit, 0);
            typeof(SoundRuntime).GetField("currentUnit", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, Pointer.Box(unit.ToPointer(), typeof(int*)));
            roots.Add(page); roots.Add(records); roots.Add(code); roots.Add(loop); roots.Add(runtime); roots.Add(unit);
            return wrapper;
        }
        private static byte[] Read(IntPtr pointer, int length) { var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return bytes; }
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
