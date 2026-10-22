using System;
using System.IO;
using Iced.Intel;
using RedBird.X64.Extensions;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal static class AiMarketSellAssembly
    {
        internal const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int PlayerStride = 0x583C, GoodsBase = 0x379B2A0, GoldBase = 0x379B2DC;
        internal const int SaleEpilog = 0x2978B;
        internal static readonly int[] Sites = { 0x3F1F3, 0x29723 };
        internal static readonly string[] OriginalHex = { "488D0516D06203448B8C2890F01200", "8BD7488D2DE42A6403418BF0488BCD" };

        // state: atomically published OR of enabled safety bits (1/2) and callback demands (4/8).
        internal static void Generate(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            int index, ulong image, ulong state, ulong reserveCallback = 0, ulong quantityCallback = 0)
        {
            if (index < 0 || index > 1 || back != image + (uint)Sites[index] + 15)
                throw new InvalidOperationException("Unexpected sell hook continuation.");
            var vanilla = a.CreateLabel();
            a.pushfq(); a.push(rax); a.mov(rax, state); a.test(__dword_ptr[rax], index == 0 ? 5 : 10);
            a.je(vanilla); a.pop(rax); a.popfq();
            if (index == 0)
            {
                // Replay produces the actual total stock in R9D. R8 is player*0x160F,
                // RDI the goods index, EDX the effective reserve; CMP at back consumes it.
                Replay(a, original);
                var noCallback = a.CreateLabel(); var ready = a.CreateLabel();
                a.pushfq(); a.push(rax); a.mov(rax, state); a.test(__dword_ptr[rax], 4); a.je(noCallback);
                a.pop(rax); a.popfq(); Bridge(a, reserveCallback, true);
                a.pushfq(); a.push(rax); a.mov(rax, state);
                a.Label(ref noCallback); a.test(__dword_ptr[rax], 1); a.je(ready);
                a.test(edx, edx); a.jns(ready); a.mov(edx, 0);
                a.Label(ref ready); a.pop(rax); a.popfq(); a.AddUnrestrictedJmp(back);
            }
            else
            {
                var empty = a.CreateLabel(); var unconstrained = a.CreateLabel();
                var noCallback = a.CreateLabel();
                a.pushfq(); a.push(rax); a.push(rcx);
                a.cmp(edi, 1); a.jl(empty); a.cmp(edi, 8); a.jg(empty);
                a.cmp(r8d, (int)eGoods.STORED_NULL); a.jle(empty);
                a.cmp(r8d, (int)eGoods.Count); a.jae(empty);
                a.cmp(r8d, (int)eGoods.STORED_GOLD); a.je(empty);
                a.imul(rax, rdi, PlayerStride); a.mov(ecx, r8d); a.lea(rax, __[rax + rcx * 4]);
                a.mov(rcx, image + GoodsBase); a.mov(eax, __dword_ptr[rcx + rax]);
                a.mov(rcx, state); a.test(__dword_ptr[rcx], 8); a.je(noCallback);
                Bridge(a, quantityCallback, false);
                // A recursive sale inside an adjuster can consume stock. Enforce the final
                // constraint against the current total, not the earlier context snapshot.
                a.imul(rax, rdi, PlayerStride); a.mov(ecx, r8d); a.lea(rax, __[rax + rcx * 4]);
                a.mov(rcx, image + GoodsBase); a.mov(eax, __dword_ptr[rcx + rax]);
                a.Label(ref noCallback); a.mov(rcx, state); a.test(__dword_ptr[rcx], 2); a.je(unconstrained);
                a.test(eax, eax); a.jle(empty);
                a.cmp(ebx, eax); a.jle(unconstrained); a.mov(ebx, eax);
                a.Label(ref unconstrained); a.test(ebx, ebx); a.jle(empty); a.mov(r9d, ebx);
                a.pop(rcx); a.pop(rax); a.popfq(); Replay(a, original); a.AddUnrestrictedJmp(back);
                a.Label(ref empty); a.pop(rcx); a.pop(rax); a.popfq(); a.AddUnrestrictedJmp(image + SaleEpilog);
            }
            a.Label(ref vanilla); a.pop(rax); a.popfq(); Replay(a, original); a.AddUnrestrictedJmp(back);
        }

        // Windows x64 reverse-P/Invoke: save actual entry flags before arithmetic, every
        // volatile integer register and XMM0..5. Dynamic alignment supports both sites and
        // the private harness. Shadow space belongs to the callee; saved RSP is outside it.
        // Nonvolatile registers and XMM6..15 are preserved by the ABI. Only EDX (reserve)
        // or EBX (amount) is intentionally changed. No RedBird context flags are consulted.
        private static void Bridge(Assembler a, ulong callback, bool reserve)
        {
            if (callback == 0) throw new InvalidOperationException("Sell callback bridge requires a rooted delegate.");
            a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx); a.push(r8); a.push(r9); a.push(r10); a.push(r11);
            a.mov(r11, rsp); a.and(rsp, -16); a.sub(rsp, 144); a.mov(__qword_ptr[rsp + 128], r11);
            a.movdqu(__xmmword_ptr[rsp + 32], xmm0); a.movdqu(__xmmword_ptr[rsp + 48], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 64], xmm2); a.movdqu(__xmmword_ptr[rsp + 80], xmm3);
            a.movdqu(__xmmword_ptr[rsp + 96], xmm4); a.movdqu(__xmmword_ptr[rsp + 112], xmm5);
            if (reserve)
            {
                a.mov(ecx, __dword_ptr[r11 + 24]); a.mov(edx, edi);
                a.mov(r8d, __dword_ptr[r11 + 16]); a.mov(r9d, __dword_ptr[r11 + 32]);
            }
            else
            {
                a.mov(ecx, edi); a.mov(edx, __dword_ptr[r11 + 24]);
                a.mov(r8d, __dword_ptr[r11 + 48]); a.mov(r9d, ebx);
            }
            a.mov(rax, callback); a.call(rax); a.mov(r11, __qword_ptr[rsp + 128]);
            if (reserve) { a.mov(eax, eax); a.mov(__qword_ptr[r11 + 32], rax); } else a.mov(ebx, eax);
            a.movdqu(xmm0, __xmmword_ptr[rsp + 32]); a.movdqu(xmm1, __xmmword_ptr[rsp + 48]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 64]); a.movdqu(xmm3, __xmmword_ptr[rsp + 80]);
            a.movdqu(xmm4, __xmmword_ptr[rsp + 96]); a.movdqu(xmm5, __xmmword_ptr[rsp + 112]);
            a.mov(rsp, r11); a.pop(r11); a.pop(r10); a.pop(r9); a.pop(r8); a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
        }
        private static void Replay(Assembler a, ReadOnlySpan<Instruction> instructions)
        { foreach (var decoded in instructions) { var instruction = decoded; instruction.IP = 0; a.AddInstruction(instruction); } }
        internal static Instruction[] Decode(byte[] bytes, ulong address)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
            var result = new System.Collections.Generic.List<Instruction>();
            while (decoder.IP < address + (uint)bytes.Length)
            { var instruction = decoder.Decode(); if (instruction.IsInvalid || instruction.NextIP > address + (uint)bytes.Length)
                    throw new InvalidOperationException("Invalid hook boundary."); result.Add(instruction); }
            return result.ToArray();
        }
        internal static byte[] Assemble(Assembler assembler, ulong address)
        { using (var stream = new MemoryStream()) { assembler.Assemble(new StreamCodeWriter(stream), address); return stream.ToArray(); } }
        internal static byte[] Hex(string text)
        { var bytes = new byte[text.Length / 2]; for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16); return bytes; }
    }
}
