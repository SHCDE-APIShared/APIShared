using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using RedBird.X64.Extensions;

internal static partial class Program
{
    private static readonly Register[] RegisterOrder = { Register.RAX, Register.RCX, Register.RDX, Register.RBX, Register.RSP, Register.RBP, Register.RSI, Register.RDI, Register.R8, Register.R9, Register.R10, Register.R11, Register.R12, Register.R13, Register.R14, Register.R15 };
    private static partial void TestBoundaryGenerators(IntPtr percent)
    {
        Put(WorkerProductionBonusNativeDefinition.CurrentUnitRva, UnitId);
        Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + 0x98E, 7);
        Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + 0x990, 3);
        foreach (var site in WorkerProductionBonusNativeDefinition.Sites)
        {
            var original = WorkerProductionBonusNativeDefinition.Decode(site.OriginalBytes, Address(site.Rva));
            var exits = new Dictionary<int, int> { [site.Rva + site.Length] = 1 };
            if (site.FooterRva != 0) exits[site.FooterRva] = 2;
            foreach (var instruction in original)
                if (instruction.FlowControl == FlowControl.ConditionalBranch)
                {
                    int target = checked((int)(instruction.NearBranchTarget - unchecked((ulong)image.ToInt64())));
                    if (!exits.ContainsKey(target)) exits[target] = 2;
                }
            if (site.Kind == WorkerProductionBonusNativeKind.Render)
                exits[WorkerProductionBonusNativeDefinition.RenderRejectRva] = 2;
            using var generated = new MachineHarness(a => Generate(a, original, Address(site.Rva + site.Length), site, percent), exits);
            if (site.Kind == WorkerProductionBonusNativeKind.Core)
            {
                foreach (int value in new[] { -100, -50, 0, 50, 1000 })
                foreach (int mode in new[] { 0, 99 })
                foreach (int eligible in new[] { 0, 1 })
                {
                    Marshal.WriteInt32(percent, value); Put(ModeRva, mode);
                    generated.ResetInput(); generated.Set(0, 150); generated.Set(8, (uint)eligible);
                    generated.Run();
                    Check(generated.Marker == 1 && generated.Get(0) == (ulong)(150 + (mode != 0 && eligible != 0 ? value : 0)),
                        $"Core modifies only the eligible coefficient: percent={value},mode={mode},flag={eligible},marker={generated.Marker},rax={generated.Get(0)}.");
                    generated.CheckPreserved(0);
                }
                continue;
            }

            foreach (int value in new[] { -50, 0, 50 })
            foreach (int mode in new[] { 0, 99 })
            foreach (int amount in new[] { 0, 1, 4, 7, 8, 12 })
            {
                Marshal.WriteInt32(percent, value); Put(ModeRva, mode);
                Marshal.WriteInt32(percent, sizeof(int), 1);
                Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + WorkerProductionBonusNativeDefinition.BonusYieldOffset, 0);
                Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + WorkerProductionBonusNativeDefinition.CarriedOffset, amount);
                generated.ResetInput();
                PrepareOriginalInputs(generated, site, amount);
                generated.Run();
                bool active = value < 0 && mode != 0;
                if (site.Kind == WorkerProductionBonusNativeKind.ZeroCompletion)
                {
                    // This phase is reached only after work has actually completed.
                    // Any supported upstream Pre (including UnitId redirects) can make
                    // its physical cargo zero without leaving native extra provenance.
                    bool completed = amount == 0;
                    Check(generated.Marker == (completed ? 2 : 1), "Every ready zero-output completion finishes its native work cycle: " + site.Name);
                    if (completed)
                    {
                        var changed = new List<int> { 0, 2 };
                        // The literal contract table is independent of the generator's own map;
                        // it comes from the downstream native footer reads, not private names.
                        foreach (var expected in ExpectedFooterRegisters(site.Name))
                        { Check(generated.Get(expected.Key) == expected.Value, "Actual footer register input: " + site.Name + "/" + RegisterOrder[expected.Key]); changed.Add(expected.Key); }
                        generated.CheckPreserved(changed.ToArray());
                    }
                    else CheckOriginalReplay(generated, site, amount);
                }
                else if (site.Kind == WorkerProductionBonusNativeKind.OxDeparture)
                {
                    Check(generated.Marker == (amount < (active ? 1 : 8) ? 2 : 1), "Ox departure threshold permits all reduced real outputs: " + site.Name);
                    generated.CheckPreserved();
                }
                else if (site.Kind == WorkerProductionBonusNativeKind.Reserve)
                {
                    Check(generated.Marker == (active && amount == 0 ? 2 : 1), "Zero reserve cannot invent another stone load.");
                    generated.CheckPreserved();
                }
                else if (site.Kind == WorkerProductionBonusNativeKind.Render)
                {
                    Check(generated.Marker == (amount == 0 ? 2 : 1), "Ordinary render zero/nonzero replay.");
                    generated.CheckPreserved(amount == 0 ? Array.Empty<int>() : new[] { 2 });
                }
            }
            if (site.Kind != WorkerProductionBonusNativeKind.Render)
            {
                // A reduced record survives a save even when the current override has
                // been disabled or the new session has native mode zero. Partial hook
                // installation must retain exact Vanilla behavior for that same record.
                foreach (int ready in new[] { 0, 1 })
                foreach (int value in new[] { -50, 50 })
                foreach (int mode in new[] { 0, 99 })
                foreach (int extra in new[] { -1, 0 })
                foreach (int amount in new[] { 0, 1, 4, 7, 8 })
                {
                    Marshal.WriteInt32(percent, value); Marshal.WriteInt32(percent, sizeof(int), ready); Put(ModeRva, mode);
                    Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + WorkerProductionBonusNativeDefinition.CarriedOffset, amount);
                    Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + WorkerProductionBonusNativeDefinition.BonusYieldOffset, extra);
                    generated.ResetInput(); PrepareOriginalInputs(generated, site, amount); generated.Run();
                    bool active = ready != 0 && (extra < 0 || (value < 0 && mode != 0));
                    bool alternate = site.Kind == WorkerProductionBonusNativeKind.ZeroCompletion ? ready != 0 && amount == 0 :
                        site.Kind == WorkerProductionBonusNativeKind.OxDeparture ? amount < (active ? 1 : 8) : active && amount == 0;
                    Check(generated.Marker == (alternate ? 2 : 1), "Saved-negative provenance and publication gates: " + site.Name);
                    if (site.Kind == WorkerProductionBonusNativeKind.ZeroCompletion)
                    {
                        if (!alternate) CheckOriginalReplay(generated, site, amount);
                        else
                        {
                            var changed = new List<int> { 0, 2 };
                            foreach (var expected in ExpectedFooterRegisters(site.Name))
                            { Check(generated.Get(expected.Key) == expected.Value, "Saved-negative footer inputs: " + site.Name); changed.Add(expected.Key); }
                            generated.CheckPreserved(changed.ToArray());
                        }
                    }
                    else generated.CheckPreserved();
                }
            }
            if (site.Kind == WorkerProductionBonusNativeKind.Reserve)
            {
                // The handler's current unit is the driver, while original RBX addresses
                // the other unit (the Ox). Only the latter record establishes history.
                foreach (int driverExtra in new[] { -1, 0 })
                foreach (int oxExtra in new[] { -1, 0 })
                {
                    Marshal.WriteInt32(percent, 50); Marshal.WriteInt32(percent, sizeof(int), 1); Put(ModeRva, 0);
                    Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + WorkerProductionBonusNativeDefinition.BonusYieldOffset, driverExtra);
                    Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + 2 * UnitStride + WorkerProductionBonusNativeDefinition.BonusYieldOffset, oxExtra);
                    Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + 2 * UnitStride + WorkerProductionBonusNativeDefinition.CarriedOffset, 0);
                    generated.ResetInput(); PrepareOriginalInputs(generated, site, 0);
                    generated.Set(3, Address(WorkerProductionBonusNativeDefinition.UnitManagerRva) + 2 * UnitStride);
                    generated.Run();
                    Check(generated.Marker == (oxExtra < 0 ? 2 : 1), "Reserve history uses the Ox record, independently of the current driver.");
                    generated.CheckPreserved();
                }
            }
            if (site.Kind == WorkerProductionBonusNativeKind.Render)
            {
                foreach (int ready in new[] { 0, 1 })
                foreach (int value in new[] { -50, 0, 50 })
                foreach (int mode in new[] { 0, 99 })
                foreach (int extra in new[] { -100, -1, 0, 1, 4, 8 })
                {
                    Marshal.WriteInt32(percent, value); Marshal.WriteInt32(percent, sizeof(int), ready); Put(ModeRva, mode);
                    generated.ResetInput(); PrepareOriginalInputs(generated, site, extra);
                    generated.Run();
                    bool rejected = extra == 0 || (ready != 0 && extra < 0);
                    Check(generated.Marker == (rejected ? 2 : 1), "Signed negative extra never appears as positive visual bonus.");
                    generated.CheckPreserved(rejected ? Array.Empty<int>() : new[] { 2 });
                    // Rejection restores entry flags; fallback replays the original CMP.
                    if (ready != 0 && extra < 0) Check(generated.Flags == 0x246, "Render guard preserves flags on alternate exit.");
                }
            }
            Marshal.WriteInt32(percent, sizeof(int), 1);
        }
    }

    private static void PrepareOriginalInputs(MachineHarness harness, WorkerProductionBonusNativeSite site, int amount)
    {
        ulong units = Address(WorkerProductionBonusNativeDefinition.UnitManagerRva), buildings = Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva);
        const ulong offset = UnitId * UnitStride;
        if (site.Kind == WorkerProductionBonusNativeKind.OxDeparture)
        { harness.Set(3, offset); harness.Set(1, offset); harness.Set(14, units); return; }
        if (site.Kind == WorkerProductionBonusNativeKind.Reserve)
        { harness.Set(3, units + offset); return; }
        if (site.Kind == WorkerProductionBonusNativeKind.Render)
        {
            harness.Set(0, units + offset); harness.Set(14, 0);
            Word(WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride + WorkerProductionBonusNativeDefinition.BonusYieldOffset, amount);
            return;
        }
        harness.Set(3, offset); harness.Set(12, units); harness.Set(5, buildings);
        harness.Set(13, units); harness.Set(14, buildings); harness.Set(15, units); harness.Set(7, offset);
        // MOV instructions below may read ESI/EDI/EBX as a value; pointers consumed by
        // other original operands stay valid, while no original external call executes.
    }

    private static Dictionary<int, ulong> ExpectedFooterRegisters(string name)
    {
        ulong units = Address(WorkerProductionBonusNativeDefinition.UnitManagerRva), buildings = Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva);
        const ulong offset = UnitId * UnitStride;
        switch (name)
        {
            case "Wood": case "Pitch": return new Dictionary<int, ulong> { [3]=UnitId,[1]=offset,[12]=units,[6]=0,[7]=1 };
            case "Hunter": return new Dictionary<int, ulong> { [3]=UnitId,[1]=offset,[13]=units,[5]=0,[6]=1 };
            case "Hops": return new Dictionary<int, ulong> { [1]=offset,[12]=units,[3]=buildings,[7]=0 };
            case "Apple": return new Dictionary<int, ulong> { [3]=offset,[12]=units,[7]=0,[5]=1,[8]=UnitId };
            case "Cattle": return new Dictionary<int, ulong> { [3]=offset,[13]=units,[7]=0,[6]=1,[8]=UnitId,[12]=2 };
            case "Iron": return new Dictionary<int, ulong> { [3]=UnitId,[1]=offset,[15]=units,[6]=0,[12]=1 };
            case "Fletcher": return new Dictionary<int, ulong> { [3]=offset,[12]=units,[7]=0,[6]=1,[14]=UnitId,[15]=3 };
            case "Brewer": return new Dictionary<int, ulong> { [7]=offset,[15]=units,[6]=0,[12]=1,[3]=UnitId };
            case "Poleturner": return new Dictionary<int, ulong> { [3]=offset,[12]=units,[6]=0,[13]=1,[5]=UnitId,[14]=3 };
            case "Blacksmith": return new Dictionary<int, ulong> { [3]=offset,[12]=units,[13]=0,[7]=1,[5]=UnitId,[14]=3 };
            case "Armourer": return new Dictionary<int, ulong> { [14]=offset,[13]=units,[5]=0,[6]=1,[15]=UnitId,[3]=buildings,[7]=3 };
            case "Tanner": return new Dictionary<int, ulong> { [3]=UnitId,[1]=offset,[12]=units,[7]=0,[13]=1,[5]=3 };
            default: throw new InvalidOperationException("Missing independent footer fixture.");
        }
    }

    private static void CheckOriginalReplay(MachineHarness harness, WorkerProductionBonusNativeSite site, int amount)
    {
        // Explicit outputs of the displaced MOV/LEA/XOR block; all other live state
        // and every XMM register must survive the dormant/positive-output guard.
        var changed = new List<int>();
        void Expected(int register, ulong value) { changed.Add(register); Check(harness.Get(register) == value, $"Original replay output: {site.Name}/{RegisterOrder[register]} expected={value:X}, actual={harness.Get(register):X}."); }
        ulong buildings = Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva);
        switch (site.Name)
        {
            case "Wood": case "Iron": Expected(2, (uint)harness.InputRegister(site.Name == "Wood" ? 14 : 7)); Expected(8, 7); Expected(1, harness.InputRegister(5)); break;
            case "Hunter": Expected(2, (uint)harness.InputRegister(3)); Expected(8, 7); Expected(7, buildings); break;
            case "Pitch": Expected(5, buildings); Expected(8, 7); break;
            case "Hops": Expected(8, 7); Expected(3, buildings); break;
            case "Apple": case "Cattle": Expected(2, (uint)harness.InputRegister(site.Name == "Apple" ? 6 : 5)); Expected(8, 7); Expected(1, harness.InputRegister(14)); break;
            case "Fletcher": case "Poleturner": case "Blacksmith": Expected(2, (uint)amount); Expected(0, 3); break;
            case "Brewer": Expected(1, harness.InputRegister(13)); Expected(9, (ushort)1); Expected(2, (uint)harness.InputRegister(6) + 14); break;
            case "Armourer": Expected(2, 0); Expected(0, (uint)amount); Expected(9, 1); break;
            case "Tanner": Expected(2, 0); Expected(0, (uint)amount); Expected(1, harness.InputRegister(6)); break;
            default: throw new InvalidOperationException("Missing original replay fixture.");
        }
        harness.CheckPreserved(changed.ToArray());
        if (site.Name != "Armourer" && site.Name != "Tanner") Check(harness.Flags == 0x246, "Flag-neutral displaced instructions preserve incoming flags.");
    }
}
