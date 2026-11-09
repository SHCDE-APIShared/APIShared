using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

internal static partial class Program
{
    private static void TestWeaponProductionAdapters()
    {
        const int buildingId = 3;
        const int slots = WeaponProductionNativeDefinition.MaxBuildingId + 1;
        IntPtr state = Marshal.AllocHGlobal(slots + 16);
        IntPtr callbacks = state + slots + 4;
        IntPtr calls = callbacks + 4;
        ulong enabled = unchecked((ulong)(state + slots).ToInt64());
        ulong demand = unchecked((ulong)state.ToInt64());
        try
        {
            Marshal.Copy(new byte[slots + 16], 0, state, slots + 16);
            Put(WorkerProductionBonusNativeDefinition.CurrentUnitRva, UnitId);
            Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride +
                WeaponProductionNativeDefinition.LinkedBuildingOffset, buildingId);
            ulong imageBase = Address(0);
            using var filter = new Code(a => {
                a.mov(rax, unchecked((ulong)callbacks.ToInt64())); a.inc(__dword_ptr[rax]);
                a.xor(ecx, ecx); a.xor(edx, edx); a.xor(r8d, r8d); a.xor(r9d, r9d); a.xor(r10d, r10d); a.xor(r11d, r11d);
                for (int i = 0; i < 6; i++) a.pxor(Simd[i], Simd[i]);
                a.mov(eax, 7); a.ret();
            });
            ulong callback = unchecked((ulong)filter.Address.ToInt64());

            foreach (var role in WeaponProductionNativeDefinition.Roles)
            foreach (var site in role.Sites.Concat(WeaponProductionNativeDefinition.GetIngredientSites(role.Building)))
            {
                ulong entry = Address(site.Rva);
                Check(At(site.Rva, site.Bytes.Length).SequenceEqual(site.Bytes), "Weapon production span matches native image.");
                Check(At(site.Rva + site.Bytes.Length, site.Next.Length).SequenceEqual(site.Next), "Weapon production continuation matches native image.");
                using var hook = new X64InlineHook(entry, site.Bytes.Length);
                Check(hook.DisplacedByteCount == site.Bytes.Length, "Weapon production backend displaces complete audited span.");
                hook.Generate((a, original, back) => WeaponProductionNativeAssembly.Generate(a, original, back, site,
                    imageBase, enabled, demand, callback));
                byte[] expected = WeaponProductionNativeAssembly.ExpectedStub(site, imageBase,
                    unchecked((ulong)hook.StubAddress.ToInt64()), enabled, demand, callback);
                byte[] actual = new byte[expected.Length];
                Marshal.Copy(hook.StubAddress, actual, 0, actual.Length);
                Check(expected.SequenceEqual(actual), "Prepared weapon production stub matches production generator.");
            }

            // The Extender's context hook immediately precedes the armourer's material latch.
            const int extenderArmourerProduce = 0x13D6D3;
            using (var extender = new X64InlineHook(Address(extenderArmourerProduce), 14))
            {
                var armourer = WeaponProductionNativeDefinition.Roles.Single(role => role.Worker == eChimps.CHIMP_TYPE_ARMOURER);
                var latch = armourer.Sites.Single(site => site.Operation == WeaponProductionNativeOperation.LatchAfterCall);
                Check(extenderArmourerProduce + extender.DisplacedByteCount == latch.Rva,
                    "The material latch starts exactly after the Extender's live displacement.");
            }

            var fletcher = WeaponProductionNativeDefinition.Roles.Single(role => role.Worker == eChimps.CHIMP_TYPE_FLETCHER);
            foreach (var operation in new[] { WeaponProductionNativeOperation.Yield, WeaponProductionNativeOperation.Manufacturing,
                WeaponProductionNativeOperation.Deposit, WeaponProductionNativeOperation.Complete })
            {
                var site = fletcher.Sites.Single(item => item.Operation == operation);
                var original = WorkerProductionBonusNativeDefinition.Decode(site.Bytes, Address(site.Rva));
                int back = site.Rva + site.Bytes.Length;
                var exits = new Dictionary<int, int> { [back] = 1 };
                if (operation == WeaponProductionNativeOperation.Complete)
                    exits[(int)(original[original.Length - 1].NearBranchTarget - imageBase)] = 2;
                using var call = new Code(a => {
                    a.pushfq(); a.push(r11);
                    a.mov(r11, unchecked((ulong)calls.ToInt64())); a.inc(__dword_ptr[r11]);
                    a.pop(r11); a.popfq();
                    if (operation == WeaponProductionNativeOperation.Yield) a.mov(eax, edx);
                    else if (operation == WeaponProductionNativeOperation.Deposit) a.mov(eax, r9d);
                    else a.xor(eax, eax);
                    a.ret();
                });
                byte[] oldCall = site.CallTarget == 0 ? null : Read(site.CallTarget, 16);
                if (oldCall != null)
                {
                    var jump = new Assembler(64);
                    jump.AddUnrestrictedJmp(unchecked((ulong)call.Address.ToInt64()));
                    Copy(site.CallTarget, WorkerProductionBonusNativeAssembly.Assemble(jump, Address(site.CallTarget)));
                }
                try
                {
                    using var harness = new MachineHarness(a => WeaponProductionNativeAssembly.Generate(a, original, Address(back),
                        site, imageBase, enabled, demand, callback), exits);
                    foreach (var (gate, slot, cargo) in new[] { (0, 1, 0), (1, 0, 0), (1, 1, 0), (1, 1, 1) })
                    {
                        Marshal.WriteInt32(state + slots, gate);
                        Marshal.WriteByte(state, buildingId, (byte)slot);
                        Marshal.WriteInt32(calls, 0); Marshal.WriteInt32(callbacks, 0);
                        harness.ResetInput();
                        harness.Set(1, (ulong)buildingId * 0x32C);
                        harness.Set(2, 1);
                        harness.Set(3, (ulong)UnitId * UnitStride);
                        harness.Set(5, Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva));
                        harness.Set(6, 1);
                        harness.Set(7, 0);
                        harness.Set(12, Address(WorkerProductionBonusNativeDefinition.UnitManagerRva));
                        Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride +
                            WorkerProductionBonusNativeDefinition.CarriedOffset, cargo);
                        Word(WorkerProductionBonusNativeDefinition.BuildingManagerRva + buildingId * 0x32C + 0x2EA,
                            (int)eGoods.STORED_BOWS);
                        harness.Run();
                        bool active = gate != 0 && slot != 0 && (operation != WeaponProductionNativeOperation.Complete || cargo == 0);
                        Check(Marshal.ReadInt32(callbacks) == (active ? 1 : 0), "Only a demanded workshop reaches the callback.");
                        Check(Marshal.ReadInt32(calls) == (oldCall == null ? 0 : 1), "Original native call executes exactly once.");
                        Check(harness.Marker == (operation == WeaponProductionNativeOperation.Complete && cargo != 0 ? 2 : 1),
                            "Weapon production preserves the native continuation.");
                        switch (operation)
                        {
                            case WeaponProductionNativeOperation.Yield:
                                Check(harness.Get(0) == (active ? 7UL : 1UL), "Yield gets its selected base before the original call.");
                                harness.CheckPreserved(0, 2);
                                break;
                            case WeaponProductionNativeOperation.Manufacturing:
                                Check(harness.Get(0) == (active ? 7UL : 0UL), "Manufacturing completion is filtered after the original call.");
                                harness.CheckPreserved(0, 1, 2, 8);
                                break;
                            case WeaponProductionNativeOperation.Deposit:
                                Check(harness.Get(0) == (active ? 7UL : (ulong)eGoods.STORED_BOWS), "Deposit gets the latched good before the original call.");
                                harness.CheckPreserved(0, 1, 9);
                                break;
                            case WeaponProductionNativeOperation.Complete:
                                harness.CheckPreserved();
                                break;
                        }
                    }
                }
                finally { if (oldCall != null) Copy(site.CallTarget, oldCall); }
            }

            // The Tanner's neighbouring hook is a continuation, never a displaced call.
            // Verify the exact inputs that the ImprovedHunters fixture consumes next.
            var tanner = WeaponProductionNativeDefinition.Roles.Single(role => role.Worker == eChimps.CHIMP_TYPE_TANNER);
            foreach (var site in tanner.Sites.Where(item => item.FilterBeforeAdjacentCall))
            {
                bool yield = site.Operation == WeaponProductionNativeOperation.Yield;
                int back = site.Rva + site.Bytes.Length;
                var original = WorkerProductionBonusNativeDefinition.Decode(site.Bytes, Address(site.Rva));
                using var harness = new MachineHarness(a => WeaponProductionNativeAssembly.Generate(a, original, Address(back),
                    site, imageBase, enabled, demand, callback), new Dictionary<int, int> { [back] = 1 });
                foreach (int gate in new[] { 0, 1 })
                {
                    Marshal.WriteInt32(state + slots, gate);
                    Marshal.WriteByte(state, buildingId, 1);
                    harness.ResetInput();
                    harness.Set(1, (ulong)(yield ? UnitId : UnitId * UnitStride));
                    harness.Set(7, 0);
                    harness.Set(9, (int)eGoods.STORED_LEATHER_ARMOUR);
                    harness.Set(12, Address(WorkerProductionBonusNativeDefinition.UnitManagerRva));
                    harness.Set(13, 1);
                    Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + 0x98E, 5);
                    harness.Run();
                    Check(harness.Marker == 1, "Tanner always enters the adjoining legacy or original call block.");
                    Check(harness.Get(yield ? 2 : 9) == (gate != 0 ? 7UL : yield ? 1UL : (ulong)eGoods.STORED_LEATHER_ARMOUR),
                        "Tanner hands the selected input to the adjoining call block.");
                    Check(harness.Get(2) == (yield ? (gate != 0 ? 7UL : 1UL) : 5UL), "Tanner leaves destination ID unchanged.");
                    harness.CheckPreserved(yield ? new[] { 2, 3, 8 } : new[] { 2, 9 });
                }
            }

            TestWeaponProductionWoodAdapters(state, enabled, buildingId);
            TestWeaponProductionLocalIngredients(state, enabled, buildingId);
        }
        finally { Marshal.FreeHGlobal(state); }
    }
}
