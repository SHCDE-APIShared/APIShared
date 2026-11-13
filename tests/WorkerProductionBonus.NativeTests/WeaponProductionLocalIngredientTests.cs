using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using RedBird.X64.Extensions;
using SHCDESE.Interop;
using static APIShared.Economy.WeaponProductionNativeDefinition;
using static Iced.Intel.AssemblerRegisters;

internal static partial class Program
{
    private static void TestWeaponProductionLocalIngredients(IntPtr demand, ulong enabled, int buildingId)
    {
        IntPtr state = Marshal.AllocHGlobal(28);
        byte[] old = Read(AddGoodRva, 16);
        byte[] oldSearch = Read(FindStockRva, 16);
        try
        {
            ulong scratch = unchecked((ulong)state.ToInt64());
            using var filter = new Code(a => {
                a.mov(rax, scratch); a.cmp(ecx, -1);
                var normal = a.CreateLabel();
                a.jne(normal); a.inc(__dword_ptr[rax + 24]);
                a.Label(ref normal); a.mov(eax, __dword_ptr[rax]); a.ret();
            });
            using var helper = new Code(a => {
                a.mov(rax, scratch); a.inc(__dword_ptr[rax + 8]);
                a.mov(r10d, __dword_ptr[rsp + 0x28]); a.mov(__dword_ptr[rax + 12], r10d);
                a.mov(r10d, __dword_ptr[rsp + 0x30]); a.mov(__dword_ptr[rax + 16], r10d);
                a.mov(__dword_ptr[rax + 20], r9d); a.mov(eax, __dword_ptr[rax + 4]); a.ret();
            });
            var jump = new Assembler(64);
            jump.AddUnrestrictedJmp(unchecked((ulong)helper.Address.ToInt64()));
            Copy(AddGoodRva, WorkerProductionBonusNativeAssembly.Assemble(jump, Address(AddGoodRva)));
            var searchJump = new Assembler(64);
            searchJump.AddUnrestrictedJmp(unchecked((ulong)helper.Address.ToInt64()));
            Copy(FindStockRva, WorkerProductionBonusNativeAssembly.Assemble(searchJump, Address(FindStockRva)));

            ulong imageBase = Address(0);
            ulong callback = unchecked((ulong)filter.Address.ToInt64());
            Marshal.WriteByte(demand, buildingId, 1);
            foreach (var role in Roles)
            foreach (var site in GetIngredientSites(role.Building).Where(item =>
                item.Operation is WeaponProductionNativeOperation.IngredientWorkshopDebit or
                    WeaponProductionNativeOperation.IngredientIronStock or WeaponProductionNativeOperation.IngredientTannerStock or
                    WeaponProductionNativeOperation.IngredientTannerGrant))
            {
                bool tanner = role.Worker == eChimps.CHIMP_TYPE_TANNER;
                bool armourer = role.Worker == eChimps.CHIMP_TYPE_ARMOURER;
                bool cowGrant = site.Operation == WeaponProductionNativeOperation.IngredientTannerGrant;
                int back = site.Rva + site.Bytes.Length;
                int retry = tanner ? TannerIngredientRetryRva : armourer ? ArmourerIngredientRetryRva : SmithIngredientRetryRva;
                var exits = new Dictionary<int, int> { [back] = 1 };
                if (site.AlternativeRva != 0) exits[site.AlternativeRva] = 2;
                if (site.Operation == WeaponProductionNativeOperation.IngredientWorkshopDebit) exits[retry] = 3;
                var original = WorkerProductionBonusNativeDefinition.Decode(site.Bytes, Address(site.Rva));
                using var harness = new MachineHarness(a => {
                    a.mov(__dword_ptr[rsp + 0x20], cowGrant ? 3 : -1);
                    a.mov(__dword_ptr[rsp + 0x28], cowGrant ? 3 : 48);
                    WeaponProductionNativeAssembly.Generate(a, original, Address(back), site, imageBase, enabled,
                        unchecked((ulong)demand.ToInt64()), callback);
                }, exits);

                void Run(bool active, int selected, int helperResult = 1, int stock = 8)
                {
                    const int stride = 0x32C;
                    harness.ResetInput();
                    Marshal.WriteInt32(new IntPtr(unchecked((long)enabled)), active ? 1 : 0);
                    Marshal.Copy(new[] { selected, helperResult, 0, 0, 0, 0, 0 }, 0, state, 7);
                    ulong manager = Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva);
                    harness.Set(0, (ulong)buildingId);
                    harness.Set(1, cowGrant ? manager : (ulong)buildingId * stride);
                    harness.Set(2, (ulong)buildingId);
                    harness.Set(3, manager);
                    harness.Set(6, manager);
                    harness.Set(7, 0);
                    harness.Set(9, (ulong)eGoods.STORED_COW_HIDES);
                    harness.Set(13, 0);
                    Put(WorkerProductionBonusNativeDefinition.BuildingManagerRva + buildingId * stride + (tanner ? 0x190 : 0x194), stock);
                    harness.Run();
                }

                int Value(int offset) => Marshal.ReadInt32(state + offset);
                if (cowGrant)
                {
                    Run(false, 7);
                    Check(harness.Marker == 1 && Value(8) == 1 && Value(12) == 3 && Value(16) == 3,
                        "The dormant Tanner grant keeps both native cow arguments.");
                    Run(true, 7);
                    Check(harness.Marker == 1 && Value(8) == 1 && Value(12) == 3 && Value(16) == 7 &&
                        Value(20) == (int)eGoods.STORED_COW_HIDES, "A Tanner override adjusts capacity while the cow still grants three hides.");
                }
                else if (site.Operation == WeaponProductionNativeOperation.IngredientTannerStock)
                {
                    Run(true, 0);
                    Check(harness.Marker == 2 && Value(8) == 0, "Insufficient Tanner stock reaches the original cow acquisition branch.");
                    Run(true, 1);
                    Check(harness.Marker == 1 && Value(8) == 0, "Enough Tanner stock bypasses acquisition.");
                }
                else if (site.Operation == WeaponProductionNativeOperation.IngredientIronStock)
                {
                    Run(false, 1);
                    Check(harness.Marker == 1 && Value(8) == 1, "Dormant iron-buffer hook uses the original search.");
                    Run(true, 1, stock: 0);
                    Check(harness.Marker == 1 && Value(8) == 1, "An empty iron buffer bypasses the managed check.");
                    Run(true, 0);
                    Check(harness.Marker == 1 && Value(8) == 1, "Insufficient local iron uses the original search.");
                    Run(true, 1);
                    Check(harness.Marker == 2 && Value(8) == 0 && site.AlternativeRva == retry,
                        "Enough local iron bypasses the stockpile search and fake drop-off event.");
                }
                else
                {
                    Run(false, 5);
                    Check(harness.Marker == 1 && Value(8) == 1 && Value(12) == -1 && Value(16) == 48,
                        "A dormant ingredient hook charges exactly one native ingredient.");
                    Run(true, 5);
                    Check(harness.Marker == 1 && Value(8) == 1 && Value(12) == -5 && Value(16) == 48 &&
                        Value(20) == (int)(tanner ? eGoods.STORED_COW_HIDES : eGoods.STORED_IRON_INGOTS),
                        "The workshop helper consumes exactly the selected amount of the native good.");
                    Run(true, -1);
                    Check(harness.Marker == 2 && Value(8) == 0, "An eligible buffered product reaches its native recovery branch without a debit.");
                    Run(true, 0);
                    Check(harness.Marker == 3 && Value(8) == 0, "A shortage with no recoverable product returns to the worker loop without a debit.");
                    Run(true, 5, helperResult: 0);
                    Check(harness.Marker == 3 && Value(8) == 1 && Value(24) == 1,
                        "Failed native debit is reported and cannot advance manufacturing.");
                }
            }
        }
        finally { Copy(AddGoodRva, old); Copy(FindStockRva, oldSearch); Marshal.FreeHGlobal(state); }
    }
}
