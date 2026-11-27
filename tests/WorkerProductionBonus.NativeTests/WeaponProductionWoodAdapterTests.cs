using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using RedBird.X64.Extensions;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

internal static partial class Program
{
    private static void TestWeaponProductionWoodAdapters(IntPtr demand, ulong enabled, int buildingId)
    {
        IntPtr result = new IntPtr(unchecked((long)enabled)) + 4;
        IntPtr searches = result + 4;
        IntPtr argument = searches + 4;
        using var filter = new Code(a => {
            a.mov(rax, unchecked((ulong)argument.ToInt64())); a.mov(__dword_ptr[rax], ecx);
            a.mov(rax, unchecked((ulong)result.ToInt64())); a.mov(eax, __dword_ptr[rax]); a.ret();
        });
        using var stockSearch = new Code(a => {
            a.mov(rax, unchecked((ulong)searches.ToInt64())); a.inc(__dword_ptr[rax]);
            a.mov(eax, 42); a.ret();
        });
        int find = WeaponProductionNativeDefinition.FindStockRva;
        byte[] old = Read(find, 16);
        var jump = new Assembler(64);
        jump.AddUnrestrictedJmp(unchecked((ulong)stockSearch.Address.ToInt64()));
        Copy(find, WorkerProductionBonusNativeAssembly.Assemble(jump, Address(find)));

        try
        {
            ulong imageBase = Address(0);
            ulong callback = unchecked((ulong)filter.Address.ToInt64());
            const int buildingStride = 0x32C;
            int plankStock = WorkerProductionBonusNativeDefinition.BuildingManagerRva + buildingId * buildingStride +
                WeaponProductionNativeDefinition.WorkshopPlanksOffset;
            Marshal.WriteByte(demand, buildingId, 1);

            foreach (eStructs workshop in new[] { eStructs.STRUCT_FLETCHERS_WORKSHOP, eStructs.STRUCT_POLETURNERS_WORKSHOP })
            foreach (var site in WeaponProductionNativeDefinition.GetIngredientSites(workshop))
            {
                bool fletcher = workshop == eStructs.STRUCT_FLETCHERS_WORKSHOP;
                var original = WorkerProductionBonusNativeDefinition.Decode(site.Bytes, Address(site.Rva));
                int back = site.Rva + site.Bytes.Length;
                var exits = new Dictionary<int, int> { [back] = 1 };
                if (site.AlternativeRva != 0) exits[site.AlternativeRva] = 2;
                using var harness = new MachineHarness(a => WeaponProductionNativeAssembly.Generate(a, original, Address(back),
                    site, imageBase, enabled, unchecked((ulong)demand.ToInt64()), callback), exits);

                void Run(bool active, int filterResult, int stock)
                {
                    harness.ResetInput();
                    Marshal.WriteInt32(new IntPtr(unchecked((long)enabled)), active ? 1 : 0);
                    Marshal.WriteInt32(result, filterResult);
                    Marshal.WriteInt32(searches, 0);
                    Marshal.WriteInt32(argument, -1);
                    Put(plankStock, stock);
                    ulong manager = Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva);
                    harness.Set(1, (ulong)buildingId * buildingStride);
                    harness.Set(2, (ulong)UnitId * UnitStride);
                    harness.Set(3, (ulong)UnitId * UnitStride);
                    harness.Set(fletcher ? 5 : 7, manager);
                    harness.Set(12, Address(WorkerProductionBonusNativeDefinition.UnitManagerRva));
                    harness.Set(6, 0);
                    harness.Set(13, 2);
                    if (site.Operation == WeaponProductionNativeOperation.IngredientThreshold)
                        harness.Set(1, fletcher ? 3UL : 2UL);
                    if (site.Operation == WeaponProductionNativeOperation.IngredientFindWood)
                        harness.Set(0, fletcher ? 3UL : 2UL);
                    harness.Run();
                }

                switch (site.Operation)
                {
                    case WeaponProductionNativeOperation.IngredientThreshold:
                        Run(false, 5, 4);
                        Check((harness.Flags & 0x80) == 0, "Vanilla accepts stock at its own workshop threshold.");
                        Run(true, 5, 4);
                        Check((harness.Flags & 0x80) != 0, "The original comparison rejects stock below the custom requirement.");
                        Run(true, 5, 5);
                        Check((harness.Flags & 0x40) != 0, "The original comparison accepts exactly the custom requirement.");
                        harness.CheckPreserved(0, 1, fletcher ? 9 : 8);
                        break;

                    case WeaponProductionNativeOperation.IngredientConsumeWood:
                        foreach (var (active, remainder, stock, expected, marker) in new[]
                        {
                            (false, 3, 8, 0, 1), (true, WeaponProductionNativeDefinition.IngredientVanilla, 8, 0, 1),
                            (true, -1, 4, 4, 2), (true, 0, 5, 0, 1), (true, 3, 8, 3, 1)
                        })
                        {
                            Run(active, remainder, stock);
                            Check(harness.Marker == marker && Marshal.ReadInt32(image + plankStock) == expected,
                                "Workshop debit preserves vanilla, exact custom subtraction and the no-stock continuation.");
                            harness.CheckPreserved(fletcher ? new[] { 0, 7, 8 } : new[] { 0 });
                        }
                        break;

                    case WeaponProductionNativeOperation.IngredientFindWood:
                        Run(true, 0, 0);
                        Check(harness.Marker == 1 && Marshal.ReadInt32(searches) == 1 && harness.Get(0) == 42 &&
                            Marshal.ReadInt32(argument) == -1, "Empty workshops run the original search without a managed call.");
                        Run(true, 0, 1);
                        Check(harness.Marker == 1 && Marshal.ReadInt32(searches) == 1,
                            "Insufficient buffered wood runs the original single-plank search once.");
                        Run(false, 1, 8);
                        Check(harness.Marker == 1 && Marshal.ReadInt32(searches) == 1,
                            "Dormant ingredient hooks retain the normal stockpile search.");
                        Marshal.WriteByte(demand, buildingId, 0); // Selection has returned to vanilla.
                        Run(true, 1, 8);
                        Marshal.WriteByte(demand, buildingId, 1);
                        Check(harness.Marker == 2 && Marshal.ReadInt32(searches) == 0,
                            "Enough buffered wood is accessible after a switch back to vanilla.");
                        Check(Marshal.ReadInt32(argument) == (fletcher ? 3 : 2),
                            "The buffer gate passes the computed vanilla ingredient threshold.");
                        if (site.Rva == WeaponProductionNativeDefinition.FletcherFindWoodAtHomeRva)
                            Check(harness.Get(10) == (ulong)UnitId, "Fletcher restores the unit ID live after drop-off.");
                        Check(harness.Get(2) == (ulong)UnitId * UnitStride,
                            "The alternate continuation receives the native worker offset.");
                        break;
                }
            }
        }
        finally { Copy(find, old); }
    }
}
