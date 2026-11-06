using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

internal static partial class Program
{
    private static readonly Dictionary<string, int> WorkerEpilog = new Dictionary<string, int>
    {
        ["Wood"]=0x12D20C,["Hunter"]=0x131402,["Pitch"]=0x1337E3,["Hops"]=0x135E0F,
        ["Apple"]=0x136D12,["Cattle"]=0x137798,["Iron"]=0x14E4B8,["Fletcher"]=0x12E3DB,
        ["Brewer"]=0x13A74F,["Poleturner"]=0x13BDAA,["Blacksmith"]=0x13CF02,
        ["Armourer"]=0x13E085,["Tanner"]=0x13E6C2
    };

    private static void TestActualNativeFooters(IntPtr percent)
    {
        // These are external boundaries of the footer under test. Its real instructions,
        // state writes, retirement path and control flow execute from the copied DLL.
        // Only work accounting / fear selection / workplace and route queries are doubled.
        IntPtr observations = Marshal.AllocHGlobal(256);
        var restored = new List<(int Rva, byte[] Bytes)>();
        try
        {
            void Boundary(int rva, int observation, int resultOffset, bool route = false)
            {
                // Return values are read from caller-owned native data so every scenario
                // exercises the same compiled boundary and production footer bytes.
                using var code = new Code(a => {
                    a.push(rcx); a.mov(rcx, unchecked((ulong)observations.ToInt64()));
                    a.inc(__dword_ptr[rcx + observation]); a.pop(rcx);
                    a.push(r11); a.mov(r11, unchecked((ulong)observations.ToInt64()));
                    a.mov(__qword_ptr[r11 + observation + 8], rcx);
                    a.mov(__qword_ptr[r11 + observation + 16], rdx);
                    if (route)
                    {
                        a.mov(__qword_ptr[r11 + 192], r8); a.mov(__qword_ptr[r11 + 200], r9);
                        a.mov(eax, __dword_ptr[rsp + 0x30]); // fifth argument above return address and shadow space; R11 was pushed
                        a.mov(__dword_ptr[r11 + 208], eax);
                    }
                    a.mov(eax, __dword_ptr[r11 + resultOffset]); a.pop(r11); a.ret();
                });
                restored.Add((rva, Read(rva, 16)));
                using var stream = new System.IO.MemoryStream();
                var jump = new Assembler(64); jump.AddUnrestrictedJmp(unchecked((ulong)code.Address.ToInt64()));
                jump.Assemble(new StreamCodeWriter(stream), Address(rva)); Copy(rva, stream.ToArray());
            }
            Boundary(0x193A70, 0, 96); // work accounting
            Boundary(0x191070, 24, 100); // fear pause
            Boundary(0x19B930, 48, 104); // next workplace
            Boundary(0xBBB70, 72, 96); // farm validity; force the ordinary completion fallback
            Boundary(0xB9280, 120, 108); // ingredient building lookup
            Boundary(0xC90E0, 144, 108); // ingredient building validation
            Boundary(0x196280, 168, 108, route: true); // route selection

            const int ingredientBuilding = 1, buildingStride = 0x32C, tile = 0x12345678, x = 211, y = 177;
            int building = WorkerProductionBonusNativeDefinition.BuildingManagerRva + ingredientBuilding * buildingStride;
            Put(building + 0x134, tile); Word(building + 0x15A, x); Word(building + 0x15C, y);
            // Fletcher's native recipe gates precede its ingredient lookup (12DEFA,
            // 12DF0F in mode99). Zero-filled copied BSS would skip every ingredient
            // scenario regardless of the external query returns under test.
            Put(0x87EE30C, 1); Put(0x87EE2F4, 1); Put(0x87EE2F0, 1);
            Word(WorkerProductionBonusNativeDefinition.BuildingManagerRva + 3 * buildingStride + 0x2EC, 0);

            foreach (var site in WorkerProductionBonusNativeDefinition.Sites.Where(s => s.Kind == WorkerProductionBonusNativeKind.ZeroCompletion))
            {
                Console.WriteLine("Executing actual native completion paths: " + site.Name);
                var original = WorkerProductionBonusNativeDefinition.Decode(site.OriginalBytes, Address(site.Rva));
                using var harness = new MachineHarness(a => Generate(a, original, Address(site.Rva + site.Length), site, percent),
                    new Dictionary<int, int> { [WorkerEpilog[site.Name]]=1, [site.Rva+site.Length]=2 });
                foreach (int workplace in new[] { 0, 1 })
                foreach (int fear in new[] { 0, 1 })
                foreach (int ingredients in new[] { 0, 1 })
                {
                    for (int offset = 0; offset < 256; offset += 8) Marshal.WriteInt64(observations, offset, 0);
                    Marshal.WriteInt32(observations, 100, fear);
                    Marshal.WriteInt32(observations, 104, workplace);
                    Marshal.WriteInt32(observations, 108, ingredients);
                    int unit = WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride;
                    for (int offset = 0x650; offset < 0xAE0; offset += 8) Marshal.WriteInt64(image, unit + offset, 0);
                    Word(unit + 0x6E4, 2); // existing alive unit
                    Word(unit + 0x6EE, 1); // participating owner
                    Word(unit + 0x990, 3); // linked building fixture
                    Put(WorkerProductionBonusNativeDefinition.CurrentUnitRva, UnitId);
                    Put(ModeRva, 99); Marshal.WriteInt32(percent, -50);
                    harness.ResetInput(); PrepareOriginalInputs(harness, site, 0);
                    PrepareNativeFooterLiveInputs(harness, site);
                    harness.Run();
                    Check(harness.Marker == 1, "Zero output executes the actual complete native footer: " + site.Name);
                    Check(Marshal.ReadInt32(observations, 0) == 1 && Marshal.ReadInt64(observations, 8) == UnitId && Marshal.ReadInt64(observations, 16) == 1,
                        "Native footer accounts exactly one complete work cycle: " + site.Name);
                    Check(Word(unit + 0x9E0) == 0, "Native footer never creates or underflows cargo: " + site.Name);
                    bool simpleRetirement = site.Name == "Pitch" || site.Name == "Iron";
                    bool immediateWorkshopRetirement = site.Name == "Brewer" || site.Name == "Poleturner" || site.Name == "Blacksmith" || site.Name == "Armourer" || site.Name == "Tanner";
                    bool retired = (simpleRetirement || immediateWorkshopRetirement && fear == 0 && (ingredients == 0 || site.Name == "Tanner")) && workplace == 0;
                    Check(Word(unit + 0x6E4) == (retired ? 3 : 2), $"Invalid workplace retires with the exact native alive state: {site.Name}; workplace={workplace},fear={fear},ingredients={ingredients},alive={Word(unit + 0x6E4)},queryCalls={Marshal.ReadInt32(observations,48)}.");
                    // These three workers use the normal delayed-retirement state instead
                    // of deleting immediately. Preserve the distinction; forcing alive=3
                    // here would skip Vanilla's later recovery/retirement attempts.
                    bool waitsForWork = (site.Name == "Wood" || site.Name == "Hunter" || site.Name == "Fletcher" && fear == 0 && ingredients == 0) && workplace == 0;
                    if (waitsForWork)
                        Check(Word(unit + 0x918) == 110, "Native delayed-retirement state remains available: " + site.Name);
                    bool ingredientWorkshop = site.Name == "Fletcher" || site.Name == "Brewer" || site.Name == "Poleturner" || site.Name == "Blacksmith" || site.Name == "Armourer";
                    if (ingredientWorkshop && fear == 0)
                    {
                        ulong buildings = Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva);
                        Check(Marshal.ReadInt32(observations, 120) == 1 && unchecked((ulong)Marshal.ReadInt64(observations, 128)) == buildings &&
                            Marshal.ReadInt32(observations, 144) == 1 && unchecked((ulong)Marshal.ReadInt64(observations, 152)) == buildings,
                            "Native ingredient queries use the real building manager: " + site.Name);
                        if (site.Name == "Fletcher")
                            Check(Marshal.ReadByte(image, unit + 0xA47) == 3 && Marshal.ReadInt64(observations, 136) == 2,
                                "Native Fletcher recipe retains the prologue's material selector R13D=2.");
                        if (ingredients != 0)
                        {
                            Check(Marshal.ReadInt32(observations, 168) == 1 &&
                                unchecked((ulong)Marshal.ReadInt64(observations, 176)) == Address(WorkerProductionBonusNativeDefinition.UnitManagerRva) &&
                                Marshal.ReadInt64(observations, 184) == UnitId && Marshal.ReadInt64(observations, 192) == x &&
                                Marshal.ReadInt64(observations, 200) == y && Marshal.ReadInt32(observations, 208) == 0,
                                "Native route receives the selected ingredient building coordinates: " + site.Name);
                            Check(Word(unit + 0x98E) == ingredientBuilding && Marshal.ReadInt32(image, unit + 0x6F8) == tile,
                                "Native footer retains the selected building and tile identity: " + site.Name);
                        }
                    }
                }
            }
        }
        finally
        {
            // All code belongs to this private allocation and was never published to game threads.
            foreach (var entry in restored) Copy(entry.Rva, entry.Bytes);
            Marshal.FreeHGlobal(observations);
        }
    }

    private static void PrepareNativeFooterLiveInputs(MachineHarness harness, WorkerProductionBonusNativeSite site)
    {
        // Inputs already live at the original storage hook, not the generator's footer
        // assignments. The generic gate fixture deliberately uses arbitrary sentinels;
        // complete native footers additionally consume these preserved native values.
        ulong buildings = Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva);
        switch (site.Name)
        {
            case "Hops": harness.Set(13, 1); break; // 134F34; only R13 definition before storage hook
            case "Fletcher": harness.Set(5, buildings); harness.Set(13, 2); break; // 12DA74, 12D257
            case "Brewer": harness.Set(13, buildings); break; // 139972
            case "Poleturner": harness.Set(7, buildings); break; // 13B44F
            case "Blacksmith": harness.Set(6, buildings); break; // 13C5E8
            // Other eight workers have no additional preserved register read on the
            // selected zero-cargo footer paths; the generator establishes their inputs.
            case "Wood": case "Hunter": case "Pitch": case "Apple": case "Cattle":
            case "Iron": case "Armourer": case "Tanner": break;
            default: throw new InvalidOperationException("Missing native footer live-input audit.");
        }
    }
}
