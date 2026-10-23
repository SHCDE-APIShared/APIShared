using System;
using System.IO;
using System.Runtime.InteropServices;
using APIShared.Compatibility;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Core.Memory;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    // Private executable memory only. Models the audited seller frame and executes
    // the installed emitter's bytes; no game function or published hook is touched.
    internal static class FixesWheatExecutionFixture
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Execute(int player, int good);

        internal static void Run(byte[] emitted, NativeFlagArray flags)
        {
            IntPtr memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);
            if (memory == IntPtr.Zero) throw new InvalidOperationException("Wheat fixture allocation failed");
            try
            {
                ulong image = unchecked((ulong)memory.ToInt64()), stub = image + 0x100, exit = image + 0x200;
                byte[] complete = FixesWheatTestStub.Audited(flags.Address, exit, 0x50);
                Array.Copy(emitted, complete, emitted.Length);
                // The source emitter was assembled without RIP-relative data or
                // external branches; its relative internal labels survive this copy.
                Marshal.Copy(complete, 0, new IntPtr(unchecked((long)stub)), complete.Length);
                // The fixture frame keeps the player at +0x68, like the installed seller's home slot.
                Assert.AreEqual(emitted[FixesWheatTestStub.OperandOffset] != 0x78, FixesWheatAnalysis.Correct(
                    () => FixesWheatMemory.ReadAvailable(stub, complete.Length),
                    (offset, value) => FixesWheatMemory.WriteOperand(stub + (uint)offset, value),
                    bytes => FixesWheatAnalysis.AnalyzeStub(bytes, stub, flags.Address, FixesWheatTestStub.Original, exit, 0x68)));
                var continuation = new Assembler(64);
                continuation.mov(eax, ecx);
                continuation.add(rsp, 0x88);
                continuation.pop(rbp);
                continuation.pop(rdi);
                continuation.ret();
                Write(continuation, exit);
                var run = (Execute)Marshal.GetDelegateForFunctionPointer(memory, typeof(Execute));
                foreach (ulong oldSlot in new[] { 0UL, 0x7FFC3E96C950UL, ulong.MaxValue })
                {
                    var frame = new Assembler(64);
                    frame.push(rdi);
                    frame.push(rbp);
                    frame.sub(rsp, 0x88);
                    // Stub pushes RDX/R8: +0x68 becomes the audited +0x78.
                    frame.mov(__dword_ptr[rsp + 0x68], ecx);
                    frame.mov(rax, oldSlot);
                    frame.mov(__qword_ptr[rsp + 0x40], rax);
                    frame.movsxd(rdi, edx);
                    frame.mov(ecx, 2);
                    frame.xor(r8d, r8d);
                    frame.mov(r10, image + 0x300);
                    frame.jmp(stub);
                    Write(frame, image);
                    for (int player = 1; player <= 8; player++)
                    {
                        flags.Fill(false);
                        Assert.AreEqual(2, run(player, 9), "Disabled flag preserves category");
                        flags[player] = true;
                        Assert.AreEqual(6, run(player, 9), "Wheat uses true player, independent of old stack slot");
                        Assert.AreEqual(2, run(player, 8), "Other goods preserve category");
                        if (player < 8) Assert.AreEqual(2, run(player + 1, 9), "Other player flag is not inherited");
                    }
                }
                GC.KeepAlive(run);
            }
            finally { if (!VirtualFree(memory, UIntPtr.Zero, 0x8000)) throw new InvalidOperationException("Wheat fixture release failed"); }
        }

        private static void Write(Assembler assembler, ulong address)
        {
            using (var stream = new MemoryStream())
            {
                assembler.Assemble(new StreamCodeWriter(stream), address);
                byte[] bytes = stream.ToArray();
                Marshal.Copy(bytes, 0, new IntPtr(unchecked((long)address)), bytes.Length);
            }
        }
    }
}
