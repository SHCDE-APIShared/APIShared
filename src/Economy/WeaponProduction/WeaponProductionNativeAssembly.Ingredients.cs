using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using static APIShared.Economy.WeaponProductionNativeDefinition;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal static partial class WeaponProductionNativeAssembly
    {
        private static bool TryGenerateWorkshopIngredient(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            WeaponProductionNativeSite site, ulong image, ulong enabled, ulong buildingDemand, ulong callback)
        {
            switch (site.Operation)
            {
                case WeaponProductionNativeOperation.IngredientIronStock:
                    EmitIronStock(a, original, back, site, image, enabled, callback);
                    return true;
                case WeaponProductionNativeOperation.IngredientWorkshopDebit:
                    EmitWorkshopDebit(a, original, back, site, image, enabled, buildingDemand, callback);
                    return true;
                case WeaponProductionNativeOperation.IngredientTannerStock:
                    EmitTannerStock(a, original, back, site, image, enabled, buildingDemand, callback);
                    return true;
                case WeaponProductionNativeOperation.IngredientTannerGrant:
                    EmitTannerGrant(a, original, back, site, image, enabled, callback);
                    return true;
                default:
                    return false;
            }
        }

        private static void EmitIronStock(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            WeaponProductionNativeSite site, ulong image, ulong enabled, ulong callback)
        {
            int call = site.Rva == SmithIngredientStockRva ? 1 : 3;
            if (original.Length <= call || !Calls(original[call], image + FindStockRva))
                throw new InvalidOperationException("The iron workshop stockpile lookup differs.");

            var dormant = a.CreateLabel();
            var findIron = a.CreateLabel();
            var originalLookup = a.CreateLabel();
            EmitPositiveLocalStockCheck(a, dormant, image, enabled, IronManagerStockOffset);
            RestoreDemandRegisters(a);
            a.push(rax);
            EmitWorkshopIngredientFilter(a, site.Operation, callback, 0);
            a.test(eax, eax); a.je(findIron);
            a.pop(rax);
            // The worker is already at the work tile. There is no new material
            // grant or drop-off event to replay before leaving this update.
            a.AddUnrestrictedJmp(image + (uint)site.AlternativeRva);
            a.Label(ref findIron);
            a.pop(rax); a.jmp(originalLookup);
            a.Label(ref dormant);
            RestoreDemandRegisters(a);
            a.Label(ref originalLookup);
            WorkerProductionBonusNativeAssembly.Replay(a, original);
            a.AddUnrestrictedJmp(back);
        }

        private static void EmitWorkshopDebit(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            WeaponProductionNativeSite site, ulong image, ulong enabled, ulong buildingDemand, ulong callback)
        {
            bool tanner = site.Rva == TannerIngredientDebitRva;
            bool armourer = site.Rva == ArmourerIngredientDebitRva;
            int branch = armourer ? 3 : 2;
            int call = original.Length - 1;
            if (original.Length < 5 || original[1].Mnemonic != Mnemonic.Cmp ||
                original[branch].Mnemonic != Mnemonic.Jle ||
                original[branch].NearBranchTarget != image + (uint)site.AlternativeRva ||
                !Calls(original[call], image + AddGoodRva))
                throw new InvalidOperationException("The workshop ingredient debit path differs.");

            ulong retry = image + (uint)(tanner ? TannerIngredientRetryRva :
                armourer ? ArmourerIngredientRetryRva : SmithIngredientRetryRva);
            var dormant = a.CreateLabel();
            var vanilla = a.CreateLabel();
            var vanillaReplay = a.CreateLabel();
            var recover = a.CreateLabel();
            var returnToWorker = a.CreateLabel();
            var failedDebit = a.CreateLabel();
            WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(0, 1));
            EmitDemandCheck(a, dormant, site.Operation, image, enabled, buildingDemand);
            RestoreDemandRegisters(a);
            a.push(rax);
            EmitWorkshopIngredientFilter(a, site.Operation, callback, 0);
            a.cmp(eax, IngredientVanilla); a.je(vanilla);
            a.cmp(eax, IngredientRecover); a.je(recover);
            a.cmp(eax, IngredientRetry); a.je(returnToWorker);
            // The original first stack argument is the signed stock delta.
            a.neg(eax); a.mov(__dword_ptr[rsp + 0x28], eax); a.pop(rax);
            for (int i = 2; i < call; i++)
            {
                if (i == branch || tanner && original[i].IP == image + TannerNativeDeltaRva) continue;
                WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(i, 1));
            }
            WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(call, 1));
            a.test(eax, eax); a.je(failedDebit);
            a.AddUnrestrictedJmp(back);

            a.Label(ref failedDebit);
            EmitWorkshopIngredientFilter(a, site.Operation, callback, -1);
            a.AddUnrestrictedJmp(retry);

            a.Label(ref returnToWorker);
            a.pop(rax);
            a.AddUnrestrictedJmp(retry);

            a.Label(ref recover);
            a.pop(rax);
            // The armourer moves its manager pointer into RCX between CMP and JLE.
            WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(2, branch - 2));
            a.AddUnrestrictedJmp(image + (uint)site.AlternativeRva);

            a.Label(ref vanilla);
            a.pop(rax); a.jmp(vanillaReplay);
            a.Label(ref dormant);
            RestoreDemandRegisters(a);
            a.Label(ref vanillaReplay);
            WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(1));
            a.AddUnrestrictedJmp(back);
        }

        private static void EmitTannerStock(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            WeaponProductionNativeSite site, ulong image, ulong enabled, ulong buildingDemand, ulong callback)
        {
            if (original.Length != 3 || original[0].Mnemonic != Mnemonic.Imul || original[1].Mnemonic != Mnemonic.Cmp ||
                original[2].Mnemonic != Mnemonic.Jle || original[2].NearBranchTarget != image + (uint)site.AlternativeRva)
                throw new InvalidOperationException("The tanner stock check differs.");

            var dormant = a.CreateLabel();
            var vanilla = a.CreateLabel();
            var vanillaReplay = a.CreateLabel();
            var findCow = a.CreateLabel();
            WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(0, 1));
            EmitDemandCheck(a, dormant, site.Operation, image, enabled, buildingDemand);
            RestoreDemandRegisters(a);
            a.push(rax);
            EmitWorkshopIngredientFilter(a, site.Operation, callback, 0);
            a.cmp(eax, IngredientVanilla); a.je(vanilla);
            a.test(eax, eax); a.je(findCow);
            a.pop(rax); a.AddUnrestrictedJmp(back);
            a.Label(ref findCow);
            a.pop(rax); a.AddUnrestrictedJmp(image + (uint)site.AlternativeRva);

            a.Label(ref vanilla);
            a.pop(rax); a.jmp(vanillaReplay);
            a.Label(ref dormant);
            RestoreDemandRegisters(a);
            a.Label(ref vanillaReplay);
            WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(1));
            a.AddUnrestrictedJmp(back);
        }

        private static void EmitTannerGrant(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            WeaponProductionNativeSite site, ulong image, ulong enabled, ulong callback)
        {
            if (original.Length != 3 || !Calls(original[2], image + AddGoodRva))
                throw new InvalidOperationException("The tanner cow grant differs.");

            var dormant = a.CreateLabel();
            var grant = a.CreateLabel();
            var defaultCapacity = a.CreateLabel();
            WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(0, 2));
            // A cow hunt started for the previous recipe can finish after a recipe switch.
            // Its grant depends on actual hide stock and the admitted world's recipes.
            EmitPositiveLocalStockCheck(a, dormant, image, enabled, TannerManagerStockOffset);
            RestoreDemandRegisters(a);
            a.push(rax);
            EmitWorkshopIngredientFilter(a, site.Operation, callback, 0);
            a.test(eax, eax); a.je(defaultCapacity);
            a.mov(__dword_ptr[rsp + 0x30], eax);
            a.Label(ref defaultCapacity);
            a.pop(rax); a.jmp(grant);
            a.Label(ref dormant);
            RestoreDemandRegisters(a);
            a.Label(ref grant);
            WorkerProductionBonusNativeAssembly.Replay(a, original.Slice(2));
            a.AddUnrestrictedJmp(back);
        }

        private static void EmitPositiveLocalStockCheck(Assembler a, Label dormant, ulong image, ulong enabled, int stockOffset)
        {
            a.pushfq(); a.push(rax); a.push(rcx);
            a.mov(rax, enabled); a.cmp(__dword_ptr[rax], 0); a.je(dormant);
            a.mov(rax, image + WorkerProductionBonusNativeDefinition.CurrentUnitRva);
            a.mov(eax, __dword_ptr[rax]);
            a.cmp(eax, 1); a.jl(dormant);
            a.cmp(eax, MaxUnitId); a.jg(dormant);
            a.imul(rax, rax, WorkerProductionBonusNativeDefinition.UnitStride);
            a.mov(rcx, image + WorkerProductionBonusNativeDefinition.UnitManagerRva);
            a.movsx(eax, __word_ptr[rcx + rax + LinkedBuildingOffset]);
            a.cmp(eax, 1); a.jl(dormant);
            a.cmp(eax, MaxBuildingId); a.jg(dormant);
            a.imul(rax, rax, BuildingStride);
            a.mov(rcx, image + WorkerProductionBonusNativeDefinition.BuildingManagerRva);
            a.cmp(__dword_ptr[rcx + rax + stockOffset], 0); a.jle(dormant);
        }

        private static void EmitWorkshopIngredientFilter(Assembler a, WeaponProductionNativeOperation operation, ulong callback, int phase)
        {
            a.push(rcx); a.mov(ecx, phase);
            EmitFilter(a, operation, callback);
            a.pop(rcx);
        }

        private static bool Calls(Instruction instruction, ulong target) =>
            instruction.Code == Code.Call_rel32_64 && instruction.NearBranchTarget == target;
    }
}
