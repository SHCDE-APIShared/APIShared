using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal static partial class WeaponProductionNativeAssembly
    {
        internal static byte[] ExpectedStoredDeliveryStub(WeaponStoredDeliverySite site, ulong image, ulong stub,
            ulong enabled, ulong buildingDemand, ulong workerWire, ulong storedOptions, ulong callback) => Internal.NativeInlineStub.ExpectedStub(
                image + (uint)site.Rva, site.Bytes, site.Bytes.Length,
                (assembler, original, back) => GenerateStoredDelivery(assembler, original, back, site, image, enabled, buildingDemand, workerWire, storedOptions, callback),
                stub, WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);

        internal static void GenerateStoredDelivery(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            WeaponStoredDeliverySite site, ulong image, ulong enabled, ulong buildingDemand, ulong workerWire, ulong storedOptions, ulong callback)
        {
            if (a == null || site == null || image == 0 || enabled == 0 || buildingDemand == 0 || workerWire == 0 || storedOptions == 0 || callback == 0 ||
                image > ulong.MaxValue - ((ulong)WorkerProductionBonusNativeDefinition.UnitManagerRva +
                    (ulong)WeaponProductionNativeDefinition.MaxUnitId * WorkerProductionBonusNativeDefinition.UnitStride +
                    WorkerProductionBonusNativeDefinition.CarriedOffset) ||
                back != image + (uint)site.Rva + (uint)site.Bytes.Length)
                throw new InvalidOperationException(WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);
            var expected = WorkerProductionBonusNativeDefinition.Decode(site.Bytes, image + (uint)site.Rva);
            if (original.Length != expected.Length)
                throw new InvalidOperationException(WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);
            for (int i = 0; i < original.Length; i++)
                if (!Instruction.EqualsAllBits(in original[i], in expected[i]))
                    throw new InvalidOperationException(WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);
            // Validate register metadata before emitting even the first demand instruction.
            Stored64(site.UnitIdRegister); Stored64(site.UnitOffsetRegister);
            Stored64(site.UnitManagerRegister); Stored64(site.BuildingManagerRegister);
            Stored32(site.UnitIdRegister); Stored32(site.ZeroRegister); Stored32(site.OneRegister); Stored32(site.ThreeRegister);

            var dormant = a.CreateLabel(); var native = a.CreateLabel();
            var accepted = a.CreateLabel(); var refused = a.CreateLabel();
            EmitStoredDeliveryDemandCheck(a, dormant, enabled, buildingDemand, workerWire, storedOptions,
                image + WorkerProductionBonusNativeDefinition.CurrentUnitRva, image + WorkerProductionBonusNativeDefinition.UnitManagerRva);
            RestoreStoredDeliveryDemandRegisters(a);
            EmitStoredDeliveryDecision(a, callback, native, accepted, refused,
                image + WorkerProductionBonusNativeDefinition.CurrentUnitRva, site.UnitIdRegister);
            a.Label(ref dormant); RestoreStoredDeliveryDemandRegisters(a);
            a.Label(ref native);
            WorkerProductionBonusNativeAssembly.Replay(a, original);
            a.AddUnrestrictedJmp(back);

            a.Label(ref accepted);
            // The helper carried its original physical ID through private stack storage.
            // Rebuild the skipped success tail from that ID, never a later mutable global.
            a.imul(Stored64(site.UnitOffsetRegister), Stored64(site.UnitIdRegister), WorkerProductionBonusNativeDefinition.UnitStride);
            a.mov(Stored32(site.ZeroRegister), 0); a.mov(Stored32(site.OneRegister), 1); a.mov(Stored32(site.ThreeRegister), 3);
            a.mov(Stored64(site.UnitManagerRegister), image + WorkerProductionBonusNativeDefinition.UnitManagerRva);
            a.mov(Stored64(site.BuildingManagerRegister), image + WorkerProductionBonusNativeDefinition.BuildingManagerRva);
            a.AddUnrestrictedJmp(image + (uint)site.FooterRva);

            a.Label(ref refused);
            // The epilogue performs no delivery-tail writes. Only a separately authorized
            // callback may schedule native search; cargo/destination stay intact here.
            // Never enter the delivery fallback, which can discard that cargo.
            a.AddUnrestrictedJmp(image + (uint)site.EpilogueRva);
        }

        // The wire is the existing saved/repaired batch row, not a new per-worker
        // activation registry. A frozen 32-byte option lookup excludes native-only
        // batches before a managed callback frame is built, even in a mixed catalogue.
        // Explicit addresses also permit bounded synthetic execution of this exact guard.
        internal static void EmitStoredDeliveryDemandCheck(Assembler a, Label dormant, ulong enabled, ulong buildingDemand,
            ulong workerWire, ulong storedOptions, ulong currentUnit, ulong units)
        {
            if (a == null || enabled == 0 || buildingDemand == 0 || workerWire == 0 || storedOptions == 0 || currentUnit == 0 || units == 0)
                throw new InvalidOperationException(WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);
            a.pushfq(); a.push(rax); a.push(rcx);
            a.mov(rax, enabled); a.cmp(__dword_ptr[rax], 0); a.je(dormant);
            a.mov(rax, currentUnit); a.mov(eax, __dword_ptr[rax]);
            a.cmp(eax, 1); a.jl(dormant);
            a.cmp(eax, WeaponProductionNativeDefinition.MaxUnitId); a.jg(dormant);
            a.imul(rax, rax, WorkerProductionBonusNativeDefinition.UnitStride);
            a.mov(rcx, units);
            a.movsx(eax, __word_ptr[rcx + rax + WeaponProductionNativeDefinition.LinkedBuildingOffset]);
            a.cmp(eax, 1); a.jl(dormant);
            a.cmp(eax, WeaponProductionNativeDefinition.MaxBuildingId); a.jg(dormant);
            a.mov(rcx, buildingDemand); a.cmp(__byte_ptr[rcx + rax], 0); a.je(dormant);
            a.mov(rax, currentUnit); a.mov(eax, __dword_ptr[rax]);
            a.imul(rax, rax, WeaponProductionNativeDefinition.StoredWorkerWireStride);
            a.mov(rcx, workerWire);
            a.movzx(eax, __byte_ptr[rcx + rax + WeaponProductionNativeDefinition.StoredWorkerOptionOffset]);
            a.cmp(eax, WeaponProductionNativeDefinition.StoredOptionCount - 1); a.ja(dormant);
            a.mov(rcx, storedOptions); a.cmp(__byte_ptr[rcx + rax], 1); a.jne(dormant);
        }
        internal static void RestoreStoredDeliveryDemandRegisters(Assembler a) => RestoreDemandRegisters(a);

        // The production callback frame is also executed by a bounded native-free fixture.
        // No argument/output register is borrowed: Native and Refused restore every live
        // register and flag. Only the accepted continuation performs explicit footer repair.
        internal static void EmitStoredDeliveryDecision(Assembler a, ulong callback, Label native, Label accepted, Label refused,
            ulong currentUnit = 0, Register capturedUnitId = Register.None)
        {
            if (a == null || callback == 0) throw new InvalidOperationException(WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);
            if ((currentUnit == 0) != (capturedUnitId == Register.None) || currentUnit != 0 &&
                capturedUnitId != Register.RBX && capturedUnitId != Register.RBP && capturedUnitId != Register.R14 && capturedUnitId != Register.R15)
                throw new InvalidOperationException(WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);
            var nativeReturn = a.CreateLabel(); var acceptedReturn = a.CreateLabel(); var refusedReturn = a.CreateLabel();
            a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx); a.push(r8); a.push(r9); a.push(r10); a.push(r11);
            a.mov(r11, rsp); a.and(rsp, -16); a.sub(rsp, 144); a.mov(__qword_ptr[rsp + 128], r11);
            a.movdqu(__xmmword_ptr[rsp + 32], xmm0); a.movdqu(__xmmword_ptr[rsp + 48], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 64], xmm2); a.movdqu(__xmmword_ptr[rsp + 80], xmm3);
            a.movdqu(__xmmword_ptr[rsp + 96], xmm4); a.movdqu(__xmmword_ptr[rsp + 112], xmm5);
            if (currentUnit != 0)
            {
                a.mov(rax, currentUnit); a.mov(eax, __dword_ptr[rax]);
                a.mov(__dword_ptr[rsp + 136], eax); a.mov(ecx, eax);
            }
            a.cld(); a.AddUnrestrictedCall(callback);
            a.test(eax, eax); a.je(nativeReturn);
            a.cmp(eax, 1); a.je(acceptedReturn);
            a.jmp(refusedReturn); // Unknown callback results fail closed.
            a.Label(ref nativeReturn); RestoreStoredDeliveryFrame(a); a.jmp(native);
            a.Label(ref acceptedReturn);
            if (currentUnit != 0) a.mov(Stored32(capturedUnitId), __dword_ptr[rsp + 136]);
            RestoreStoredDeliveryFrame(a); a.jmp(accepted);
            a.Label(ref refusedReturn); RestoreStoredDeliveryFrame(a); a.jmp(refused);
        }

        private static void RestoreStoredDeliveryFrame(Assembler a)
        {
            a.movdqu(xmm0, __xmmword_ptr[rsp + 32]); a.movdqu(xmm1, __xmmword_ptr[rsp + 48]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 64]); a.movdqu(xmm3, __xmmword_ptr[rsp + 80]);
            a.movdqu(xmm4, __xmmword_ptr[rsp + 96]); a.movdqu(xmm5, __xmmword_ptr[rsp + 112]);
            a.mov(rsp, __qword_ptr[rsp + 128]);
            a.pop(r11); a.pop(r10); a.pop(r9); a.pop(r8); a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
        }

        private static AssemblerRegister64 Stored64(Register register)
        {
            switch (register)
            {
                case Register.RAX: return rax; case Register.RBX: return rbx; case Register.RCX: return rcx; case Register.RDX: return rdx;
                case Register.RBP: return rbp; case Register.RSI: return rsi; case Register.RDI: return rdi;
                case Register.R8: return r8; case Register.R9: return r9; case Register.R10: return r10; case Register.R11: return r11;
                case Register.R12: return r12; case Register.R13: return r13; case Register.R14: return r14; case Register.R15: return r15;
                default: throw new InvalidOperationException(WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);
            }
        }
        private static AssemblerRegister32 Stored32(Register register)
        {
            switch (register)
            {
                case Register.RAX: return eax; case Register.RBX: return ebx; case Register.RCX: return ecx; case Register.RDX: return edx;
                case Register.RBP: return ebp; case Register.RSI: return esi; case Register.RDI: return edi;
                case Register.R8: return r8d; case Register.R9: return r9d; case Register.R10: return r10d; case Register.R11: return r11d;
                case Register.R12: return r12d; case Register.R13: return r13d; case Register.R14: return r14d; case Register.R15: return r15d;
                default: throw new InvalidOperationException(WeaponProductionNativeDefinition.StoredDeliveryBoundaryError);
            }
        }
    }
}
