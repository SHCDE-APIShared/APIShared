using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.X64.Extensions;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Units.AiMilitary.Engineering
{
    // Native intermediary before the reverse-P/Invoke callback. The backend owns
    // its executable page, retained with the process-lifetime publication.
    internal sealed class AiEngineeringNativeGate : IDetourIntermediaryFactory
    {
        private readonly ulong image, demand, trackedBuildings, callback;
        internal AiEngineeringNativeGate(ulong image, ulong demand, ulong trackedBuildings, ulong callback)
        { this.image = image; this.demand = demand; this.trackedBuildings = trackedBuildings; this.callback = callback; }
        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Engineering gate requires the audited Win64 trampoline contract.");
            ulong original = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var entry = ExecutableFunction.Create(a => Generate(a, image, demand, trackedBuildings, original, callback));
            return new DetourIntermediaryStubs(entry.EntryPoint, context.TrampolineAddress, entry);
        }
        internal static void Generate(Assembler a, ulong image, ulong demand, ulong trackedBuildings, ulong original, ulong callback)
        {
            var dormant = a.CreateLabel(); var active = a.CreateLabel();
            a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx); a.push(r8); a.push(r9);
            a.mov(rax, demand); a.cmp(__dword_ptr[rax], 0); a.je(dormant);
            a.mov(rax, image + 0x8F9BE4); a.mov(ecx, __dword_ptr[rax]);
            a.cmp(ecx, 1); a.jl(dormant); a.cmp(ecx, 3999); a.jg(dormant);
            a.imul(rdx, rcx, 0x32C); a.mov(rax, image + 0x64CCBB0); a.add(rdx, rax);
            a.mov(rax, trackedBuildings); a.mov(r8d, __dword_ptr[rax + rcx * 4]);
            a.test(r8d, r8d); var untracked = a.CreateLabel(); a.je(untracked);
            a.cmp(r8d, __dword_ptr[rdx + 0x134]); a.je(active);
            a.Label(ref untracked); a.movsx(r9d, __word_ptr[rdx + 0x1F6]);
            a.cmp(r9d, 1); a.jl(dormant); a.cmp(r9d, 4); a.jg(dormant);
            // A fixed maximum of four slots; never scan unit/building arrays.
            for (int slot = 0; slot < 4; slot++)
            {
                var next = a.CreateLabel(); a.cmp(r9d, slot); a.jle(dormant);
                a.movzx(ecx, __word_ptr[rdx + 0x1FA + slot * 2]); a.cmp(ecx, 1); a.jl(next); a.cmp(ecx, 9999); a.jg(next);
                a.mov(rax, demand + 64); a.mov(r8d, __dword_ptr[rax + rcx * 4]); a.test(r8d, r8d); a.je(next);
                a.cmp(r8d, __dword_ptr[rdx + 0x204 + slot * 4]); a.je(active); a.Label(ref next); a.nop();
            }
            a.Label(ref dormant); Restore(a); a.AddUnrestrictedJmp(original);
            a.Label(ref active); Restore(a); a.AddUnrestrictedJmp(callback);
        }
        private static void Restore(Assembler a) { a.pop(r9); a.pop(r8); a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq(); }
        internal void Validate(IntPtr address, ulong original)
        {
            var assembler = new Assembler(64); Generate(assembler, image, demand, trackedBuildings, original, callback);
            using (var stream = new MemoryStream())
            {
                assembler.Assemble(new StreamCodeWriter(stream), unchecked((ulong)address.ToInt64()));
                NativeSites.NativeIndirectEntry.VerifyBytes(address, stream.ToArray());
            }
        }

        internal static void ValidateEntry<T>(IDetour<T> hook, IntPtr target, byte[] original, bool installed) where T : Delegate
        {
            var native = hook as NativeDetour<T>;
            int length = original.Length;
            if (native == null || native.IsInstalled != installed || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != length || native.TargetAddress != unchecked((ulong)target.ToInt64()) || native.ChainDepth != 1 ||
                native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Engineering Indirect entry backend differs.");
            if (!installed) NativeSites.NativeIndirectEntry.VerifyBytes(target, original);

            // Installed NativeX64.TrampolineBuilder reencodes instructions, not source bytes.
            // In these hash-bound PUSH prologues Iced removes a redundant 0x40 REX prefix.
            // Verify the exact complete backend output, including its original +length resume,
            // rather than treating displaced source length as relocated machine-code length.
            byte[] expected = ExpectedTrampoline(target, native.TrampolineAddress, original);
            if (native.TrampolineSize != expected.Length)
                throw new InvalidOperationException("Engineering trampoline size differs.");
            for (int i = 0; i < expected.Length; i++)
                if (Marshal.ReadByte(native.TrampolineAddress, i) != expected[i])
                    throw new InvalidOperationException("Engineering relocated trampoline differs at byte " + i + ".");
            if (installed)
            {
                if (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                    target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot || Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                    throw new InvalidOperationException("Engineering installed entry differs.");
                for (int i = 6; i < length; i++)
                    if (Marshal.ReadByte(target, i) != 0x90) throw new InvalidOperationException("Engineering padding differs.");
            }
        }

        private static byte[] ExpectedTrampoline(IntPtr target, IntPtr trampoline, byte[] original)
        {
            ulong start = unchecked((ulong)target.ToInt64());
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(original)); decoder.IP = start;
            var instructions = new List<Instruction>();
            while (decoder.IP < start + (uint)original.Length)
            {
                decoder.Decode(out Instruction instruction);
                if (instruction.IsInvalid || instruction.NextIP > start + (uint)original.Length)
                    throw new InvalidOperationException("Engineering original instruction boundary differs.");
                instructions.Add(instruction);
            }
            instructions.Add(Instruction.CreateBranch(Code.Jmp_rel32_64, start + (uint)original.Length));
            using (var stream = new MemoryStream())
            {
                var block = new InstructionBlock(new StreamCodeWriter(stream), instructions, unchecked((ulong)trampoline.ToInt64()));
                if (!BlockEncoder.TryEncode(64, block, out string error, out _, BlockEncoderOptions.ReturnNewInstructionOffsets))
                    throw new InvalidOperationException("Engineering trampoline encoding failed: " + error);
                return stream.ToArray();
            }
        }
    }
}
