using System;
using APIShared.Internal;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal static class AllyTransferChatAssembly
    {
        internal const int Rva = 0x1C8D0, Length = 14;
        internal const int RecipientReturnRva = 0xD7C80, SenderReturnRva = 0xD7CB7;
        internal static readonly byte[] Body = Convert.FromBase64String(
            "SGOBAFonAEyL2UyNFIBmQomUUQhaJwBIY4EAWicASI0UgGZEiYRRClonAEhjgQBaJwBIjRSAZkSJjFEMWicASGOBAFonAEiNFIAPt0QkKGaJhFEOWicASGOBAFonAEgFaO8DAEiNDIAPt0QkMGZBiQRLM8lB/4MAWicAQYuDAFonAIP4Cg9NwUGJgwBaJwDD");

        internal static bool IsGiftReturn(ulong address, int category, ulong image) =>
            address == image + RecipientReturnRva && category == 10 ||
            address == image + SenderReturnRva && category == 15;

        internal static void ValidatePristine(ReadOnlySpan<byte> memory)
        {
            if (!NativeInlineStub.SnapshotMatches(memory, Rva, Body))
                throw new InvalidOperationException("The native gift chat writer differs.");
            foreach (int returnRva in new[] { RecipientReturnRva, SenderReturnRva })
            {
                int call = returnRva - 5;
                if (call < 0 || returnRva > memory.Length || memory[call] != 0xE8 ||
                    returnRva + BitConverter.ToInt32(memory.Slice(call + 1, 4).ToArray(), 0) != Rva)
                    throw new InvalidOperationException("The native gift chat producer call differs.");
            }
        }

        // Entry is a leaf. First 14 bytes are exactly MOVSXD RAX,[RCX+275A00],
        // MOV R11,RCX, LEA R10,[RAX+RAX*4]. No direct edge enters this span.
        // Before any original instruction, [RSP] is the physical native return address.
        internal static void Generate(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            ulong demand, ulong callback)
        {
            var vanilla = a.CreateLabel(); var forward = a.CreateLabel();
            a.pushfq(); a.push(rax); a.mov(rax, demand); a.cmp(__dword_ptr[rax], 0); a.je(vanilla);
            a.pop(rax); a.popfq();
            a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx); a.push(r8); a.push(r9); a.push(r10); a.push(r11);
            a.mov(r11, rsp); a.and(rsp, -16); a.sub(rsp, 144); a.mov(__qword_ptr[rsp + 128], r11);
            a.movdqu(__xmmword_ptr[rsp + 32], xmm0); a.movdqu(__xmmword_ptr[rsp + 48], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 64], xmm2); a.movdqu(__xmmword_ptr[rsp + 80], xmm3);
            a.movdqu(__xmmword_ptr[rsp + 96], xmm4); a.movdqu(__xmmword_ptr[rsp + 112], xmm5);
            a.mov(rcx, __qword_ptr[r11 + 64]); a.movzx(edx, __word_ptr[r11 + 32]);
            a.mov(rax, callback); a.call(rax); a.test(eax, eax); a.je(forward);
            Restore(a); a.ret();
            a.Label(ref forward); Restore(a); Replay(a, original); a.AddUnrestrictedJmp(back);
            a.Label(ref vanilla); a.pop(rax); a.popfq(); Replay(a, original); a.AddUnrestrictedJmp(back);
        }
        private static void Restore(Assembler a)
        {
            a.mov(r11, __qword_ptr[rsp + 128]);
            a.movdqu(xmm0, __xmmword_ptr[rsp + 32]); a.movdqu(xmm1, __xmmword_ptr[rsp + 48]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 64]); a.movdqu(xmm3, __xmmword_ptr[rsp + 80]);
            a.movdqu(xmm4, __xmmword_ptr[rsp + 96]); a.movdqu(xmm5, __xmmword_ptr[rsp + 112]);
            a.mov(rsp, r11); a.pop(r11); a.pop(r10); a.pop(r9); a.pop(r8); a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
        }
        private static void Replay(Assembler a, ReadOnlySpan<Instruction> original)
        { foreach (var item in original) { var instruction = item; instruction.IP = 0; a.AddInstruction(instruction); } }
    }
}
