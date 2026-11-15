using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using APIShared;
using APIShared.Buildings;
using APIShared.NativeSites;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    // Only private executable memory is detoured. No installed-game hook or service is initialized.
    [TestClass, DoNotParallelize]
    public sealed class HumanConstructionNativeTests
    {
        [TestMethod]
        public void NativeUndoFieldsUseTwoShortsAtPendingParameterStartWithoutCustomEnvelope()
        {
            // Exact native pending-slot layout: offset17 begins opcode-specific parameters;
            // it is not a universal body-length field. Manager payloadSize is deliberately
            // absent: at queued entry it can still describe the previously executed opcode.
            foreach (var expected in new[] { (Mode: (short)1, Slot: (short)25), (Mode: (short)2, Slot: (short)3999) })
            {
                byte[] pending = new byte[0x500];
                Buffer.BlockCopy(BitConverter.GetBytes(expected.Mode), 0, pending, 17, 2);
                Buffer.BlockCopy(BitConverter.GetBytes(expected.Slot), 0, pending, 19, 2);
                // Bytes where the old decoder incorrectly expected mode/slot are unrelated.
                pending[21] = 0xFF; pending[23] = 0xFF;
                Assert.IsTrue(HumanConstructionRuntime.TryDecodeDisbandPending(pending, 0, out short mode, out short slot));
                Assert.AreEqual(expected.Mode, mode); Assert.AreEqual(expected.Slot, slot);
                foreach (int cursor in new[] { -1, 1, 2, 4 })
                    Assert.IsFalse(HumanConstructionRuntime.TryDecodeDisbandPending(pending, cursor, out _, out _), "Only an untouched execute-entry cursor is valid.");
                Assert.IsFalse(HumanConstructionRuntime.TryDecodeDisbandPending(new ReadOnlySpan<byte>(pending, 0, 20), 0, out _, out _), "Both complete native int16 fields are required.");
                pending[19] = 0; pending[20] = 0;
                Assert.IsFalse(HumanConstructionRuntime.TryDecodeDisbandPending(pending, 0, out _, out _), "Slot zero is invalid.");
                pending[19] = 0xFF; pending[20] = 0xFF;
                Assert.IsFalse(HumanConstructionRuntime.TryDecodeDisbandPending(pending, 0, out _, out _), "Native int16 slots are signed.");
            }
            byte[] customEnvelope = new byte[0x500];
            Buffer.BlockCopy(BitConverter.GetBytes(4), 0, customEnvelope, 17, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((short)1), 0, customEnvelope, 21, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((short)25), 0, customEnvelope, 23, 2);
            Assert.IsFalse(HumanConstructionRuntime.TryDecodeDisbandPending(customEnvelope, 0, out _, out _), "The custom106 length-prefixed format is not native Undo.");
            customEnvelope[17] = 0;
            Assert.IsFalse(HumanConstructionRuntime.TryDecodeDisbandPending(customEnvelope, 0, out _, out _), "Unknown Undo modes are refused.");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RemoveFixture(IntPtr manager, int tile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DisbandFixture();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        private static byte[] Entry => (byte[])typeof(HumanConstructionRuntime).GetField("DecorationEntry", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        private static NativeDetourOptions BackendOptions => (NativeDetourOptions)typeof(HumanConstructionRuntime)
            .GetProperty("DecorationBackendOptions", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        private static byte[] DisbandEntry => (byte[])typeof(HumanConstructionRuntime).GetField("DisbandEntry", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        private static NativeDetourOptions DisbandOptions => (NativeDetourOptions)typeof(HumanConstructionRuntime)
            .GetProperty("DisbandBackendOptions", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        [TestMethod]
        public void CanonicalDecorationRemovalHasCompleteSevenByteEntryAndNoInteriorBranch()
        {
            byte[] file = File.ReadAllBytes(Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"));
            using (var hash = SHA256.Create())
                Assert.AreEqual(ApiSharedRuntime.SupportedHash, BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""));
            byte[] function = AtRva(file, HumanConstructionRuntime.DecorationRemovalRva, 121);
            using (var hash = SHA256.Create())
                Assert.AreEqual("E3F245F3E19DA54AD3C4AF52E3729A217DFAAC8198B1B21B25734222DE9719E9", BitConverter.ToString(hash.ComputeHash(function)).Replace("-", ""));
            CollectionAssert.AreEqual(Entry, new ArraySegment<byte>(function, 0, 7).ToArray());
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(function));
            decoder.IP = (ulong)HumanConstructionRuntime.DecorationRemovalRva;
            ulong start = decoder.IP, end = start + (ulong)function.Length;
            decoder.Decode(out Instruction first);
            decoder.Decode(out Instruction second);
            Assert.AreEqual(4, first.Length);
            Assert.AreEqual(3, second.Length);
            Assert.AreEqual(start + 7, decoder.IP, "The next instruction starts exactly after the selected Indirect span.");
            while (decoder.IP < end)
            {
                decoder.Decode(out Instruction instruction);
                Assert.AreNotEqual(Code.INVALID, instruction.Code);
                if (instruction.FlowControl == FlowControl.ConditionalBranch || instruction.FlowControl == FlowControl.UnconditionalBranch || instruction.FlowControl == FlowControl.Call)
                    Assert.IsFalse(instruction.NearBranchTarget > start && instruction.NearBranchTarget < start + 7,
                        "No branch in the complete canonical function may enter displaced instruction bytes.");
            }
            Assert.AreEqual(end, decoder.IP);
            // This is a hash-bound function-local control-flow proof, not a module-wide xref scan.
            // External entry/xref review remains part of the preserved native audit.
        }

        [TestMethod]
        public void ProductionIndirectPolicyDisplacesSevenBytesAndRunsOriginalExactlyOnce()
        {
            using (var target = ExecutableFunction.Create(a => {
                a.db(Entry);
                a.inc(__dword_ptr[rcx]);
                a.mov(__qword_ptr[rcx + 8], rax);
                a.add(rsp, 8);
                a.ret();
            }))
            {
                IntPtr output = Marshal.AllocHGlobal(16);
                NativeDetour<RemoveFixture> hook = null;
                bool active = false;
                int callbacks = 0, policyCalls = 0;
                var callback = new RemoveFixture((manager, tile) => {
                    callbacks++;
                    if (active) policyCalls++;
                    hook.Original(manager, tile);
                });
                try
                {
                    for (int i = 0; i < 16; i += 4) Marshal.WriteInt32(output, i, 0);
                    var options = BackendOptions with { SuspendThreadsDuringPatch = false };
                    Assert.AreEqual(DetourScheme.Indirect, options.AllowedSchemes);
                    Assert.IsFalse(options.FollowJumps);
                    Assert.IsFalse(options.FollowCalls);
                    Assert.AreEqual(121, options.FunctionScanLimit);
                    var backend = new NativeDetourBackend(options);
                    var request = new DetourRequest<RemoveFixture> { Name = "Private construction decoration fixture", Callback = callback, TargetAddress = unchecked((ulong)target.EntryPoint.ToInt64()) };
                    hook = (NativeDetour<RemoveFixture>)backend.CreateDetour(in request);
                    NativeIndirectEntry.Validate(hook, target.EntryPoint, Entry, 7, false);
                    Assert.AreEqual(7, hook.DisplacedByteCount);
                    var invoke = Marshal.GetDelegateForFunctionPointer<RemoveFixture>(target.EntryPoint);
                    invoke(output, -7);
                    Assert.AreEqual(0, callbacks);
                    Assert.AreEqual(-7L, Marshal.ReadInt64(output, 8));
                    hook.Enable();
                    NativeIndirectEntry.Validate(hook, target.EntryPoint, Entry, 7, true);
                    invoke(output, -123);
                    Assert.AreEqual(1, callbacks);
                    Assert.AreEqual(0, policyCalls, "Dormant policy preserves the complete original.");
                    Assert.AreEqual(2, Marshal.ReadInt32(output));
                    Assert.AreEqual(-123L, Marshal.ReadInt64(output, 8), "The displaced sign extension is executed.");
                    active = true;
                    invoke(output, 456);
                    Assert.AreEqual(2, callbacks);
                    Assert.AreEqual(1, policyCalls);
                    Assert.AreEqual(3, Marshal.ReadInt32(output), "Exactly one original call per invocation.");
                    Assert.AreEqual(456L, Marshal.ReadInt64(output, 8));
                    GC.KeepAlive(callback); GC.KeepAlive(invoke);
                }
                finally { hook?.Dispose(); Marshal.FreeHGlobal(output); }
                // Disposal is safe only here: no other thread can execute this private fixture.
            }
        }

        [TestMethod]
        public void CanonicalDisbandHasCompleteSixByteEntryAndNoInteriorBranch()
        {
            byte[] file = File.ReadAllBytes(Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"));
            using (var hash = SHA256.Create())
                Assert.AreEqual(ApiSharedRuntime.SupportedHash, BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""));
            byte[] function = AtRva(file, HumanConstructionRuntime.DisbandRva, HumanConstructionRuntime.DisbandLength);
            using (var hash = SHA256.Create())
                Assert.AreEqual(HumanConstructionRuntime.DisbandHash, BitConverter.ToString(hash.ComputeHash(function)).Replace("-", ""));
            CollectionAssert.AreEqual(DisbandEntry, new ArraySegment<byte>(function, 0, 6).ToArray());
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(function));
            decoder.IP = (ulong)HumanConstructionRuntime.DisbandRva;
            ulong start = decoder.IP, end = start + (ulong)function.Length;
            decoder.Decode(out Instruction first); decoder.Decode(out Instruction second);
            Assert.AreEqual(2, first.Length); Assert.AreEqual(4, second.Length);
            Assert.AreEqual(start + 6, decoder.IP);
            while (decoder.IP < end)
            {
                decoder.Decode(out Instruction instruction);
                Assert.AreNotEqual(Code.INVALID, instruction.Code);
                if (instruction.FlowControl == FlowControl.ConditionalBranch || instruction.FlowControl == FlowControl.UnconditionalBranch || instruction.FlowControl == FlowControl.Call)
                    Assert.IsFalse(instruction.NearBranchTarget > start && instruction.NearBranchTarget < start + 6,
                        "No branch in the full disband handler may enter displaced bytes.");
            }
            Assert.AreEqual(end, decoder.IP);
        }

        [TestMethod]
        public void ProductionDisbandIndirectPolicyDisplacesSixBytesAndRunsOriginalOnce()
        {
            IntPtr output = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(output, 0);
                using (var target = ExecutableFunction.Create(a => {
                    a.db(DisbandEntry); a.mov(rax, unchecked((ulong)output.ToInt64()));
                    a.inc(__dword_ptr[rax]); a.add(rsp, 0x30); a.pop(rbx); a.ret();
                }))
                {
                    NativeDetour<DisbandFixture> hook = null;
                    bool active = false; int callbacks = 0, policyCalls = 0;
                    var callback = new DisbandFixture(() => { callbacks++; if (active) policyCalls++; hook.Original(); });
                    try
                    {
                        var options = DisbandOptions with { SuspendThreadsDuringPatch = false };
                        Assert.AreEqual(DetourScheme.Indirect, options.AllowedSchemes);
                        Assert.IsFalse(options.FollowJumps); Assert.IsFalse(options.FollowCalls);
                        Assert.AreEqual(HumanConstructionRuntime.DisbandLength, options.FunctionScanLimit);
                        var backend = new NativeDetourBackend(options);
                        var request = new DetourRequest<DisbandFixture> { Name = "Private construction disband fixture", Callback = callback, TargetAddress = unchecked((ulong)target.EntryPoint.ToInt64()) };
                        hook = (NativeDetour<DisbandFixture>)backend.CreateDetour(in request);
                        byte[] relocated = new byte[hook.TrampolineSize];
                        Marshal.Copy(hook.TrampolineAddress, relocated, 0, relocated.Length);
                        Assert.AreEqual(10, relocated.Length);
                        CollectionAssert.AreEqual(new byte[] { 0x53, 0x48, 0x83, 0xEC, 0x30 }, new ArraySegment<byte>(relocated, 0, 5).ToArray(),
                            "The installed backend removes only the redundant PUSH REX prefix.");
                        HumanConstructionRuntime.ValidateDisbandEntry(hook, target.EntryPoint, false);
                        Assert.IsTrue(VirtualProtect(hook.TrampolineAddress, (UIntPtr)relocated.Length, 0x40, out uint protection));
                        try
                        {
                            foreach (int offset in new[] { 0, 4, 6 })
                            {
                                // No thread executes this prepared private trampoline. Prove rejection
                                // of changed register, stack size and jump continuation, then restore.
                                byte original = Marshal.ReadByte(hook.TrampolineAddress, offset);
                                try
                                {
                                    Marshal.WriteByte(hook.TrampolineAddress, offset, (byte)(original ^ 1));
                                    Assert.ThrowsExactly<InvalidOperationException>(() => HumanConstructionRuntime.ValidateDisbandEntry(hook, target.EntryPoint, false));
                                }
                                finally { Marshal.WriteByte(hook.TrampolineAddress, offset, original); }
                            }
                        }
                        finally
                        {
                            Assert.IsTrue(VirtualProtect(hook.TrampolineAddress, (UIntPtr)relocated.Length, protection, out _));
                            Assert.IsTrue(FlushInstructionCache(new IntPtr(-1), hook.TrampolineAddress, (UIntPtr)relocated.Length));
                        }
                        Assert.AreEqual(6, hook.DisplacedByteCount);
                        var invoke = Marshal.GetDelegateForFunctionPointer<DisbandFixture>(target.EntryPoint);
                        invoke(); Assert.AreEqual(0, callbacks); Assert.AreEqual(1, Marshal.ReadInt32(output));
                        hook.Enable(); HumanConstructionRuntime.ValidateDisbandEntry(hook, target.EntryPoint, true);
                        invoke(); Assert.AreEqual(1, callbacks); Assert.AreEqual(0, policyCalls); Assert.AreEqual(2, Marshal.ReadInt32(output));
                        active = true;
                        invoke(); Assert.AreEqual(2, callbacks); Assert.AreEqual(1, policyCalls); Assert.AreEqual(3, Marshal.ReadInt32(output));
                        GC.KeepAlive(invoke); GC.KeepAlive(callback);
                    }
                    finally { hook?.Dispose(); }
                    // This private fixture cannot be reached by another thread; no published game hook is removed.
                }
            }
            finally { Marshal.FreeHGlobal(output); }
        }

        private static byte[] AtRva(byte[] file, int rva, int length)
        {
            int pe = BitConverter.ToInt32(file, 0x3C);
            int sections = pe + 24 + BitConverter.ToUInt16(file, pe + 20);
            for (int i = 0; i < BitConverter.ToUInt16(file, pe + 6); i++)
            {
                int section = sections + i * 40, start = BitConverter.ToInt32(file, section + 12);
                int size = BitConverter.ToInt32(file, section + 16);
                if (rva < start || (long)rva + length > (long)start + size) continue;
                var bytes = new byte[length];
                Array.Copy(file, BitConverter.ToInt32(file, section + 20) + rva - start, bytes, 0, length);
                return bytes;
            }
            throw new InvalidOperationException("Decoration removal is outside the canonical PE sections.");
        }
    }
}
