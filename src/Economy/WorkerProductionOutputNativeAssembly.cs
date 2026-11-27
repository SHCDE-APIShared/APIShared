using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal static class WorkerProductionOutputNativeAssembly
    {
        internal static void Generate(Assembler a,ReadOnlySpan<Instruction> original,ulong back,
            WorkerProductionOutputSite definition,ulong image,ulong percent,WorkerProductionHotAddresses hot)
        {
            var site=definition.Site;
            ulong units=image+WorkerProductionBonusNativeDefinition.UnitManagerRva;
            ulong current=image+WorkerProductionBonusNativeDefinition.CurrentUnitRva;
            if(definition.Kind==WorkerProductionOutputSiteKind.Zero)
            {
                WorkerProductionBonusNativeAssembly.Generate(a,original,back,site,image,percent,
                    image+WorkerProductionBonusNativeDefinition.ModeRva,units,current,0,hot); return;
            }
            var vanilla=a.CreateLabel(); var custom=a.CreateLabel();
            WorkerProductionHotAssembly.PushAll(a);
            a.mov(rbx,hot.StoreControl); a.cmp(__dword_ptr[rbx+4],1); a.jne(vanilla);
            a.cmp(__dword_ptr[rbx],1); a.jne(vanilla);
            a.mov(rax,current); a.movsxd(rax,__dword_ptr[rax]);
            a.cmp(eax,1); a.jl(vanilla); a.cmp(eax,9999); a.jg(vanilla);
            a.imul(rcx,rax,0x490); a.mov(r13,units); a.add(r13,rcx);
            a.shl(rax,5); a.mov(r12,hot.Rows); a.add(r12,rax);
            a.mov(ebp,(int)eChimps.CHIMP_TYPE_MILLER);
            if(definition.Kind==WorkerProductionOutputSiteKind.MillerPrepare)
            {
                var calculate=a.CreateLabel(); var haveBatch=a.CreateLabel();
                a.mov(eax,__dword_ptr[r13+WorkerProductionHotAssembly.GlobalIdOffset]);
                a.cmp(__dword_ptr[r12],eax); a.jne(calculate);
                a.test(__word_ptr[r12+6],4); a.jne(haveBatch);
                a.Label(ref calculate);
                a.mov(rbx,hot.ConfigurationSlot); a.mov(rbx,__qword_ptr[rbx]); a.test(rbx,rbx); a.je(vanilla);
                a.movsx(ecx,__word_ptr[r13+WorkerProductionHotAssembly.OwnerOffset]);
                a.cmp(ecx,1); a.jl(vanilla); a.cmp(ecx,8); a.jg(vanilla);
                a.mov(edi,18); // Miller worker index 9, times the two owner roles.
                a.mov(rsi,hot.AiLineup); a.cmp(__dword_ptr[rsi+rcx*4],0);
                var human=a.CreateLabel(); a.je(human); a.inc(edi); a.Label(ref human);
                var needsTransform=a.CreateLabel(); a.bt(__qword_ptr[rbx+WorkerProductionHotAssembly.ActiveRoleMaskOffset],rdi); a.jc(needsTransform);
                a.mov(eax,__dword_ptr[r13+WorkerProductionHotAssembly.GlobalIdOffset]); a.cmp(__dword_ptr[r12],eax); a.jne(vanilla);
                a.Label(ref needsTransform);
                a.imul(edi,edi,WorkerProductionHotAssembly.EntriesPerRole); a.inc(edi); // actual flour baseline = 1
                a.imul(rdi,rdi,WorkerProductionHotAssembly.EntrySize); a.add(rdi,__qword_ptr[rbx+WorkerProductionHotAssembly.TableOffset]);
                var invalid=a.CreateLabel(); var transformed=a.CreateLabel();
                WorkerProductionHotAssembly.Quantize(a,invalid);
                a.cmp(r10,32767); a.ja(invalid); a.mov(__word_ptr[r13+0x9E0],r10w);
                a.mov(__word_ptr[r12+6],4); a.test(r10,r10); a.jne(transformed); a.or(__word_ptr[r12+6],1);
                a.Label(ref transformed); a.jmp(haveBatch);
                a.Label(ref invalid);
                a.mov(rbx,hot.Fault); a.cmp(__dword_ptr[rbx],0); var latched=a.CreateLabel(); a.jne(latched);
                a.mov(__dword_ptr[rbx],3); a.Label(ref latched);
                // A terminal error never reaches a potentially overflowing native assignment.
                a.mov(__word_ptr[r13+0x9E0],0);
                a.Label(ref haveBatch);
                a.cmp(__word_ptr[r13+0x9E0],0); a.jne(vanilla);
                WorkerProductionHotAssembly.PopAll(a);
                MillerFooter(a,image,units,current);
                a.AddUnrestrictedJmp(image+0x1383AF); returnAfterCustom(a,ref vanilla,original,back); return;
            }
            a.mov(eax,__dword_ptr[r13+WorkerProductionHotAssembly.GlobalIdOffset]);
            a.cmp(__dword_ptr[r12],eax); a.jne(vanilla); a.test(__word_ptr[r12+6],4); a.je(vanilla);
            if(definition.Kind==WorkerProductionOutputSiteKind.MillerFinish)
            {
                a.cmp(__word_ptr[r13+0x9E0],0); a.jg(vanilla);
                a.mov(__word_ptr[r12+6],0); WorkerProductionHotAssembly.CanonicalizeEmpty(a);
                a.jmp(vanilla);
                returnAfterCustom(a,ref vanilla,original,back); return;
            }
            a.jmp(custom);
            a.Label(ref custom); WorkerProductionHotAssembly.PopAll(a);
            if(definition.Kind==WorkerProductionOutputSiteKind.MillerPickup)
            {
                // Carry was computed exactly once before storage search; replay only the byte store.
                WorkerProductionBonusNativeAssembly.Replay(a,original.Slice(1)); a.AddUnrestrictedJmp(back);
            }
            else if(definition.Kind==WorkerProductionOutputSiteKind.MillerDeposit)
            {
                a.dec(__word_ptr[rcx+r15+0x9E0]);
                WorkerProductionBonusNativeAssembly.Replay(a,original.Slice(1)); a.AddUnrestrictedJmp(back);
            }
            else throw new InvalidOperationException("Unknown output site.");
            returnAfterCustom(a,ref vanilla,original,back);
        }

        private static void returnAfterCustom(Assembler a,ref Label vanilla,ReadOnlySpan<Instruction> original,ulong back)
        { a.Label(ref vanilla); WorkerProductionHotAssembly.PopAll(a); WorkerProductionBonusNativeAssembly.Replay(a,original); a.AddUnrestrictedJmp(back); }

        private static void MillerFooter(Assembler a,ulong image,ulong units,ulong current)
        {
            // Full normal completion entry requires these live registers, notably BP=20.
            a.mov(rax,current); a.movsxd(rdi,__dword_ptr[rax]); a.mov(r15,units);
            a.mov(r12,image+WorkerProductionBonusNativeDefinition.BuildingManagerRva);
            a.xor(esi,esi); a.mov(r13d,1); a.mov(ebp,20);
        }
    }
}
