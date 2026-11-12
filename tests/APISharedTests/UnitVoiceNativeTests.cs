using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Audio;
using CrusaderDE;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class UnitVoiceNativeTests
    {
        // Published private hooks deliberately remain rooted for this test process. No real
        // CrusaderDE code is patched; thread suspension is disabled for isolated copies only.
        private static readonly List<object> roots = new List<object>();

        [TestMethod]
        public void ChangedOptionalEntryCanBeRejectedWithoutRejectingTheOtherEntryContracts()
        {
            int length = UnitVoiceNativeContract.Targets.Max(target => target.Rva + target.Prologue.Length);
            var original = new byte[length];
            foreach (var target in UnitVoiceNativeContract.Targets) Array.Copy(target.Prologue, 0, original, target.Rva, target.Prologue.Length);
            var live = (byte[])original.Clone();
            var changed = UnitVoiceNativeContract.Targets.Single(target => target.Key == "Recruitment.Arabian");
            live[changed.Rva] ^= 1;
            var rejected = new List<string>();
            foreach (var target in UnitVoiceNativeContract.Targets)
            {
                try { UnitVoiceNativeContract.ValidateEntry(original, live, target); }
                catch (InvalidOperationException) { rejected.Add(target.Key); }
            }
            CollectionAssert.AreEqual(new[] { changed.Key }, rejected, "Optional source preparation uses independent entry checks; the mandatory output remains valid.");
        }

        [TestMethod]
        public void EverySourcePrologueUsesTheRealBackendAndRelocatesItsOriginalTargets()
        {
            var files = new InstalledAudioFiles(InstalledTestEnvironment.GameDirectory());
            files.Validate(UnitVoiceNativeContract.Hash);
            byte[] file = File.ReadAllBytes(files.Native);
            foreach (var target in UnitVoiceNativeContract.Targets)
            {
                IntPtr memory = VirtualAlloc(IntPtr.Zero, new UIntPtr(0x1000), 0x3000, 0x40);
                Assert.AreNotEqual(IntPtr.Zero, memory);
                Marshal.Copy(file, UnitVoiceCatalogTests.Offset(file, target.Rva), memory, target.FunctionLength);
                UnitVoiceNativeRuntime.NativeCall callback = (a, b, c, d) => { };
                IntPtr demand = Marshal.AllocHGlobal(1); Marshal.WriteByte(demand, 0);
                var bridge = new UnitVoiceNativeGuardBridge(callback, demand);
                var request = new DetourRequest<UnitVoiceNativeRuntime.NativeCall> {
                    Name = "unit-voice-prologue-fixture." + target.Key, TargetAddress = unchecked((ulong)memory.ToInt64()),
                    Callback = callback, IntermediaryFactory = bridge
                };
                var hook = (NativeDetour<UnitVoiceNativeRuntime.NativeCall>)NativeContract.Backend(target.FunctionLength, false).CreateDetour(in request);
                roots.Add(memory); roots.Add(demand); roots.Add(callback); roots.Add(bridge); roots.Add(hook);
                UnitVoiceNativeContract.ValidateHook(hook, target, request.TargetAddress); bridge.Validate(hook);
                hook.Enable();
                UnitVoiceNativeContract.ValidateHook(hook, target, request.TargetAddress); bridge.Validate(hook);
                // The copied body still contains game data references. Decode/validate it here;
                // executing those unrelocated body references would not be a valid game test.
            }
        }

        [TestMethod]
        public void GuardExecutesBothDemandBranchesAndPreservesAllFourArgumentsAndOriginalCount()
        {
            var function = ExecutableFunction.Create(asm => {
                asm.mov(__qword_ptr[rcx], rdx); asm.mov(__qword_ptr[rcx + 8], r8);
                asm.mov(__qword_ptr[rcx + 16], r9); asm.inc(__qword_ptr[rcx + 24]); asm.ret();
            });
            byte[] code = Read(function.EntryPoint, 64);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
            int length = 0;
            while (length < 14) length += decoder.Decode().Length;
            var target = new UnitVoiceNativeTarget("fixture", 0, 4, 64,
                BitConverter.ToString(code, 0, length).Replace("-", ""));
            IntPtr storage = Marshal.AllocHGlobal(32); for (int i = 0; i < 32; i++) Marshal.WriteByte(storage, i, 0);
            IntPtr demand = Marshal.AllocHGlobal(1); Marshal.WriteByte(demand, 0);
            int callbacks = 0;
            NativeDetour<UnitVoiceNativeRuntime.NativeCall> hook = null;
            UnitVoiceNativeRuntime.NativeCall callback = (a, b, c, d) => { callbacks++; hook.Original(a, b, c, d); };
            var bridge = new UnitVoiceNativeGuardBridge(callback, demand);
            var request = new DetourRequest<UnitVoiceNativeRuntime.NativeCall> {
                Name = "unit-voice-guard-execution-fixture", TargetAddress = unchecked((ulong)function.EntryPoint.ToInt64()),
                Callback = callback, IntermediaryFactory = bridge
            };
            hook = (NativeDetour<UnitVoiceNativeRuntime.NativeCall>)NativeContract.Backend(64, false).CreateDetour(in request);
            roots.Add(function); roots.Add(storage); roots.Add(demand); roots.Add(callback); roots.Add(bridge); roots.Add(hook);
            UnitVoiceNativeContract.ValidateHook(hook, target, request.TargetAddress); bridge.Validate(hook); hook.Enable();
            UnitVoiceNativeContract.ValidateHook(hook, target, request.TargetAddress); bridge.Validate(hook);
            byte[] before = Read(hook.HookEntryPointAddress, 48);
            var invoke = Marshal.GetDelegateForFunctionPointer<UnitVoiceNativeRuntime.NativeCall>(function.EntryPoint); roots.Add(invoke);
            ulong bValue = 0x123456789ABCDEF0, cValue = 0xFFFFFFFFFFFFABCD, dValue = 0x1122334455667788;
            invoke(unchecked((ulong)storage.ToInt64()), bValue, cValue, dValue);
            Assert.AreEqual(0, callbacks); Assert.AreEqual(1L, Marshal.ReadInt64(storage, 24));
            Assert.AreEqual(bValue, unchecked((ulong)Marshal.ReadInt64(storage)));
            Assert.AreEqual(cValue, unchecked((ulong)Marshal.ReadInt64(storage, 8)));
            Assert.AreEqual(dValue, unchecked((ulong)Marshal.ReadInt64(storage, 16)));
            Marshal.WriteByte(demand, 1);
            invoke(unchecked((ulong)storage.ToInt64()), bValue, cValue, dValue);
            Assert.AreEqual(1, callbacks); Assert.AreEqual(2L, Marshal.ReadInt64(storage, 24));
            Marshal.WriteByte(demand, 0);
            invoke(unchecked((ulong)storage.ToInt64()), bValue, cValue, dValue);
            Assert.AreEqual(1, callbacks); Assert.AreEqual(3L, Marshal.ReadInt64(storage, 24));
            CollectionAssert.AreEqual(before, Read(hook.HookEntryPointAddress, 48), "Activation changes data, never executable bridge bytes.");
        }

        // The civilian guard calls managed code only for a nonzero selection that differs from the previous one,
        // the only case in which 0x97DA0 reaches its speech output. All four arguments reach the original either way.
        [TestMethod]
        public void SelectionGuardCallsBackOnlyForANewNonzeroSelection()
        {
            var function = ExecutableFunction.Create(asm => {
                asm.mov(__qword_ptr[rcx], rdx); asm.mov(__qword_ptr[rcx + 8], r8);
                asm.mov(__qword_ptr[rcx + 16], r9); asm.inc(__qword_ptr[rcx + 24]); asm.ret();
            });
            byte[] code = Read(function.EntryPoint, 64);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(code));
            int length = 0;
            while (length < 14) length += decoder.Decode().Length;
            var target = new UnitVoiceNativeTarget("selection-fixture", 0, 2, 64,
                BitConverter.ToString(code, 0, length).Replace("-", ""));
            IntPtr storage = Marshal.AllocHGlobal(32); for (int i = 0; i < 32; i++) Marshal.WriteByte(storage, i, 0);
            IntPtr demand = Marshal.AllocHGlobal(1); Marshal.WriteByte(demand, 1);
            IntPtr selected = Marshal.AllocHGlobal(4), previous = Marshal.AllocHGlobal(4);
            int callbacks = 0;
            NativeDetour<UnitVoiceNativeRuntime.NativeCall> hook = null;
            UnitVoiceNativeRuntime.NativeCall callback = (a, b, c, d) => { callbacks++; hook.Original(a, b, c, d); };
            var bridge = new UnitVoiceNativeGuardBridge(callback, demand,
                unchecked((ulong)selected.ToInt64()), unchecked((ulong)previous.ToInt64()));
            var request = new DetourRequest<UnitVoiceNativeRuntime.NativeCall> {
                Name = "unit-voice-selection-guard-fixture", TargetAddress = unchecked((ulong)function.EntryPoint.ToInt64()),
                Callback = callback, IntermediaryFactory = bridge
            };
            hook = (NativeDetour<UnitVoiceNativeRuntime.NativeCall>)NativeContract.Backend(64, false).CreateDetour(in request);
            roots.Add(function); roots.Add(storage); roots.Add(demand); roots.Add(selected); roots.Add(previous);
            roots.Add(callback); roots.Add(bridge); roots.Add(hook);
            UnitVoiceNativeContract.ValidateHook(hook, target, request.TargetAddress); bridge.Validate(hook); hook.Enable();
            bridge.Validate(hook);
            var invoke = Marshal.GetDelegateForFunctionPointer<UnitVoiceNativeRuntime.NativeCall>(function.EntryPoint); roots.Add(invoke);
            ulong bValue = 0x123456789ABCDEF0, cValue = 0xFFFFFFFFFFFFABCD, dValue = 0x1122334455667788;
            void Call(int selection, int last)
            {
                Marshal.WriteInt32(selected, selection); Marshal.WriteInt32(previous, last);
                invoke(unchecked((ulong)storage.ToInt64()), bValue, cValue, dValue);
                Assert.AreEqual(bValue, unchecked((ulong)Marshal.ReadInt64(storage)));
                Assert.AreEqual(cValue, unchecked((ulong)Marshal.ReadInt64(storage, 8)));
                Assert.AreEqual(dValue, unchecked((ulong)Marshal.ReadInt64(storage, 16)));
            }
            Call(0, 7); Assert.AreEqual(0, callbacks, "No selection: Vanilla returns before speaking.");
            Call(7, 7); Assert.AreEqual(0, callbacks, "Unchanged selection: Vanilla does not speak.");
            Call(8, 7); Assert.AreEqual(1, callbacks, "A new selection may speak and is attributed.");
            Assert.AreEqual(3L, Marshal.ReadInt64(storage, 24), "The original ran exactly once per call.");
            Marshal.WriteByte(demand, 0);
            Call(9, 7); Assert.AreEqual(1, callbacks, "Without demand the demand guard still skips first.");
            Assert.AreEqual(4L, Marshal.ReadInt64(storage, 24));
        }

        [TestMethod]
        public void RealEngineerEntryExecutesBothUnsignedBranchOutcomesOnPrivateSafeEndpoints()
        {
            var profile = UnitVoiceNativeContract.Targets.Single(value => value.Key == "Engineer.SpecialActions");
            IntPtr memory = VirtualAlloc(IntPtr.Zero, new UIntPtr(0x1000), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, memory);
            Marshal.Copy(profile.Prologue, 0, memory, profile.Prologue.Length);
            var decode = Decoder.Create(64, new ByteArrayCodeReader(profile.Prologue));
            decode.IP = unchecked((ulong)memory.ToInt64()); decode.Decode(); var branch = decode.Decode();
            var taken = new IntPtr(unchecked((long)branch.NearBranchTarget));
            var normal = ExecutableFunction.Create(asm => {
                asm.add(rsp, 0x70); asm.pop(rbx); asm.inc(__qword_ptr[rcx]); asm.ret();
            });
            var early = ExecutableFunction.Create(asm => { asm.inc(__qword_ptr[rcx + 8]); asm.ret(); });
            byte[] normalBytes = UntilReturn(normal.EntryPoint), earlyBytes = UntilReturn(early.EntryPoint);
            Marshal.Copy(normalBytes, 0, memory + profile.Prologue.Length, normalBytes.Length);
            Marshal.Copy(earlyBytes, 0, taken, earlyBytes.Length);
            IntPtr counters = Marshal.AllocHGlobal(16); Marshal.WriteInt64(counters, 0); Marshal.WriteInt64(counters, 8, 0);
            IntPtr demand = Marshal.AllocHGlobal(1); Marshal.WriteByte(demand, 0);
            int callbacks = 0;
            NativeDetour<UnitVoiceNativeRuntime.NativeCall> hook = null;
            UnitVoiceNativeRuntime.NativeCall callback = (a, b, c, d) => { callbacks++; hook.Original(a, b, c, d); };
            var bridge = new UnitVoiceNativeGuardBridge(callback, demand);
            var request = new DetourRequest<UnitVoiceNativeRuntime.NativeCall> {
                Name = "unit-voice-engineer-branch-fixture", TargetAddress = unchecked((ulong)memory.ToInt64()),
                Callback = callback, IntermediaryFactory = bridge
            };
            hook = (NativeDetour<UnitVoiceNativeRuntime.NativeCall>)NativeContract.Backend(512, false).CreateDetour(in request);
            roots.Add(memory); roots.Add(normal); roots.Add(early); roots.Add(counters); roots.Add(demand); roots.Add(callback); roots.Add(bridge); roots.Add(hook);
            UnitVoiceNativeContract.ValidateHook(hook, profile, request.TargetAddress); bridge.Validate(hook); hook.Enable();
            UnitVoiceNativeContract.ValidateHook(hook, profile, request.TargetAddress); bridge.Validate(hook);
            var invoke = Marshal.GetDelegateForFunctionPointer<UnitVoiceNativeRuntime.NativeCall>(memory); roots.Add(invoke);
            foreach (byte enabled in new byte[] { 0, 1 })
            {
                Marshal.WriteByte(demand, enabled);
                invoke(unchecked((ulong)counters.ToInt64()), 0, 0, 0);
                invoke(unchecked((ulong)counters.ToInt64()), uint.MaxValue, 0, 0);
            }
            Assert.AreEqual(2L, Marshal.ReadInt64(counters)); Assert.AreEqual(2L, Marshal.ReadInt64(counters, 8));
            Assert.AreEqual(2, callbacks, "Both active calls enter the callback; both dormant calls remain native.");
        }

        [TestMethod]
        public void TransportRestoresEveryTokenAndBindsTheLastWriterToTheExactPlayState()
        {
            UnitVoiceTransport.ResetForTest();
            Assert.IsTrue(UnitVoiceCatalog.TryResolveFileName("Bedouin_M1.wav", out var first));
            Assert.IsTrue(UnitVoiceCatalog.TryResolveFileName("Eunuch_S1.wav", out var last));
            string firstToken = UnitVoiceTransport.CreateToken(first, out long firstId);
            string lastToken = UnitVoiceTransport.CreateToken(last, out long lastId);
            var a = new UnitVoiceSource(1, null, (int)eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER, first.VoiceBankKey, first.Key, UnitVoiceSituation.Move, UnitVoiceEvidence.NativeAudited, 17, "Troop.Dispatcher");
            var b = new UnitVoiceSource(2, null, (int)eChimps.CHIMP_TYPE_BEDOUIN_EUNUCH, last.VoiceBankKey, last.Key, UnitVoiceSituation.Selection, UnitVoiceEvidence.NativeAudited, 17, "Troop.Dispatcher");
            UnitVoiceTransport.Publish(firstToken, "fx\\speech\\Bedouin_M1.wav", a, firstId);
            UnitVoiceTransport.Publish(lastToken, "fx\\speech\\Eunuch_S1.wav", b, lastId);
            var state = new EngineInterface.PlayState { speechFileName = lastToken };
            UnitVoiceTransport.BindPlayState(state);
            Assert.AreEqual("fx\\speech\\Eunuch_S1.wav", state.speechFileName);
            Assert.IsTrue(UnitVoiceTransport.TryGetBinding(state, out var binding));
            Assert.AreEqual(2, binding.Source.RequestingPlayerId); Assert.IsNull(binding.Source.OwnerPlayerId);
            Assert.AreEqual(lastId, binding.EmissionId);
            Assert.IsTrue(UnitVoiceTransport.TryGetBinding(state, out var repeated));
            Assert.AreSame(binding, repeated, "Multiple record-18 entries share one immutable state binding, rather than consuming it.");

            var overwritten = new EngineInterface.PlayState { speechFileName = "fx\\speech\\Eunuch_S1.wav" };
            UnitVoiceTransport.BindPlayState(overwritten);
            Assert.IsFalse(UnitVoiceTransport.TryGetBinding(overwritten, out _), "An untagged writer using the same filename is not the unit source.");
            var older = new EngineInterface.PlayState { speechFileName = firstToken };
            UnitVoiceTransport.BindPlayState(older);
            Assert.AreEqual("fx\\speech\\" + first.FileName, older.speechFileName);
            Assert.IsFalse(UnitVoiceTransport.TryGetBinding(older, out _), "A lost or superseded snapshot restores the file without inventing provenance.");
            Assert.IsTrue(firstToken.All(character => character <= 127)); Assert.IsTrue(firstToken.Length < 128);

            UnitVoiceTransport.ResetForTest();
            var afterEnd = new EngineInterface.PlayState { speechFileName = lastToken };
            UnitVoiceTransport.BindPlayState(afterEnd);
            Assert.AreEqual("fx\\speech\\" + last.FileName, afterEnd.speechFileName);
            Assert.IsFalse(UnitVoiceTransport.TryGetBinding(afterEnd, out _));
        }

        private static byte[] Read(IntPtr pointer, int count)
        { var bytes = new byte[count]; Marshal.Copy(pointer, bytes, 0, count); return bytes; }
        private static byte[] UntilReturn(IntPtr pointer)
        {
            var bytes = Read(pointer, 64); var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            int length = 0;
            while (length < bytes.Length)
            { var instruction = decoder.Decode(); length += instruction.Length; if (instruction.Mnemonic == Mnemonic.Ret) return bytes.Take(length).ToArray(); }
            throw new InvalidOperationException("Fixture return missing.");
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protection);
    }
}
