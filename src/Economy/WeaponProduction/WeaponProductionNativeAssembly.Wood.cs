using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal static partial class WeaponProductionNativeAssembly
    {
        private static bool TryBranchForWoodIngredient(Assembler a, WeaponProductionNativeSite site, Label vanilla,
            ulong image, ulong back)
        {
            ulong alternative = image + (uint)site.AlternativeRva;
            switch (site.Operation)
            {
                case WeaponProductionNativeOperation.IngredientFindWood:
                    a.test(eax, eax); a.je(vanilla);
                    // At home, resume after the real drop-off event with the two native
                    // record indexes it would have set. After delivery, walk home first.
                    if (site.Rva == WeaponProductionNativeDefinition.FletcherFindWoodAtHomeRva ||
                        site.Rva == WeaponProductionNativeDefinition.PoleturnerFindWoodAtHomeRva)
                    {
                        a.mov(rdx, image + WorkerProductionBonusNativeDefinition.CurrentUnitRva);
                        a.movsxd(rdx, __dword_ptr[rdx]);
                        if (site.Rva == WeaponProductionNativeDefinition.FletcherFindWoodAtHomeRva) a.mov(r10, rdx);
                        a.imul(rdx, rdx, WorkerProductionBonusNativeDefinition.UnitStride);
                    }
                    a.AddUnrestrictedJmp(alternative);
                    return true;

                case WeaponProductionNativeOperation.IngredientConsumeWood:
                    var shortage = a.CreateLabel();
                    a.cmp(eax, WeaponProductionNativeDefinition.IngredientVanilla); a.je(vanilla);
                    a.test(eax, eax); a.js(shortage);
                    int planks = WeaponProductionNativeDefinition.WorkshopPlanksOffset;
                    if (site.Rva == WeaponProductionNativeDefinition.FletcherConsumeWoodRva)
                    {
                        a.xor(edi, edi);
                        a.mov(r8d, r13d);
                        a.mov(__dword_ptr[rcx + rbp + planks], eax);
                    }
                    else a.mov(__dword_ptr[rcx + rdi + planks], eax);
                    a.AddUnrestrictedJmp(back);
                    a.Label(ref shortage);
                    a.AddUnrestrictedJmp(alternative);
                    return true;

                default: return false;
            }
        }
    }
}
