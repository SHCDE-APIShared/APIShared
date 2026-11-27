using System;
using APIShared.Internal;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Units
{
    internal static class UnitRelocationNative
    {
        internal const int EntryRva=0x19BA40,Length=16,UnitsRva=0x67E8400,StandingReturnRva=0x183014;
        internal static readonly byte[] Entry={0x48,0x89,0x6C,0x24,0x18,0x56,0x57,0x41,0x56,0x48,0x83,0xEC,0x20,0x49,0x63,0xF0};
        internal static void ValidateStandingCaller(ReadOnlySpan<byte> bytes,ulong image)
        {
            if(bytes.Length!=5)throw new InvalidOperationException("Standing-recovery caller extent differs.");
            var decoder=Decoder.Create(64,new ByteArrayCodeReader(bytes.ToArray()));decoder.IP=image+StandingReturnRva-5;
            decoder.Decode(out var call);
            if(call.Code!=Code.Call_rel32_64||call.Length!=5||call.NextIP!=image+StandingReturnRva||call.NearBranchTarget!=image+EntryRva)
                throw new InvalidOperationException("Standing-recovery caller target differs.");
        }
        // Eight pushes retain entry alignment; 0xA8 adds shadow/argument space and six complete volatile XMM saves.
        // The original fifth argument is entry RSP+40; original caller is entry RSP. Neither comes from a managed wrapper.
        internal static void Emit(Assembler a,ReadOnlySpan<Instruction> original,ulong back,ulong units,ulong interest,ulong callback)
        {
            if(back<Length||original.Length!=6)throw new InvalidOperationException("Relocation original span differs.");
            var expected=NativeInlineStub.Decode(Entry,back-Length,"Relocation instruction boundary differs.");
            for(int i=0;i<original.Length;i++)if(original[i]!=expected[i])throw new InvalidOperationException("Relocation displaced instructions differ.");
            var pass=a.CreateLabel();var allowed=a.CreateLabel();
            a.pushfq();a.push(rax);a.push(rcx);a.push(rdx);a.push(r8);a.push(r9);a.push(r10);a.push(r11);
            a.mov(rax,units);a.cmp(rcx,rax);a.jne(pass);
            a.cmp(edx,1);a.jl(pass);a.cmp(edx,9999);a.jg(pass);
            a.movsxd(r10,edx);a.imul(r10,r10,0x490);a.add(r10,rcx);
            a.movzx(eax,__word_ptr[r10+0x6E6]);a.mov(r11,interest);a.cmp(__byte_ptr[r11+rax],0);a.je(pass);
            a.sub(rsp,0xA8);
            a.movdqu(__xmmword_ptr[rsp+64],xmm0);a.movdqu(__xmmword_ptr[rsp+80],xmm1);a.movdqu(__xmmword_ptr[rsp+96],xmm2);
            a.movdqu(__xmmword_ptr[rsp+112],xmm3);a.movdqu(__xmmword_ptr[rsp+128],xmm4);a.movdqu(__xmmword_ptr[rsp+144],xmm5);
            a.movzx(eax,__word_ptr[rsp+272]);a.mov(__dword_ptr[rsp+32],eax);
            a.mov(rax,__qword_ptr[rsp+232]);a.mov(__qword_ptr[rsp+40],rax);
            a.mov(rax,callback);a.call(rax);a.test(eax,eax);a.je(allowed);
            RestoreXmm(a);a.add(rsp,0xA8);Restore(a);a.ret();
            a.Label(ref allowed);RestoreXmm(a);a.add(rsp,0xA8);
            a.Label(ref pass);Restore(a);
            foreach(var instruction in original.CloneInstructionsWithoutIP())a.AddInstruction(instruction);
            // X64InlineHook appends exactly one continuation jump.
        }
        private static void RestoreXmm(Assembler a)
        {
            a.movdqu(xmm0,__xmmword_ptr[rsp+64]);a.movdqu(xmm1,__xmmword_ptr[rsp+80]);a.movdqu(xmm2,__xmmword_ptr[rsp+96]);
            a.movdqu(xmm3,__xmmword_ptr[rsp+112]);a.movdqu(xmm4,__xmmword_ptr[rsp+128]);a.movdqu(xmm5,__xmmword_ptr[rsp+144]);
        }
        private static void Restore(Assembler a){a.pop(r11);a.pop(r10);a.pop(r9);a.pop(r8);a.pop(rdx);a.pop(rcx);a.pop(rax);a.popfq();}
    }
}
