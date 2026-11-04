using System;
using System.IO;
using System.Runtime.InteropServices;
using APIShared.Presentation;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class MapperCameraNativeTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FixtureCall();
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protect);
        [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);
        [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

        [TestMethod]
        public void ActualIndirectBackendSuppressesOnlyMatchingMapperCallerAndPreservesMachineState()
        {
            IntPtr page = VirtualAlloc(IntPtr.Zero, new UIntPtr(8192), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, page);
            IntPtr output = Marshal.AllocHGlobal(512);
            NativeDetour<FixtureCall> hook = null;
            try
            {
                ulong target = unchecked((ulong)page.ToInt64()), demand = target + 7168;
                var registers = new[] { rax, rbx, rcx, rdx, rsi, rdi, rbp, r8, r9, r10, r11, r12, r13, r14, r15, rsp };
                var simd = new[] { xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7,
                    xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15 };
                ulong[] sentinels = { 0x1111111111111111,0x2222222222222222,0x3333333333333333,0x4444444444444444,
                    0x5555555555555555,0x6666666666666666,0x7777777777777777,unchecked((ulong)-7L),
                    0x9999999999999999,0xAAAAAAAAAAAAAAAA,0xBBBBBBBBBBBBBBBB,0xCCCCCCCCCCCCCCCC,
                    0xDDDDDDDDDDDDDDDD,0xEEEEEEEEEEEEEEEE,unchecked((ulong)output.ToInt64()),0 };
                const ulong Simd = 0x7654321012345678;
                var originalCode = new Assembler(64);
                var entry = new byte[MapperCameraNative.DisplacedLength]; Array.Copy(MapperCameraNative.Window, entry, entry.Length);
                originalCode.db(entry);
                originalCode.mov(__dword_ptr[r15 + 24], 1); // Original camera side effect, without changing flags.
                originalCode.ret();
                byte[] originalBytes = Assemble(originalCode, target);
                Marshal.Copy(originalBytes, 0, page, originalBytes.Length);
                ulong[] returns = new ulong[2];
                var callers = new FixtureCall[2];
                // Two real CALL instructions: their distinct return addresses exercise
                // the exact native caller filter, including demand-active foreign callers.
                for (int caller = 0; caller < 2; caller++)
                {
                    ulong address = target + (caller == 0 ? 512UL : 3072UL);
                    var a = new Assembler(64);
                    a.push(rbx); a.push(rsi); a.push(rdi); a.push(rbp);
                    a.push(r12); a.push(r13); a.push(r14); a.push(r15);
                    a.lea(rsp, __qword_ptr[rsp - 256]);
                    for (int i = 0; i < simd.Length; i++) a.movdqu(__xmmword_ptr[rsp + i * 16], simd[i]);
                    a.lea(rsp, __qword_ptr[rsp - 40]); // Win64 shadow space plus alignment.
                    a.mov(r15, unchecked((ulong)output.ToInt64())); a.mov(__qword_ptr[r15], rsp);
                    for (int i = 0; i < simd.Length; i++)
                    { a.mov(rax, Simd + (ulong)i); a.movq(simd[i], rax); a.punpcklqdq(simd[i], simd[i]); }
                    // All arithmetic/stack setup precedes the selected flags.
                    a.mov(r11, caller == 0 ? 0xED7UL : 0x202UL); a.push(r11); a.popfq();
                    for (int i = 0; i < registers.Length - 2; i++) a.mov(registers[i], sentinels[i]);
                    a.pushfq(); a.pop(__qword_ptr[r15 + 8]);
                    a.call(target);
                    a.pushfq(); a.pop(__qword_ptr[r15 + 16]);
                    for (int i = 0; i < registers.Length; i++) a.mov(__qword_ptr[r15 + 32 + i * 8], registers[i]);
                    for (int i = 0; i < simd.Length; i++) a.movdqu(__xmmword_ptr[r15 + 160 + i * 16], simd[i]);
                    a.cld(); a.lea(rsp, __qword_ptr[rsp + 40]);
                    for (int i = 0; i < simd.Length; i++) a.movdqu(simd[i], __xmmword_ptr[rsp + i * 16]);
                    a.lea(rsp, __qword_ptr[rsp + 256]);
                    a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12);
                    a.pop(rbp); a.pop(rdi); a.pop(rsi); a.pop(rbx); a.ret();
                    byte[] bytes = Assemble(a, address);
                    var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
                    while (decoder.IP < address + (ulong)bytes.Length)
                    {
                        var instruction = decoder.Decode();
                        Assert.AreNotEqual(Code.INVALID, instruction.Code);
                        if (instruction.Mnemonic == Mnemonic.Call && instruction.NearBranchTarget == target)
                            returns[caller] = instruction.NextIP;
                    }
                    Assert.AreNotEqual(0UL, returns[caller]);
                    Marshal.Copy(bytes, 0, new IntPtr(unchecked((long)address)), bytes.Length);
                    callers[caller] = Marshal.GetDelegateForFunctionPointer<FixtureCall>(new IntPtr(unchecked((long)address)));
                }
                Assert.IsTrue(FlushInstructionCache(GetCurrentProcess(), page, new UIntPtr(8192)));
                int callbacks = 0; FixtureCall callback = () => callbacks++;
                var bridge = new MapperCameraBridge(demand, returns[0]);
                var request = new DetourRequest<FixtureCall> { Name = "private mapper camera function", TargetAddress = target,
                    Callback = callback, IntermediaryFactory = bridge };
                var backend = new NativeDetourBackend(new NativeDetourOptions {
                    AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false,
                    Chaining = HookChainingPolicy.Refuse, FunctionScanLimit = MapperCameraNative.Window.Length,
                    SuspendThreadsDuringPatch = false
                });
                hook = (NativeDetour<FixtureCall>)backend.CreateDetour(in request);
                Assert.AreEqual(7, hook.DisplacedByteCount);
                bridge.Validate(hook, page, entry, false);
                hook.Enable(); bridge.Validate(hook, page, entry, true);
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    new MapperCameraBridge(demand + 4, returns[0]).Validate(hook, page, entry, true));
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    new MapperCameraBridge(demand, returns[1]).Validate(hook, page, entry, true));
                for (int active = 0; active < 2; active++)
                for (int caller = 0; caller < 2; caller++)
                {
                    bool suppressed = active == 1 && caller == 0;
                    Marshal.WriteInt32(new IntPtr(unchecked((long)demand)), active); Marshal.WriteInt32(output, 24, 0);
                    callers[caller]();
                    Assert.AreEqual(suppressed ? 0 : 1, Marshal.ReadInt32(output, 24));
                    ulong[] expected = (ulong[])sentinels.Clone(); expected[15] = unchecked((ulong)Marshal.ReadInt64(output));
                    if (!suppressed) { expected[0] = unchecked((ulong)-7L); expected[7] = unchecked((ulong)-21L); }
                    for (int i = 0; i < registers.Length; i++)
                        Assert.AreEqual(expected[i], unchecked((ulong)Marshal.ReadInt64(output, 32 + i * 8)), "register " + i);
                    Assert.AreEqual(Marshal.ReadInt64(output, 8), Marshal.ReadInt64(output, 16), "flags");
                    for (int i = 0; i < simd.Length * 2; i++)
                        Assert.AreEqual(Simd + (ulong)(i / 2), unchecked((ulong)Marshal.ReadInt64(output, 160 + i * 8)), "SIMD " + i);
                }
                Assert.AreEqual(0, callbacks, "Both paths must remain native.");
                GC.KeepAlive(callers); GC.KeepAlive(callback);
            }
            finally
            {
                // Private fixture only; game hooks and process-lifetime demand are never disposed.
                hook?.Dispose(); Marshal.FreeHGlobal(output); Assert.IsTrue(VirtualFree(page, UIntPtr.Zero, 0x8000));
            }
        }
        private static byte[] Assemble(Assembler a, ulong address)
        { using (var stream = new MemoryStream()) { a.Assemble(new StreamCodeWriter(stream), address); return stream.ToArray(); } }
    }
}
