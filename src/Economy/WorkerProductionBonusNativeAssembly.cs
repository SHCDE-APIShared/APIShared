using System;
using System.Collections.Generic;
using System.IO;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal enum WorkerProductionBonusFooterValue { UnitId, UnitOffset, UnitManager, BuildingManager, Zero, One, Two, Three }
    internal readonly struct WorkerProductionBonusFooterRegister
    {
        internal WorkerProductionBonusFooterRegister(Register register, WorkerProductionBonusFooterValue value)
        { Register=register; Value=value; }
        internal Register Register { get; }
        internal WorkerProductionBonusFooterValue Value { get; }
    }

    // Pure native generators: no callback, allocation, event or managed transition in
    // the running game. Flags and scratch registers are restored before replaying Vanilla.
    internal static class WorkerProductionBonusNativeAssembly
    {
        internal static void Generate(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            WorkerProductionBonusNativeSite site, ulong imageBase, ulong percentAddress,
            ulong modeAddress, ulong unitManagerAddress, ulong currentUnitAddress, ulong readyAddress=0,
            WorkerProductionHotAddresses hot=default)
        {
            if(a==null || site==null || back!=imageBase+(uint)site.Rva+(uint)site.Length)
                throw new InvalidOperationException("Worker production generator continuation differs.");
            int length=0; foreach(var instruction in original) length+=instruction.Length;
            if(length!=site.Length) throw new InvalidOperationException("Worker production displaced block differs.");
            if(site.Kind==WorkerProductionBonusNativeKind.Core)
            {
                if(hot.IsDefined)
                { WorkerProductionHotAssembly.Core(a,imageBase,back,percentAddress,modeAddress,unitManagerAddress,hot); return; }
                // RCX held the now-dead productivity minimum; retain it anyway. The native
                // ADD at back establishes its own flags before any downstream consumer.
                var done=a.CreateLabel();
                a.push(rcx); a.mov(rcx,modeAddress); a.cmp(__dword_ptr[rcx],0); a.je(done);
                a.test(r8d,r8d); a.je(done); a.mov(rcx,percentAddress); a.add(eax,__dword_ptr[rcx]);
                a.Label(ref done); a.pop(rcx); a.AddUnrestrictedJmp(back); return;
            }

            var vanilla=a.CreateLabel(); var active=a.CreateLabel();
            // Extra is the signed actual-minus-base result of the last production. It is
            // stored by 18D9C6, survives native saves and is cleared with its unit slot.
            // Thus an old reduced/zero load remains protected even after an override is
            // removed. A normal native calculation cannot create a negative Extra.
            // The ready DWORD prevents even this historical path during partial startup.
            a.pushfq(); a.push(rax); a.push(rcx);
            a.mov(rax,readyAddress==0 ? percentAddress+WorkerProductionBonusNativeDefinition.ReadyOffset : readyAddress);
            a.cmp(__dword_ptr[rax],0); a.je(vanilla);
            // These are audited completed-output phases. Vanilla's positive production
            // never arrives empty here; an empty cargo must finish even if SE Pre redirected
            // bonus bookkeeping to another unit, or the old owner has been disabled.
            if(site.Kind==WorkerProductionBonusNativeKind.ZeroCompletion) a.jmp(active);
            if(hot.IsDefined && (site.Kind==WorkerProductionBonusNativeKind.OxDeparture || site.Kind==WorkerProductionBonusNativeKind.Reserve))
            {
                var noSignedFear=a.CreateLabel();
                a.mov(rax,hot.ConfigurationSlot); a.mov(rax,__qword_ptr[rax]);
                a.test(rax,rax); a.je(noSignedFear);
                a.cmp(__dword_ptr[rax+WorkerProductionHotAssembly.FearHasNegativeOffset],0); a.jne(active);
                a.Label(ref noSignedFear);
            }
            HistoryOrigin(a,site,unitManagerAddress,currentUnitAddress);
            if(hot.IsDefined) WorkerProductionHotAssembly.BranchHistory(a,hot,unitManagerAddress,active);
            a.cmp(__word_ptr[rcx+WorkerProductionBonusNativeDefinition.BonusYieldOffset],0); a.jl(active);
            a.mov(rax,percentAddress); a.cmp(__dword_ptr[rax],0); a.jge(vanilla);
            a.mov(rax,modeAddress); a.cmp(__dword_ptr[rax],0); a.je(vanilla);
            a.Label(ref active);

            if(site.Kind==WorkerProductionBonusNativeKind.ZeroCompletion)
            {
                a.mov(rax,currentUnitAddress); a.movsxd(rax,__dword_ptr[rax]);
                a.imul(rax,rax,WorkerProductionBonusNativeDefinition.UnitStride);
                a.mov(rcx,unitManagerAddress);
                a.cmp(__word_ptr[rax+rcx+WorkerProductionBonusNativeDefinition.CarriedOffset],0);
                a.jne(vanilla);
                if(hot.IsDefined) WorkerProductionHotAssembly.ClearCurrentBatch(a,hot,unitManagerAddress,currentUnitAddress);
                Restore(a);
                SetupFooter(a,site,imageBase,unitManagerAddress,currentUnitAddress);
                a.AddUnrestrictedJmp(imageBase+(uint)site.FooterRva);
            }
            else if(site.Kind==WorkerProductionBonusNativeKind.OxDeparture)
            {
                Restore(a);
                // Only output admission changes. The driver's eight-stone input batch and
                // all production counters remain Vanilla. The branch target is unchanged.
                var comparison=original[0];
                if(comparison.Code!=Code.Cmp_rm16_imm8 || comparison.Immediate8!=8 || original.Length!=2)
                    throw new InvalidOperationException("Ox output admission contract differs.");
                comparison.Immediate8=1;
                AddRelocated(a,comparison); AddRelocated(a,original[1]); a.AddUnrestrictedJmp(back);
            }
            else if(site.Kind==WorkerProductionBonusNativeKind.Reserve)
            {
                Restore(a);
                if(original.Length!=2 || original[1].FlowControl!=FlowControl.ConditionalBranch)
                    throw new InvalidOperationException("Ox reserve admission contract differs.");
                AddRelocated(a,original[0]); a.jle(original[1].NearBranchTarget); a.AddUnrestrictedJmp(back);
            }
            else if(site.Kind==WorkerProductionBonusNativeKind.Render)
            {
                // RAX on entry is the original unit-manager-relative record origin. Its
                // saved value is at RSP+8 after our three pushes. Negative extra-yield
                // bookkeeping must never choose Vanilla's positive +5 visual fallback.
                a.mov(rcx,__qword_ptr[rsp+8]);
                a.cmp(__word_ptr[rcx+WorkerProductionBonusNativeDefinition.BonusYieldOffset],0);
                a.jg(vanilla); Restore(a);
                a.AddUnrestrictedJmp(imageBase+WorkerProductionBonusNativeDefinition.RenderRejectRva);
            }
            else throw new InvalidOperationException("Unknown worker production native site.");

            a.Label(ref vanilla); Restore(a); Replay(a,original); a.AddUnrestrictedJmp(back);
        }

        internal static void Restore(Assembler a) { a.pop(rcx); a.pop(rax); a.popfq(); }
        internal static void Replay(Assembler a,ReadOnlySpan<Instruction> original)
        { foreach(var instruction in original) AddRelocated(a,instruction); }
        private static void AddRelocated(Assembler a,Instruction instruction)
        {
            // Active and dormant blocks can replay the same original instruction. Preserve
            // absolute RIP/branch operands but give each copy a fresh block-encoder identity.
            instruction.IP=0; a.AddInstruction(instruction);
        }

        private static void HistoryOrigin(Assembler a,WorkerProductionBonusNativeSite site,ulong units,ulong currentUnit)
        {
            if(site.Kind==WorkerProductionBonusNativeKind.Reserve)
            {
                // This handler belongs to the driver; its RBX points at the other unit,
                // the Ox holding the reserve. The current-driver slot is not the producer.
                a.mov(rcx,rbx);
            }
            else if(site.Kind==WorkerProductionBonusNativeKind.Render)
            {
                // The render loop's original unit origin was saved by the guard.
                a.mov(rcx,__qword_ptr[rsp+8]);
            }
            else
            {
                // Field/workshop handlers and both Ox update branches receive the current
                // simulation unit. The worker dispatcher establishes this one-based ID.
                a.mov(rax,currentUnit); a.movsxd(rax,__dword_ptr[rax]);
                a.imul(rax,rax,WorkerProductionBonusNativeDefinition.UnitStride);
                a.mov(rcx,units); a.add(rcx,rax);
            }
        }

        // These are native continuation contracts, not a second worker state machine. Each
        // target starts at the original Carry comparison and runs its whole existing footer:
        // work accounting, fear breaks, ingredient choice, farm continuations and retirement.
        // In particular the four workshop failure paths read their retirement marker from a
        // register normally established during skipped storage acceptance (native value 3).
        internal static IReadOnlyList<WorkerProductionBonusFooterRegister> FooterRegisterAssignments(WorkerProductionBonusNativeSite site)
        {
            switch(site.Name)
            {
                case "Wood": case "Pitch": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitId), F(Register.RCX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R12,WorkerProductionBonusFooterValue.UnitManager), F(Register.RSI,WorkerProductionBonusFooterValue.Zero), F(Register.RDI,WorkerProductionBonusFooterValue.One) };
                case "Hunter": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitId), F(Register.RCX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R13,WorkerProductionBonusFooterValue.UnitManager), F(Register.RBP,WorkerProductionBonusFooterValue.Zero), F(Register.RSI,WorkerProductionBonusFooterValue.One) };
                case "Hops": return new[] { F(Register.RCX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R12,WorkerProductionBonusFooterValue.UnitManager), F(Register.RBX,WorkerProductionBonusFooterValue.BuildingManager), F(Register.RDI,WorkerProductionBonusFooterValue.Zero) };
                case "Apple": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R12,WorkerProductionBonusFooterValue.UnitManager), F(Register.RDI,WorkerProductionBonusFooterValue.Zero), F(Register.RBP,WorkerProductionBonusFooterValue.One), F(Register.R8,WorkerProductionBonusFooterValue.UnitId) };
                case "Cattle": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R13,WorkerProductionBonusFooterValue.UnitManager), F(Register.RDI,WorkerProductionBonusFooterValue.Zero), F(Register.RSI,WorkerProductionBonusFooterValue.One), F(Register.R8,WorkerProductionBonusFooterValue.UnitId), F(Register.R12,WorkerProductionBonusFooterValue.Two) };
                case "Iron": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitId), F(Register.RCX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R15,WorkerProductionBonusFooterValue.UnitManager), F(Register.RSI,WorkerProductionBonusFooterValue.Zero), F(Register.R12,WorkerProductionBonusFooterValue.One) };
                case "Wheat": return new[] { F(Register.RDX,WorkerProductionBonusFooterValue.UnitId), F(Register.R13,WorkerProductionBonusFooterValue.UnitManager), F(Register.RBP,WorkerProductionBonusFooterValue.BuildingManager), F(Register.RDI,WorkerProductionBonusFooterValue.Zero) };
                case "BakerStart": case "BakerArrival": return new[] { F(Register.R8,WorkerProductionBonusFooterValue.UnitId), F(Register.R14,WorkerProductionBonusFooterValue.UnitManager), F(Register.RBX,WorkerProductionBonusFooterValue.BuildingManager), F(Register.R12,WorkerProductionBonusFooterValue.Zero), F(Register.R15,WorkerProductionBonusFooterValue.One), F(Register.RSI,WorkerProductionBonusFooterValue.Two) };
                case "Fletcher": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R12,WorkerProductionBonusFooterValue.UnitManager), F(Register.RDI,WorkerProductionBonusFooterValue.Zero), F(Register.RSI,WorkerProductionBonusFooterValue.One), F(Register.R14,WorkerProductionBonusFooterValue.UnitId), F(Register.R15,WorkerProductionBonusFooterValue.Three) };
                case "Brewer": return new[] { F(Register.RDI,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R15,WorkerProductionBonusFooterValue.UnitManager), F(Register.RSI,WorkerProductionBonusFooterValue.Zero), F(Register.R12,WorkerProductionBonusFooterValue.One), F(Register.RBX,WorkerProductionBonusFooterValue.UnitId) };
                case "Poleturner": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R12,WorkerProductionBonusFooterValue.UnitManager), F(Register.RSI,WorkerProductionBonusFooterValue.Zero), F(Register.R13,WorkerProductionBonusFooterValue.One), F(Register.RBP,WorkerProductionBonusFooterValue.UnitId), F(Register.R14,WorkerProductionBonusFooterValue.Three) };
                case "Blacksmith": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R12,WorkerProductionBonusFooterValue.UnitManager), F(Register.R13,WorkerProductionBonusFooterValue.Zero), F(Register.RDI,WorkerProductionBonusFooterValue.One), F(Register.RBP,WorkerProductionBonusFooterValue.UnitId), F(Register.R14,WorkerProductionBonusFooterValue.Three) };
                case "Armourer": return new[] { F(Register.R14,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R13,WorkerProductionBonusFooterValue.UnitManager), F(Register.RBP,WorkerProductionBonusFooterValue.Zero), F(Register.RSI,WorkerProductionBonusFooterValue.One), F(Register.R15,WorkerProductionBonusFooterValue.UnitId), F(Register.RBX,WorkerProductionBonusFooterValue.BuildingManager), F(Register.RDI,WorkerProductionBonusFooterValue.Three) };
                case "Tanner": return new[] { F(Register.RBX,WorkerProductionBonusFooterValue.UnitId), F(Register.RCX,WorkerProductionBonusFooterValue.UnitOffset), F(Register.R12,WorkerProductionBonusFooterValue.UnitManager), F(Register.RDI,WorkerProductionBonusFooterValue.Zero), F(Register.R13,WorkerProductionBonusFooterValue.One), F(Register.RBP,WorkerProductionBonusFooterValue.Three) };
                default: throw new InvalidOperationException("No worker production footer register contract for " + site.Name);
            }
        }

        private static WorkerProductionBonusFooterRegister F(Register register,WorkerProductionBonusFooterValue value) => new WorkerProductionBonusFooterRegister(register,value);
        internal static void SetupFooter(Assembler a,WorkerProductionBonusNativeSite site,ulong image,ulong units,ulong currentUnit)
        {
            a.mov(rax,currentUnit); a.movsxd(rax,__dword_ptr[rax]);
            a.imul(rdx,rax,WorkerProductionBonusNativeDefinition.UnitStride);
            foreach(var field in FooterRegisterAssignments(site))
            {
                switch(field.Value)
                {
                    case WorkerProductionBonusFooterValue.UnitId: a.AddInstruction(Instruction.Create(Code.Mov_r64_rm64,field.Register,Register.RAX)); break;
                    case WorkerProductionBonusFooterValue.UnitOffset: a.AddInstruction(Instruction.Create(Code.Mov_r64_rm64,field.Register,Register.RDX)); break;
                    case WorkerProductionBonusFooterValue.UnitManager: Move(a,field.Register,units); break;
                    case WorkerProductionBonusFooterValue.BuildingManager: Move(a,field.Register,image+WorkerProductionBonusNativeDefinition.BuildingManagerRva); break;
                    case WorkerProductionBonusFooterValue.Zero: Move(a,field.Register,0); break;
                    case WorkerProductionBonusFooterValue.One: Move(a,field.Register,1); break;
                    case WorkerProductionBonusFooterValue.Two: Move(a,field.Register,2); break;
                    case WorkerProductionBonusFooterValue.Three: Move(a,field.Register,3); break;
                    default: throw new InvalidOperationException("Unknown worker footer register value.");
                }
            }
        }
        private static void Move(Assembler a,Register register,ulong value) => a.AddInstruction(Instruction.Create(Code.Mov_r64_imm64,register,value));
        internal static byte[] Assemble(Assembler assembler,ulong address) => Internal.NativeInlineStub.Assemble(assembler,address);
        internal static byte[] ExpectedStub(WorkerProductionBonusNativeSite site,ulong image,ulong stub,ulong percent,ulong mode,ulong units,ulong currentUnit,ulong ready=0,
            WorkerProductionHotAddresses hot=default) =>
            Internal.NativeInlineStub.ExpectedStub(image+(uint)site.Rva,site.OriginalBytes,site.Length,
                (a,original,back)=>Generate(a,original,back,site,image,percent,mode,units,currentUnit,ready,hot),stub,
                WorkerProductionBonusNativeDefinition.BoundaryError);
    }
}
