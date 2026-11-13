using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    // The native finder keeps its candidate iteration and reachability decision. This
    // one aperture only adjusts the aggregate returned by its original C05B0 call.
    internal static class NativeStorageAssembly
    {
        internal const string BoundaryError = "Native storage Granary finder boundary differs.";

        internal static byte[] ExpectedStub(ulong image, ulong stub, ulong demandFlag, ulong callback) =>
            Internal.NativeInlineStub.ExpectedStub(
                image + NativeStorageNativeDefinition.GranaryFinderApertureRva,
                NativeStorageNativeDefinition.GranaryFinderApertureBytes,
                NativeStorageNativeDefinition.GranaryFinderApertureLength,
                (assembler, original, back) => Generate(assembler, original, back, image, demandFlag, callback),
                stub, BoundaryError);

        internal static void Generate(Assembler assembler, ReadOnlySpan<Instruction> original,
            ulong back, ulong image, ulong demandFlag, ulong callback)
        {
            Validate(assembler, original, back, image, demandFlag, callback);

            // Reproduce the two original arguments. An unrestricted absolute CALL returns
            // into this stub; replaying the old relative displacement/return address would
            // enter overwritten BFB0F. The pinned callee overwrites RAX before reading it.
            assembler.mov(edx, ebx);
            assembler.mov(rcx, rsi);
            assembler.mov(rax, image + NativeStorageNativeDefinition.GranaryRecomputeRva);
            assembler.call(rax);

            // The dormant path creates no managed-call frame and touches no SIMD state.
            // Preserve the native return and flags while reading the rooted DWORD demand.
            var vanilla = assembler.CreateLabel();
            var finish = assembler.CreateLabel();
            assembler.pushfq(); assembler.push(rax);
            assembler.mov(rax, demandFlag);
            assembler.cmp(__dword_ptr[rax], 0);
            assembler.je(vanilla);
            assembler.pop(rax); assembler.popfq();
            EmitFilter(assembler, callback);
            assembler.jmp(finish);

            assembler.Label(ref vanilla);
            assembler.pop(rax); assembler.popfq();
            assembler.Label(ref finish);
            // This CMP must establish the original flags immediately before its unchanged
            // JGE at back. The callback owns overflow/corrupt-total refusal (Int32.MaxValue).
            assembler.cmp(eax, NativeStorageNativeDefinition.GranaryCapacity);
            assembler.AddUnrestrictedJmp(back);
        }

        private static void Validate(Assembler assembler, ReadOnlySpan<Instruction> original,
            ulong back, ulong image, ulong demandFlag, ulong callback)
        {
            if (assembler == null || image == 0 || demandFlag == 0 || callback == 0 ||
                image > ulong.MaxValue - NativeStorageNativeDefinition.GranaryRecomputeRva ||
                original.Length != 4 ||
                back != image + NativeStorageNativeDefinition.GranaryFinderContinuationRva)
                throw new InvalidOperationException(BoundaryError);

            ulong first = image + NativeStorageNativeDefinition.GranaryFinderApertureRva;
            if (!Move(original[0], first, 2, Code.Mov_r32_rm32, Register.EDX, Register.EBX) ||
                !Move(original[1], first + 2, 3, Code.Mov_r64_rm64, Register.RCX, Register.RSI) ||
                original[2].IP != first + 5 || original[2].Length != 5 ||
                original[2].Code != Code.Call_rel32_64 || original[2].OpCount != 1 ||
                original[2].Op0Kind != OpKind.NearBranch64 ||
                original[2].NearBranchTarget != image + NativeStorageNativeDefinition.GranaryRecomputeRva ||
                original[3].IP != first + 10 || original[3].Length != 5 ||
                original[3].Code != Code.Cmp_EAX_imm32 || original[3].OpCount != 2 ||
                original[3].Op0Kind != OpKind.Register || original[3].Op0Register != Register.EAX ||
                original[3].Op1Kind != OpKind.Immediate32 ||
                original[3].Immediate32 != NativeStorageNativeDefinition.GranaryCapacity ||
                original[3].NextIP != back)
                throw new InvalidOperationException(BoundaryError);
            for (int i = 0; i < original.Length; i++)
                if (original[i].HasLockPrefix || original[i].HasRepPrefix || original[i].HasRepnePrefix ||
                    original[i].SegmentPrefix != Register.None)
                    throw new InvalidOperationException(BoundaryError);
        }

        private static bool Move(Instruction instruction, ulong ip, int length, Code code,
            Register target, Register source) =>
            instruction.IP == ip && instruction.Length == length && instruction.Code == code &&
            instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
            instruction.Op1Kind == OpKind.Register && instruction.Op0Register == target &&
            instruction.Op1Register == source;

        private static void EmitFilter(Assembler a, ulong callback)
        {
            // Preserve the native call's volatile state, except its filtered EAX result.
            // Win64 shadow space is private; align dynamically without losing the old RSP.
            const int savedRax = 48;
            a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx);
            a.push(r8); a.push(r9); a.push(r10); a.push(r11);
            a.mov(r11, rsp); a.and(rsp, -16); a.sub(rsp, 144);
            a.mov(__qword_ptr[rsp + 128], r11);
            a.movdqu(__xmmword_ptr[rsp + 32], xmm0); a.movdqu(__xmmword_ptr[rsp + 48], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 64], xmm2); a.movdqu(__xmmword_ptr[rsp + 80], xmm3);
            a.movdqu(__xmmword_ptr[rsp + 96], xmm4); a.movdqu(__xmmword_ptr[rsp + 112], xmm5);

            // int FilterGranaryAggregate(IntPtr manager, int slot, int nativeAggregate).
            // RSI/RBX are the original finder's nonvolatile manager/candidate registers.
            a.mov(rcx, rsi); a.mov(edx, ebx); a.mov(r8d, eax);
            a.cld(); // Win64 callee contract; the saved native flags are restored below.
            a.AddUnrestrictedCall(callback);
            a.mov(r11, __qword_ptr[rsp + 128]);
            a.mov(eax, eax); // Native Int32 result has zero-extended upper RAX bits.
            a.mov(__qword_ptr[r11 + savedRax], rax);

            a.movdqu(xmm0, __xmmword_ptr[rsp + 32]); a.movdqu(xmm1, __xmmword_ptr[rsp + 48]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 64]); a.movdqu(xmm3, __xmmword_ptr[rsp + 80]);
            a.movdqu(xmm4, __xmmword_ptr[rsp + 96]); a.movdqu(xmm5, __xmmword_ptr[rsp + 112]);
            a.mov(rsp, __qword_ptr[rsp + 128]);
            a.pop(r11); a.pop(r10); a.pop(r9); a.pop(r8);
            a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
        }
    }
}
