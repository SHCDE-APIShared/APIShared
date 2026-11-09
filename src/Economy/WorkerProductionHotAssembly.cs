using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal readonly struct WorkerProductionHotAddresses
    {
        internal WorkerProductionHotAddresses(ulong configurationSlot, ulong rows, ulong storeControl, ulong fault, ulong aiLineup,ulong originalUnit=0)
        { ConfigurationSlot = configurationSlot; Rows = rows; StoreControl = storeControl; Fault = fault; AiLineup = aiLineup;OriginalUnit=originalUnit; }
        internal ulong ConfigurationSlot { get; }
        internal ulong Rows { get; }
        internal ulong StoreControl { get; }
        internal ulong Fault { get; }
        internal ulong AiLineup { get; }
        internal ulong OriginalUnit { get; }
        internal bool IsDefined => ConfigurationSlot != 0;
    }

    // This generator has no managed runtime callbacks. Tables are immutable and precompiled
    // for the audited input domain. Per-unit credits remain individually canonical fractions.
    internal static class WorkerProductionHotAssembly
    {
        internal const int GlobalIdOffset = 0x6F0, KindOffset = 0x6E6, OwnerOffset = 0x6EE,
            AiStateOffset = 0x918, RowSize = 32, EntrySize = 24, EntriesPerRole = 101,
            HeaderSize = 40, CommonDenominatorOffset = 0, TableOffset = 8, ActiveRoleMaskOffset = 16,
            FearTableOffset = 24, FearHasNegativeOffset = 32;
        // PushAll's stack map; never infer these offsets from a caller's stack frame.
        private const int SavedR10 = 40, SavedR9 = 48, SavedRax = 112;

        internal static void Core(Assembler a, ulong image, ulong back, ulong percent, ulong mode,
            ulong units, WorkerProductionHotAddresses addresses)
        {
            var guarded = a.CreateLabel();
            a.push(rcx); a.mov(rcx,addresses.Fault); a.cmp(__dword_ptr[rcx + 24],0); a.pop(rcx); a.jne(guarded);
            FixedBonus(a,back,percent,mode);
            a.Label(ref guarded);
            var ordinary = a.CreateLabel(); var noBase = a.CreateLabel();
            var faultInput = a.CreateLabel(); var faultProductivity = a.CreateLabel(); var faultState = a.CreateLabel();
            PushAll(a);
            a.mov(rbx, addresses.Fault);
            a.cmp(__dword_ptr[rbx + 24], 0); a.je(noBase);
            a.cmp(__dword_ptr[rbx], 0); a.jne(faultState);
            a.cmp(r10d, 100); a.ja(faultInput); // unsigned rejects negative Pre values too
            a.cmp(eax, 150); a.jg(faultProductivity); // already floored by original 18D973
            a.mov(r14, units); a.add(r14, r9); a.mov(r13, r14);
            // Native remainder/productivity still belong to the actual modified UnitId.
            a.movsx(ecx, __word_ptr[r13 + 0x95C]); a.cmp(ecx, -99); a.jl(faultState);
            a.cmp(ecx, 99); a.jg(faultState);
            if(addresses.OriginalUnit!=0)
            {
                var useNativeActor=a.CreateLabel();a.mov(rcx,addresses.OriginalUnit);a.movsxd(rcx,__dword_ptr[rcx]);
                a.cmp(ecx,1);a.jl(useNativeActor);a.cmp(ecx,9999);a.jg(useNativeActor);
                a.imul(rcx,rcx,0x490);a.mov(r13,units);a.add(r13,rcx);
                a.Label(ref useNativeActor);
            }
            a.mov(rbx, addresses.ConfigurationSlot); a.mov(rbx, __qword_ptr[rbx]);
            a.test(rbx, rbx); a.je(noBase);
            a.movzx(ebp, __word_ptr[r13 + KindOffset]);
            // The native helper's Miller call is wheat pickup; its actual flour output
            // is intercepted later, after manufacturing, with the Vanilla baseline one.
            a.cmp(ebp, (int)eChimps.CHIMP_TYPE_MILLER); a.je(noBase);
            a.cmp(ebp, (int)eChimps.CHIMP_TYPE_BAKER); var notBaker = a.CreateLabel(); a.jne(notBaker);
            a.cmp(__dword_ptr[r13 + AiStateOffset], 0x5006D); a.jne(noBase);
            a.Label(ref notBaker);
            var selected = a.CreateLabel();
            EmitWorkerIndex(a, selected, noBase);
            a.Label(ref selected);
            ApplyFear(a, image, percent, mode, units, addresses, faultState);
            a.mov(rax, addresses.StoreControl);
            a.cmp(__dword_ptr[rax + 4], 1); a.jne(noBase);
            a.cmp(__dword_ptr[rax], 1); a.jne(noBase);
            a.cmp(__qword_ptr[rbx + TableOffset], 0); a.je(noBase);
            // EDI now holds the economic worker index; QuarryOx selects QuarryGrunt.
            a.movsx(ecx, __word_ptr[r13 + OwnerOffset]); a.cmp(ecx, 1); a.jl(noBase);
            a.cmp(ecx, 8); a.jg(noBase);
            a.shl(edi, 1); a.mov(rsi, addresses.AiLineup);
            a.cmp(__dword_ptr[rsi + rcx * 4], 0); var human = a.CreateLabel(); a.je(human); a.inc(edi);
            a.Label(ref human);
            // Base credits belong to the original producing actor. Extender UnitId changes
            // retain their native fear/bonus-remainder semantics without moving this cargo.
            a.mov(rax,r13);a.mov(rcx,units);a.sub(rax,rcx); a.xor(edx, edx); a.mov(ecx, 0x490); a.div(rcx);
            a.cmp(eax, 1); a.jl(faultState); a.cmp(eax, 9999); a.jg(faultState);
            a.shl(rax, 5); a.mov(r12, addresses.Rows); a.add(r12, rax);
            a.bt(__qword_ptr[rbx + ActiveRoleMaskOffset], rdi);
            var needQuantizer = a.CreateLabel(); a.jc(needQuantizer);
            a.mov(ecx, __dword_ptr[r13 + GlobalIdOffset]); a.cmp(__dword_ptr[r12], ecx); a.jne(noBase);
            a.Label(ref needQuantizer);
            a.imul(edi, edi, EntriesPerRole); a.add(edi, r10d);
            a.imul(rdi, rdi, EntrySize); a.add(rdi, __qword_ptr[rbx + TableOffset]);
            Quantize(a, faultState);
            a.mov(__qword_ptr[rsp + SavedR10], r10);
            // Preserve a zero/scaled Ox batch even if the consumer later disappears.
            a.mov(__word_ptr[r12 + 6], 0);
            var nonempty = a.CreateLabel(); a.test(r10d, r10d); a.jne(nonempty);
            a.mov(__word_ptr[r12 + 6], 1);
            a.Label(ref nonempty);
            a.cmp(ebp, (int)eChimps.CHIMP_TYPE_QUARRY_OX); var notOx = a.CreateLabel(); a.jne(notOx);
            a.or(__word_ptr[r12 + 6], 2);
            a.Label(ref notOx);
            CanonicalizeEmpty(a);
            a.Label(ref noBase); PopAll(a); a.jmp(ordinary);
            EmitFault(a, addresses.Fault, faultInput, faultProductivity, faultState, image);
            a.Label(ref ordinary);
            FixedBonus(a, back, percent, mode);
        }

        private static void ApplyFear(Assembler a, ulong image, ulong percent, ulong mode, ulong units,
            WorkerProductionHotAddresses addresses, Label faultState)
        {
            var done = a.CreateLabel();
            a.mov(rsi, __qword_ptr[rbx + FearTableOffset]); a.test(rsi, rsi); a.je(done);
            // As with the native bonus carry, the effective UnitId after Extender Pre selects the
            // player. The original actor has already selected the actual-output path and owns cargo.
            a.movsx(ecx, __word_ptr[r14 + OwnerOffset]); a.test(ecx, ecx); a.je(done);
            a.cmp(ecx, 8); a.ja(faultState);
            a.imul(rcx, rcx, WorkerProductionBonusNativeDefinition.PlayerRecordStride);
            a.mov(rax, image + WorkerProductionBonusNativeDefinition.FearLevelRva);
            a.mov(ecx, __dword_ptr[rax + rcx]);
            a.mov(eax, -5); a.cmp(ecx, eax); a.cmovl(ecx, eax);
            a.mov(eax, 5); a.cmp(ecx, eax); a.cmovg(ecx, eax);
            a.imul(edx, ecx, -10); a.xor(r11d, r11d);
            a.test(edx, edx); a.cmovl(edx, r11d);
            a.add(ecx, 5); a.mov(eax, __dword_ptr[rsi + rcx * 4]);
            // Other identity entries retain the native cached productivity across level changes.
            // Neutral always uses zero fear bonus while a fear plan is active.
            a.add(eax, 100); a.add(edx, 100);
            var resolved = a.CreateLabel();
            a.cmp(ecx, 5); a.je(resolved);
            a.cmp(eax, edx); a.cmove(eax, __dword_ptr[rsp + SavedRax]);
            a.Label(ref resolved); a.mov(__qword_ptr[rsp + SavedRax], rax);

            // Native history follows the Pre-selected unit and another redirected yield can
            // overwrite it. Keep provenance on the physical Ox until its cargo can be handled.
            a.cmp(ebp, (int)eChimps.CHIMP_TYPE_QUARRY_OX); a.jne(done);
            a.cmp(__dword_ptr[rbx + FearHasNegativeOffset], 0); a.je(done);
            var checkTotal = a.CreateLabel();
            a.test(r8d, r8d); a.je(checkTotal);
            a.mov(rcx, mode); a.cmp(__dword_ptr[rcx], 0); a.je(checkTotal);
            a.mov(rcx, percent); a.add(eax, __dword_ptr[rcx]);
            a.Label(ref checkTotal); a.cmp(eax, 100); a.jge(done);
            a.mov(rcx, addresses.StoreControl);
            a.cmp(__dword_ptr[rcx + 4], 1); a.jne(faultState);
            a.cmp(__dword_ptr[rcx], 1); a.jne(faultState);
            a.mov(rax, r13); a.mov(rcx, units); a.sub(rax, rcx);
            a.xor(edx, edx); a.mov(ecx, WorkerProductionBonusNativeDefinition.UnitStride); a.div(rcx);
            a.cmp(eax, 1); a.jl(faultState); a.cmp(eax, 9999); a.jg(faultState);
            a.shl(rax, 5); a.mov(r12, addresses.Rows); a.add(r12, rax);
            a.mov(ecx, __dword_ptr[r13 + GlobalIdOffset]); a.test(ecx, ecx); a.je(faultState);
            var sameUnit = a.CreateLabel();
            a.cmp(__dword_ptr[r12], ecx); a.je(sameUnit);
            a.mov(__dword_ptr[r12], ecx);
            a.mov(__qword_ptr[r12 + 8], 0); a.mov(__qword_ptr[r12 + 16], 1);
            a.mov(__qword_ptr[r12 + 24], 1);
            a.Label(ref sameUnit);
            a.mov(__word_ptr[r12 + 4], bp);
            a.mov(__word_ptr[r12 + 6], (int)WorkerProductionBatchFlags.StoneBatch);
            a.Label(ref done); a.nop();
        }

        internal static void FixedBonus(Assembler a, ulong back, ulong percent, ulong mode)
        {
            var done = a.CreateLabel();
            a.push(rcx); a.mov(rcx, mode); a.cmp(__dword_ptr[rcx], 0); a.je(done);
            a.test(r8d, r8d); a.je(done); a.mov(rcx, percent); a.add(eax, __dword_ptr[rcx]);
            a.Label(ref done); a.pop(rcx); a.AddUnrestrictedJmp(back);
        }

        private static void EmitWorkerIndex(Assembler a, Label selected, Label noBase)
        {
            // Values are domain enums, ordering is shared with WorkerProductionCompiler.
            eChimps[] kinds = { eChimps.CHIMP_TYPE_WOODCUTTER, eChimps.CHIMP_TYPE_FLETCHER,
                eChimps.CHIMP_TYPE_HUNTER, eChimps.CHIMP_TYPE_QUARRY_GRUNT, eChimps.CHIMP_TYPE_PITCHMAN,
                eChimps.CHIMP_TYPE_FARMER_WHEAT, eChimps.CHIMP_TYPE_FARMER_HOPS, eChimps.CHIMP_TYPE_FARMER_APPLE,
                eChimps.CHIMP_TYPE_FARMER_CATTLE, eChimps.CHIMP_TYPE_MILLER, eChimps.CHIMP_TYPE_BAKER,
                eChimps.CHIMP_TYPE_BREWER, eChimps.CHIMP_TYPE_POLETURNER, eChimps.CHIMP_TYPE_BLACKSMITH,
                eChimps.CHIMP_TYPE_ARMOURER, eChimps.CHIMP_TYPE_TANNER, eChimps.CHIMP_TYPE_MINER2 };
            for (int i = 0; i < kinds.Length; i++)
            {
                var next = a.CreateLabel(); a.cmp(ebp, (int)(i == 3 ? eChimps.CHIMP_TYPE_QUARRY_OX : kinds[i])); a.jne(next);
                a.mov(edi, i); a.jmp(selected); a.Label(ref next);
            }
            a.jmp(noBase);
        }

        // Inputs RBX=config, RDI=entry, R12=sidecar, R13=native unit, EBP=actual kind.
        // Output R10=whole amount. Scratch all saved by the enclosing generator.
        internal static void Quantize(Assembler a, Label fault)
        {
            var fresh = a.CreateLabel(); var creditReady = a.CreateLabel(); var noCredit = a.CreateLabel();
            a.mov(r8, __qword_ptr[rbx + CommonDenominatorOffset]);
            a.test(r8, r8); a.je(fault); a.test(r8, r8); a.js(fault);
            a.mov(ecx, __dword_ptr[r13 + GlobalIdOffset]); a.test(ecx, ecx); a.je(fault);
            a.cmp(__dword_ptr[r12], ecx); a.jne(fresh);
            a.cmp(__dword_ptr[r12 + 24], 1); a.jne(fault);
            a.mov(r11, __qword_ptr[r12 + 8]); a.mov(rsi, __qword_ptr[r12 + 16]);
            a.test(rsi, rsi); a.je(fault); a.test(rsi, rsi); a.js(fault);
            a.cmp(r11, rsi); a.jae(fault);
            a.mov(rax, r8); a.xor(edx, edx); a.div(rsi); a.test(rdx, rdx); a.jne(fault);
            a.imul(r11, rax); a.jmp(creditReady);
            a.Label(ref fresh);
            a.mov(__dword_ptr[r12], ecx); a.mov(__word_ptr[r12 + 4], bp);
            a.mov(__word_ptr[r12 + 6], 0); a.mov(__dword_ptr[r12 + 24], 1); a.mov(__dword_ptr[r12 + 28], 0);
            a.xor(r11d, r11d);
            a.Label(ref creditReady);
            a.cmp(__qword_ptr[rdi + 16], r8); a.jne(fault);
            a.mov(r10, __qword_ptr[rdi]); a.mov(rax, __qword_ptr[rdi + 8]);
            a.cmp(rax, r8); a.jae(fault); a.add(r11, rax);
            var below = a.CreateLabel(); a.cmp(r11, r8); a.jb(below); a.sub(r11, r8); a.inc(r10);
            a.Label(ref below); a.test(r11, r11); a.je(noCredit);
            // gcd(remainder,D); both inputs fit signed 64 bit, sum was unsigned.
            a.mov(rax, r8); a.mov(rcx, r11); var gcd = a.CreateLabel(); var reduced = a.CreateLabel();
            a.Label(ref gcd); a.xor(edx, edx); a.div(rcx); a.mov(rax, rcx); a.mov(rcx, rdx);
            a.test(rcx, rcx); a.jne(gcd);
            a.mov(rsi, rax); a.mov(rax, r11); a.xor(edx, edx); a.div(rsi); a.mov(__qword_ptr[r12 + 8], rax);
            a.mov(rax, r8); a.xor(edx, edx); a.div(rsi); a.mov(__qword_ptr[r12 + 16], rax); a.jmp(reduced);
            a.Label(ref noCredit); a.mov(__qword_ptr[r12 + 8], 0); a.mov(__qword_ptr[r12 + 16], 1);
            a.Label(ref reduced);
            a.mov(__word_ptr[r12 + 4], bp);
        }

        internal static void CanonicalizeEmpty(Assembler a)
        {
            var keep = a.CreateLabel(); a.cmp(__word_ptr[r12 + 6], 0); a.jne(keep);
            a.cmp(__qword_ptr[r12 + 8], 0); a.jne(keep);
            for (int i = 0; i < RowSize; i += 8) a.mov(__qword_ptr[r12 + i], 0);
            a.Label(ref keep); a.nop();
        }

        // RCX is the manager-relative unit origin, including the separate driver's Ox.
        // Match the stored GlobalId before treating any saved marker as this unit's history.
        internal static void BranchHistory(Assembler a, WorkerProductionHotAddresses addresses, ulong units, Label active)
        {
            var absent = a.CreateLabel(); var done = a.CreateLabel();
            PushAll(a);
            a.mov(rbx, addresses.StoreControl); a.cmp(__dword_ptr[rbx + 4], 1); a.jne(absent);
            a.mov(r13, rcx); a.mov(rax, rcx); a.mov(rdx, units); a.sub(rax, rdx);
            a.xor(edx, edx); a.mov(ebx, 0x490); a.div(rbx); a.test(rdx, rdx); a.jne(absent);
            a.cmp(eax, 1); a.jl(absent); a.cmp(eax, 9999); a.jg(absent);
            a.shl(rax, 5); a.mov(r12, addresses.Rows); a.add(r12, rax);
            a.mov(eax, __dword_ptr[r13 + GlobalIdOffset]); a.test(eax, eax); a.je(absent);
            a.cmp(__dword_ptr[r12], eax); a.jne(absent); a.cmp(__word_ptr[r12 + 6], 0); a.je(absent);
            a.movzx(eax,__word_ptr[r13+KindOffset]); a.cmp(__word_ptr[r12+4],ax); a.jne(absent);
            PopAll(a); a.jmp(active);
            a.Label(ref absent); PopAll(a); a.jmp(done);
            a.Label(ref done); a.nop(); // one bound label per instruction, including caller labels
        }

        internal static void ClearCurrentBatch(Assembler a, WorkerProductionHotAddresses addresses, ulong units, ulong currentUnit)
        {
            var done = a.CreateLabel(); PushAll(a);
            a.mov(rbx, addresses.StoreControl); a.cmp(__dword_ptr[rbx + 4], 1); a.jne(done);
            a.mov(rax, currentUnit); a.movsxd(rax, __dword_ptr[rax]); a.cmp(eax, 1); a.jl(done);
            a.cmp(eax, 9999); a.jg(done); a.imul(rcx, rax, 0x490); a.mov(r13, units); a.add(r13, rcx);
            a.shl(rax, 5); a.mov(r12, addresses.Rows); a.add(r12, rax);
            a.mov(eax, __dword_ptr[r13 + GlobalIdOffset]); a.test(eax, eax); a.je(done);
            a.cmp(__dword_ptr[r12], eax); a.jne(done); a.mov(__word_ptr[r12 + 6], 0);
            CanonicalizeEmpty(a);
            a.Label(ref done); PopAll(a);
        }

        private static void EmitFault(Assembler a, ulong faultAddress, Label input, Label productivity, Label state, ulong image)
        {
            var save = a.CreateLabel(); var leave = a.CreateLabel();
            a.Label(ref input); a.mov(edi, 1); a.jmp(save);
            a.Label(ref productivity); a.mov(edi, 2); a.jmp(save);
            a.Label(ref state); a.mov(edi, 3);
            a.Label(ref save); a.mov(rbx, faultAddress); a.cmp(__dword_ptr[rbx], 0); a.jne(leave);
            a.mov(rax, __qword_ptr[rsp + SavedR9]); a.xor(edx, edx); a.mov(ecx, 0x490); a.div(rcx);
            a.mov(__dword_ptr[rbx + 4], eax); a.mov(rax, __qword_ptr[rsp + SavedR10]); a.mov(__dword_ptr[rbx + 8], eax);
            a.mov(rax, __qword_ptr[rsp + SavedRax]); a.mov(__dword_ptr[rbx + 12], eax);
            a.mov(__dword_ptr[rbx], edi);
            a.Label(ref leave); PopAll(a); a.xor(eax, eax); a.AddUnrestrictedJmp(image + 0x18D9E1);
        }

        internal static void PushAll(Assembler a)
        {
            a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx); a.push(rbx); a.push(rbp); a.push(rsi); a.push(rdi);
            a.push(r8); a.push(r9); a.push(r10); a.push(r11); a.push(r12); a.push(r13); a.push(r14); a.push(r15);
        }
        internal static void PopAll(Assembler a)
        {
            a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12); a.pop(r11); a.pop(r10); a.pop(r9); a.pop(r8);
            a.pop(rdi); a.pop(rsi); a.pop(rbp); a.pop(rbx); a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
        }
    }
}
