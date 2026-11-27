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
    // Executes corrected foreign emitter bytes in private memory; no game hook/state is modified.
    internal static class FixesWheatExecutionFixture
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Execute(int player, int good);

        internal static void Run(byte[] emitted, NativeFlagArray flags)
        {
            IntPtr memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);
            if (memory == IntPtr.Zero) throw new InvalidOperationException("Wheat fixture allocation failed");
            try
            {
                ulong image = unchecked((ulong)memory.ToInt64()), stub = image + 0x400, exit = image + 0x800;
                byte[] complete = FixesWheatTestStub.Complete(emitted, exit);
                Assert.IsTrue(complete.Length < 0x400, "Fixture ranges must be disjoint");
                Marshal.Copy(complete, 0, new IntPtr(unchecked((long)stub)), complete.Length);
                Func<byte[], FixesWheatAnalysis.WheatStub> analyze = bytes => FixesWheatAnalysis.AnalyzeStub(bytes,
                    stub, flags.Address, FixesWheatTestStub.Original, exit, 0x68, flags.Length);
                bool initiallyCorrect = analyze(complete).IsCorrect;
                int writes = 0;
                Action<int, byte[], byte[]> write = (offset, expected, replacement) =>
                { FixesWheatMemory.WriteRange(stub + (uint)offset, expected, replacement); writes++; };
                Func<byte[]> read = () => FixesWheatMemory.Read(stub, complete.Length);
                Assert.AreEqual(!initiallyCorrect, FixesWheatAnalysis.Correct(read, write, analyze));
                Assert.IsFalse(FixesWheatAnalysis.Correct(read, write, analyze));
                Assert.AreEqual(initiallyCorrect ? 0 : 1, writes);

                // The displaced instructions intentionally write RAX/RBP/R11. Check their results,
                // the other live registers and the stack; preserve nonvolatile fixture registers.
                var end = new Assembler(64);
                end.mov(__qword_ptr[rbx], rsp);
                end.mov(__qword_ptr[rbx + 8], rdx); end.mov(__qword_ptr[rbx + 16], r8);
                end.mov(__qword_ptr[rbx + 24], r9); end.mov(__qword_ptr[rbx + 32], r10);
                end.mov(__qword_ptr[rbx + 40], r11); end.mov(__qword_ptr[rbx + 48], rax);
                end.mov(__qword_ptr[rbx + 56], rbp); end.mov(__qword_ptr[rbx + 64], rdi);
                end.mov(__qword_ptr[rbx + 72], rsi); end.mov(__qword_ptr[rbx + 80], r12);
                end.mov(__qword_ptr[rbx + 88], r13); end.mov(__qword_ptr[rbx + 96], r14);
                end.mov(__qword_ptr[rbx + 104], r15); end.movq(__qword_ptr[rbx + 112], xmm0);
                end.mov(eax, ecx); end.add(rsp, 0xA8);
                end.pop(r15); end.pop(r14); end.pop(r13); end.pop(r12);
                end.pop(rsi); end.pop(rbx); end.pop(rbp); end.pop(rdi); end.ret();
                Write(end, exit);
                Marshal.WriteInt32(new IntPtr(unchecked((long)(image + 0xEC4))), 0x76543210);
                var run = (Execute)Marshal.GetDelegateForFunctionPointer(memory, typeof(Execute));
                foreach (ulong oldSlot in new[] { 0UL, 0x7FFC3E96C950UL, ulong.MaxValue })
                foreach (ulong previousStock in new[] { 0UL, 8UL, 9UL, 100UL, 22588UL, ulong.MaxValue })
                {
                    var frame = new Assembler(64);
                    frame.push(rdi); frame.push(rbp); frame.push(rbx); frame.push(rsi);
                    frame.push(r12); frame.push(r13); frame.push(r14); frame.push(r15); frame.sub(rsp, 0xA8);
                    frame.mov(__dword_ptr[rsp + 0x68], ecx);
                    frame.mov(rax, oldSlot); frame.mov(__qword_ptr[rsp + 0x40], rax);
                    frame.movsxd(rdi, edx); frame.mov(ecx, 2);
                    frame.mov(rdx, 0x1122334455667788UL); frame.mov(r8, 0x12345);
                    frame.mov(r9, previousStock); frame.mov(r10, image + 0xE00);
                    frame.mov(rbx, image + 0xC00); frame.mov(__qword_ptr[rbx + 120], rsp);
                    frame.mov(rsi, 0x1122); frame.mov(r12, 0x3344); frame.mov(r13, 0x5566);
                    frame.mov(r14, 0x7788); frame.mov(r15, 0x99AA);
                    frame.mov(rax, 0x1020304050607080UL); frame.movq(xmm0, rax); frame.jmp(stub);
                    Write(frame, image);
                    for (int player = 1; player <= 8; player++)
                    {
                        flags.Fill(false);
                        Assert.AreEqual(2, run(player, 9), "Disabled player flag");
                        flags[player] = true;
                        Assert.AreEqual(6, run(player, 9), "Enabled wheat uses true player, independent of R9/old slot");
                        CheckRegisters(image, previousStock, 9);
                        Assert.AreEqual(2, run(player, 8), "Other goods preserve category");
                        CheckRegisters(image, previousStock, 8);
                        if (player < 8) Assert.AreEqual(2, run(player + 1, 9), "Other player flag is not inherited");
                    }
                    if (analyze(read()).Variant == "bounded-stack-load")
                    {
                        flags.Fill(true);
                        foreach (int invalid in new[] { -1, flags.Length, int.MaxValue, int.MinValue })
                            Assert.AreEqual(2, run(invalid, 9), "Out-of-array player preserves original category");
                    }
                }
                GC.KeepAlive(run);
            }
            finally { if (!VirtualFree(memory, UIntPtr.Zero, 0x8000)) throw new InvalidOperationException("Wheat fixture release failed"); }
        }

        private static void CheckRegisters(ulong image, ulong stock, int good)
        {
            ulong[] expected = { Read(image, 120), 0x1122334455667788UL, 0x12345UL, stock,
                image + 0xE00, 0x76543210UL, 0x12345UL + (uint)good, (0x12345UL + (uint)good) * 4,
                (uint)good, 0x1122, 0x3344, 0x5566, 0x7788, 0x99AA, 0x1020304050607080UL };
            for (int index = 0; index < expected.Length; index++)
                Assert.AreEqual(expected[index], Read(image, index * 8), "Stack/register sentinel " + index);
        }

        private static ulong Read(ulong image, int offset) =>
            unchecked((ulong)Marshal.ReadInt64(new IntPtr(unchecked((long)(image + 0xC00 + (uint)offset)))));

        private static void Write(Assembler assembler, ulong address)
        {
            using (var stream = new MemoryStream())
            {
                assembler.Assemble(new StreamCodeWriter(stream), address);
                byte[] bytes = stream.ToArray();
                Marshal.Copy(bytes, 0, new IntPtr(unchecked((long)address)), bytes.Length);
                Assert.IsTrue(FlushInstructionCache(GetCurrentProcess(), new IntPtr(unchecked((long)address)), (UIntPtr)bytes.Length));
            }
        }
    }
}
