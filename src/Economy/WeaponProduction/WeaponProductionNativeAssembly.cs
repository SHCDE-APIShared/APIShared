using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal static partial class WeaponProductionNativeAssembly
    {
        internal static byte[] ExpectedStub(WeaponProductionNativeSite site, ulong image, ulong stub,
            ulong enabled, ulong buildingDemand, ulong callback, ulong receiptAdapter = 0) =>
            Internal.NativeInlineStub.ExpectedStub(image + (uint)site.Rva, site.Bytes, site.Bytes.Length,
                (assembler, original, back) => Generate(assembler, original, back, site, image, enabled, buildingDemand, callback, receiptAdapter),
                stub, WorkerProductionBonusNativeDefinition.BoundaryError);

        internal static void Generate(Assembler assembler, ReadOnlySpan<Instruction> original, ulong back,
            WeaponProductionNativeSite site, ulong image, ulong enabled, ulong buildingDemand, ulong callback, ulong receiptAdapter = 0)
        {
            if (callback == 0 || original.Length == 0 || back != image + (uint)site.Rva + (uint)site.Bytes.Length ||
                original[original.Length - 1].NextIP != back)
                throw new InvalidOperationException("Weapon production hook continuation differs.");

            if (TryGenerateWorkshopIngredient(assembler, original, back, site, image, enabled, buildingDemand, callback)) return;

            int callIndex = -1;
            if (site.CallTarget != 0)
            {
                for (int i = 0; i < original.Length; i++)
                {
                    Instruction call = original[i];
                    if (call.Code != Code.Call_rel32_64) continue;
                    if (callIndex >= 0 || call.NearBranchTarget != image + (uint)site.CallTarget)
                        throw new InvalidOperationException("Weapon production original call target differs.");
                    callIndex = i;
                }
                if (callIndex < 0)
                    throw new InvalidOperationException("Weapon production original call target differs.");
            }

            bool beforeCall = !site.FilterBeforeAdjacentCall &&
                (site.Operation == WeaponProductionNativeOperation.Yield || site.Operation == WeaponProductionNativeOperation.Deposit ||
                    site.Operation == WeaponProductionNativeOperation.IngredientFindWood);
            bool beforeBlock = site.Operation == WeaponProductionNativeOperation.Complete ||
                site.Operation == WeaponProductionNativeOperation.IngredientThreshold ||
                site.Operation == WeaponProductionNativeOperation.IngredientConsumeWood;
            int prefixLength = beforeBlock ? 0 : beforeCall ? callIndex : callIndex >= 0 ? callIndex + 1 : original.Length;
            WorkerProductionBonusNativeAssembly.Replay(assembler, original.Slice(0, prefixLength));

            var vanilla = assembler.CreateLabel();
            var finish = assembler.CreateLabel();
            EmitDemandCheck(assembler, vanilla, site.Operation, image, enabled, buildingDemand);
            RestoreDemandRegisters(assembler);
            EmitFilter(assembler, site.Operation, callback);
            if (!TryBranchForWoodIngredient(assembler, site, finish, image, back)) assembler.jmp(finish);

            assembler.Label(ref vanilla);
            RestoreDemandRegisters(assembler);

            assembler.Label(ref finish);
            // Receipt observation follows recipe filtering on both the configured and
            // vanilla-demand paths. Tanner's adjacent call belongs to its source owner.
            if (site.Operation == WeaponProductionNativeOperation.Deposit && !site.FilterBeforeAdjacentCall)
                ProductionGoodsReceivedNativeAssembly.ReplayDeposit(assembler, original.Slice(prefixLength), image,
                    receiptAdapter);
            else
                WorkerProductionBonusNativeAssembly.Replay(assembler, original.Slice(prefixLength));
            assembler.AddUnrestrictedJmp(back);
        }

        private static void EmitDemandCheck(Assembler a, Label vanilla, WeaponProductionNativeOperation operation,
            ulong image, ulong enabled, ulong buildingDemand)
        {
            a.pushfq(); a.push(rax); a.push(rcx);
            a.mov(rax, enabled); a.cmp(__dword_ptr[rax], 0); a.je(vanilla);
            a.mov(rax, image + WorkerProductionBonusNativeDefinition.CurrentUnitRva);
            a.mov(eax, __dword_ptr[rax]);
            a.cmp(eax, 1); a.jl(vanilla);
            a.cmp(eax, WeaponProductionNativeDefinition.MaxUnitId); a.jg(vanilla);
            a.imul(rax, rax, WorkerProductionBonusNativeDefinition.UnitStride);
            a.mov(rcx, image + WorkerProductionBonusNativeDefinition.UnitManagerRva);
            if (operation == WeaponProductionNativeOperation.Complete)
            {
                a.cmp(__word_ptr[rcx + rax + WorkerProductionBonusNativeDefinition.CarriedOffset], 0);
                a.jg(vanilla);
            }
            a.movsx(eax, __word_ptr[rcx + rax + WeaponProductionNativeDefinition.LinkedBuildingOffset]);
            a.cmp(eax, 1); a.jl(vanilla);
            a.cmp(eax, WeaponProductionNativeDefinition.MaxBuildingId); a.jg(vanilla);
            if (operation == WeaponProductionNativeOperation.IngredientFindWood)
            {
                // Surplus is native stock, which survives a switch back to a vanilla
                // option. This cohort is active only for types with ingredient overrides.
                a.imul(rax, rax, WeaponProductionNativeDefinition.BuildingStride);
                a.mov(rcx, image + WorkerProductionBonusNativeDefinition.BuildingManagerRva);
                a.cmp(__dword_ptr[rcx + rax + WeaponProductionNativeDefinition.WorkshopPlanksOffset], 0);
                a.jle(vanilla);
            }
            else
            {
                a.mov(rcx, buildingDemand); a.cmp(__byte_ptr[rcx + rax], 0); a.je(vanilla);
            }
        }

        private static void RestoreDemandRegisters(Assembler a)
        {
            a.pop(rcx); a.pop(rax); a.popfq();
        }

        // Observations run inside a worker basic block, so a normal call's volatile
        // registers and flags can still be live. Retain them and use private shadow space.
        private static void EmitFilter(Assembler a, WeaponProductionNativeOperation operation, ulong callback)
        {
            const int savedRax = 48, savedRcx = 40, savedRdx = 32, savedR9 = 16;
            a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx); a.push(r8); a.push(r9); a.push(r10); a.push(r11);
            a.mov(r11, rsp); a.and(rsp, -16); a.sub(rsp, 144); a.mov(__qword_ptr[rsp + 128], r11);
            a.movdqu(__xmmword_ptr[rsp + 32], xmm0); a.movdqu(__xmmword_ptr[rsp + 48], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 64], xmm2); a.movdqu(__xmmword_ptr[rsp + 80], xmm3);
            a.movdqu(__xmmword_ptr[rsp + 96], xmm4); a.movdqu(__xmmword_ptr[rsp + 112], xmm5);
            // Yield already has unitId in ECX and base amount in EDX. The other
            // value filters take the exact original register as their only argument.
            int output = -1;
            switch (operation)
            {
                case WeaponProductionNativeOperation.Yield: output = savedRdx; break;
                case WeaponProductionNativeOperation.Deposit: a.mov(ecx, r9d); output = savedR9; break;
                case WeaponProductionNativeOperation.Manufacturing: a.mov(ecx, eax); output = savedRax; break;
                case WeaponProductionNativeOperation.StageLocalProduct:
                case WeaponProductionNativeOperation.RecoverLocalProduct: a.mov(ecx, eax); break;
                case WeaponProductionNativeOperation.IngredientThreshold: output = savedRcx; break;
                case WeaponProductionNativeOperation.IngredientFindWood: a.movzx(ecx, al); output = savedRax; break;
                case WeaponProductionNativeOperation.IngredientConsumeWood:
                case WeaponProductionNativeOperation.IngredientWorkshopDebit:
                case WeaponProductionNativeOperation.IngredientIronStock:
                case WeaponProductionNativeOperation.IngredientTannerStock:
                case WeaponProductionNativeOperation.IngredientTannerGrant: output = savedRax; break;
            }
            a.AddUnrestrictedCall(callback);
            if (output >= 0)
            {
                a.mov(r11, __qword_ptr[rsp + 128]);
                a.mov(eax, eax);
                a.mov(__qword_ptr[r11 + output], rax);
            }
            a.movdqu(xmm0, __xmmword_ptr[rsp + 32]); a.movdqu(xmm1, __xmmword_ptr[rsp + 48]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 64]); a.movdqu(xmm3, __xmmword_ptr[rsp + 80]);
            a.movdqu(xmm4, __xmmword_ptr[rsp + 96]); a.movdqu(xmm5, __xmmword_ptr[rsp + 112]);
            a.mov(rsp, __qword_ptr[rsp + 128]);
            a.pop(r11); a.pop(r10); a.pop(r9); a.pop(r8); a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
        }
    }
}
