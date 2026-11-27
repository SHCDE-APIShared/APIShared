using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

internal static partial class Program
{
    // Native register order follows the x64 encoding: RAX, RCX, RDX, RBX, RSP,
    // RBP, RSI, RDI, R8..R15. The wrapper never replaces its real stack pointer.
    private static readonly AssemblerRegister64[] Gp = { rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8, r9, r10, r11, r12, r13, r14, r15 };
    private static readonly AssemblerRegisterXMM[] Simd = { xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7, xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15 };

    private sealed class MachineHarness : IDisposable
    {
        internal readonly IntPtr Input = Marshal.AllocHGlobal(512);
        internal readonly IntPtr Capture = Marshal.AllocHGlobal(512);
        internal readonly Code Entry;
        private readonly List<(int Rva, byte[] Bytes)> restorations = new List<(int, byte[])>();
        // Full original footers use their own stack locals (for example Brewer saves
        // R14 at RSP+0x80). Keep fixture preservation outside that native scratch area.
        private const int SavedSimdOffset = 1024;
        private const int FrameSize = SavedSimdOffset + 160 + 8; // shadow/local area, XMM6..15, alignment.
        private const int FlagsOffset = 400, MarkerOffset = 408, InitialStackOffset = 416;

        internal MachineHarness(Action<Assembler> emit, IReadOnlyDictionary<int, int> exits)
        {
            ResetInput();
            foreach (var exit in exits)
            {
                int marker = exit.Value;
                using var done = new Code(a => {
                    a.pushfq();
                    a.push(rax);
                    a.mov(rax, unchecked((ulong)Capture.ToInt64()));
                    for (int index = 1; index < Gp.Length; index++)
                        if (index != 4) a.mov(__qword_ptr[rax + index * 8], Gp[index]);
                    a.lea(rdx, __[rsp + 16]);
                    a.mov(__qword_ptr[rax + 4 * 8], rdx);
                    a.mov(rdx, __qword_ptr[rsp]);
                    a.mov(__qword_ptr[rax], rdx);
                    a.mov(rdx, __qword_ptr[rsp + 8]);
                    a.mov(__qword_ptr[rax + FlagsOffset], rdx);
                    a.mov(__dword_ptr[rax + MarkerOffset], marker);
                    for (int index = 0; index < Simd.Length; index++)
                        a.movdqu(__xmmword_ptr[rax + 128 + index * 16], Simd[index]);
                    a.pop(rax);
                    a.popfq();
                    for (int index = 6; index < Simd.Length; index++)
                        a.movdqu(Simd[index], __xmmword_ptr[rsp + SavedSimdOffset + (index - 6) * 16]);
                    a.add(rsp, FrameSize);
                    a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12);
                    a.pop(rdi); a.pop(rsi); a.pop(rbp); a.pop(rbx);
                    a.ret();
                });
                restorations.Add((exit.Key, Read(exit.Key, 16)));
                var jump = new Assembler(64);
                jump.AddUnrestrictedJmp(unchecked((ulong)done.Address.ToInt64()));
                using var stream = new System.IO.MemoryStream();
                jump.Assemble(new StreamCodeWriter(stream), Address(exit.Key));
                Copy(exit.Key, stream.ToArray());
            }
            using var generated = new Code(emit);
            Entry = new Code(a => {
                a.push(rbx); a.push(rbp); a.push(rsi); a.push(rdi);
                a.push(r12); a.push(r13); a.push(r14); a.push(r15);
                a.sub(rsp, FrameSize);
                for (int index = 6; index < Simd.Length; index++)
                    a.movdqu(__xmmword_ptr[rsp + SavedSimdOffset + (index - 6) * 16], Simd[index]);
                a.mov(rax, unchecked((ulong)Capture.ToInt64()));
                a.mov(__qword_ptr[rax + InitialStackOffset], rsp);
                a.mov(rax, unchecked((ulong)Input.ToInt64()));
                for (int index = 0; index < Simd.Length; index++)
                    a.movdqu(Simd[index], __xmmword_ptr[rax + 128 + index * 16]);
                for (int index = 1; index < Gp.Length; index++)
                    if (index != 4) a.mov(Gp[index], __qword_ptr[rax + index * 8]);
                a.push(__qword_ptr[rax + FlagsOffset]);
                a.popfq();
                a.mov(rax, __qword_ptr[rax]);
                a.AddUnrestrictedJmp(unchecked((ulong)generated.Address.ToInt64()));
            });
        }

        internal void ResetInput()
        {
            for (int index = 0; index < 512; index += 8)
            { Marshal.WriteInt64(Input, index, 0); Marshal.WriteInt64(Capture, index, 0); }
            for (int index = 0; index < Gp.Length; index++) Set(index, 0x11110000UL + (uint)index * 0x100UL);
            for (int index = 0; index < Simd.Length; index++)
            {
                ulong value = 0x123456789ABC0000UL + (uint)index;
                Marshal.WriteInt64(Input, 128 + index * 16, unchecked((long)value));
                Marshal.WriteInt64(Input, 136 + index * 16, unchecked((long)~value));
            }
            Flags = 0x246;
        }
        internal void Set(int register, ulong value) => Marshal.WriteInt64(Input, register * 8, unchecked((long)value));
        internal ulong Get(int register) => unchecked((ulong)Marshal.ReadInt64(Capture, register * 8));
        internal ulong InputRegister(int register) => unchecked((ulong)Marshal.ReadInt64(Input, register * 8));
        internal ulong Flags { get => unchecked((ulong)Marshal.ReadInt64(Capture, FlagsOffset)); set => Marshal.WriteInt64(Input, FlagsOffset, unchecked((long)value)); }
        internal int Marker => Marshal.ReadInt32(Capture, MarkerOffset);
        internal void Run() => Marshal.GetDelegateForFunctionPointer<Invoke>(Entry.Address)();
        internal void CheckPreserved(params int[] changedRegisters)
        {
            var changed = new HashSet<int>(changedRegisters);
            for (int index = 0; index < Gp.Length; index++)
            {
                if (index == 4)
                    Check(Get(index) == unchecked((ulong)Marshal.ReadInt64(Capture, InitialStackOffset)), "Generator stack balanced at its continuation.");
                else if (!changed.Contains(index))
                    Check(Get(index) == InputRegister(index), "Live GP register preserved: " + Gp[index]);
            }
            for (int index = 0; index < Simd.Length; index++)
            for (int half = 0; half < 2; half++)
                Check(Marshal.ReadInt64(Capture, 128 + index * 16 + half * 8) == Marshal.ReadInt64(Input, 128 + index * 16 + half * 8), "SIMD sentinel preserved.");
        }
        public void Dispose()
        {
            // Every rewritten boundary belongs solely to this never-published private image.
            foreach (var entry in restorations) Copy(entry.Rva, entry.Bytes);
            Marshal.FreeHGlobal(Input);
            Marshal.FreeHGlobal(Capture);
        }
    }
    private static ulong Address(int rva) => unchecked((ulong)(image + rva).ToInt64());
}
