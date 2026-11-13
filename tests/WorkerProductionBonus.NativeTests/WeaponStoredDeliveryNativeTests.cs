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
    // The complete production generator executes in the existing private mapped image.
    // Capture cases stop at the native/accepted/refused boundaries. Execution cases then
    // run the actual arrival tail and cargo-test footer, with only external native helpers
    // replaced by counted machine-code doubles. This is not CLR callback, SE-event,
    // physical-ledger, retarget, installed-hook, save/load or game acceptance coverage.
    private static void TestWeaponStoredDelivery()
    {
        const int home = 3, destination = 5, option = 7, owner = 2, secondUnit = 2;
        const int wireStride = 20, optionOffset = 12, optionCount = 32;
        int slots = WeaponProductionNativeDefinition.MaxBuildingId + 1;
        int wireBytes = (WeaponProductionNativeDefinition.MaxUnitId + 1) * wireStride;
        int bytes = checked(slots + 8 + wireBytes + optionCount + 256);
        IntPtr control = Marshal.AllocHGlobal(bytes);
        IntPtr enabled = control + slots, wire = enabled + 8;
        IntPtr options = wire + wireBytes, receipt = options + optionCount;
        var restorations = new List<(int Rva, byte[] Bytes)>();
        const int callbackCalls = 0, callbackUnit = 4, callbackRsp = 8, callbackFlags = 16;
        const int decision = 24, adds = 28, observers = 32, templateStock = 36, customStock = 40;
        const int cycles = 44, cycleUnit = 48, cycleMode = 52, rests = 56, restUnit = 60, restMode = 64;
        const int achievements = 68, cues = 72, validations = 76, finders = 80, changeGlobal = 84;
        const int addManager = 88, addSlot = 96, addGlobal = 100, addGood = 104;
        const int addAmount = 108, addCapacity = 112, addFlag = 116, addRsp = 120;
        ulong receiptAddress = unchecked((ulong)receipt.ToInt64());
        int unit = WorkerProductionBonusNativeDefinition.UnitManagerRva + UnitId * UnitStride;
        int otherUnit = WorkerProductionBonusNativeDefinition.UnitManagerRva + secondUnit * UnitStride;
        int score = 0x87EF02C + owner * sizeof(int);
        try
        {
            Marshal.Copy(new byte[bytes], 0, control, bytes);
            using var callback = new Code(a => {
                a.mov(r11, receiptAddress); a.inc(__dword_ptr[r11 + callbackCalls]);
                a.mov(__dword_ptr[r11 + callbackUnit], ecx); a.mov(__qword_ptr[r11 + callbackRsp], rsp);
                a.pushfq(); a.pop(rax); a.mov(__qword_ptr[r11 + callbackFlags], rax);
                var noCommit = a.CreateLabel(); var keepGlobal = a.CreateLabel();
                a.cmp(__dword_ptr[r11 + decision], 1); a.jne(noCommit);
                // A controlled accepted boundary, not a duplicate implementation of the
                // storage adapter: count one credit and debit the actual native cargo cell.
                a.inc(__dword_ptr[r11 + customStock]); a.movsxd(r10, ecx); a.imul(r10, r10, UnitStride);
                a.mov(rax, Address(WorkerProductionBonusNativeDefinition.UnitManagerRva));
                a.dec(__word_ptr[rax + r10 + WorkerProductionBonusNativeDefinition.CarriedOffset]);
                a.Label(ref noCommit);
                a.cmp(__dword_ptr[r11 + changeGlobal], 0); a.je(keepGlobal);
                a.mov(rax, Address(WorkerProductionBonusNativeDefinition.CurrentUnitRva)); a.mov(__dword_ptr[rax], secondUnit);
                a.Label(ref keepGlobal);
                a.xor(ecx, ecx); a.xor(edx, edx); a.xor(r8d, r8d); a.xor(r9d, r9d); a.xor(r10d, r10d); a.xor(r11d, r11d);
                for (int i = 0; i < 6; i++) a.pxor(Simd[i], Simd[i]);
                a.stc(); a.std(); // The production frame must restore flags, including DF.
                a.mov(rax, receiptAddress); a.mov(eax, __dword_ptr[rax + decision]); a.ret();
            });

            void PatchHelper(int rva, Action<Assembler> emit)
            {
                using var body = new Code(emit);
                restorations.Add((rva, Read(rva, 16)));
                var jump = new Assembler(64); jump.AddUnrestrictedJmp(unchecked((ulong)body.Address.ToInt64()));
                Copy(rva, WorkerProductionBonusNativeAssembly.Assemble(jump, Address(rva)));
            }
            void CountHelper(int rva, int countOffset, int result)
            {
                PatchHelper(rva, a => {
                    a.mov(r11, receiptAddress); a.inc(__dword_ptr[r11 + countOffset]); a.mov(eax, result); a.ret();
                });
            }
            CountHelper(0xC9080, validations, 1);
            CountHelper(0xB9AA0, finders, destination);
            PatchHelper(0xB6100, a => {
                // Models an accepting Original plus a synchronous SE observer. The
                // actual SE wrapper and native storage implementation do not run here.
                a.mov(r11, receiptAddress); a.inc(__dword_ptr[r11 + adds]);
                a.mov(__qword_ptr[r11 + addManager], rcx); a.mov(__dword_ptr[r11 + addSlot], edx);
                a.mov(__dword_ptr[r11 + addGlobal], r8d); a.mov(__dword_ptr[r11 + addGood], r9d);
                a.mov(eax, __dword_ptr[rsp + 0x28]); a.mov(__dword_ptr[r11 + addAmount], eax);
                a.mov(eax, __dword_ptr[rsp + 0x30]); a.mov(__dword_ptr[r11 + addCapacity], eax);
                a.mov(eax, __dword_ptr[rsp + 0x38]); a.mov(__dword_ptr[r11 + addFlag], eax);
                a.mov(__qword_ptr[r11 + addRsp], rsp);
                a.inc(__dword_ptr[r11 + observers]); a.inc(__dword_ptr[r11 + templateStock]); a.mov(eax, 1); a.ret();
            });
            CountHelper(0x19D030, achievements, 1);
            CountHelper(0x2960, cues, 1);
            PatchHelper(0x193A70, a => {
                a.mov(r11, receiptAddress); a.inc(__dword_ptr[r11 + cycles]);
                a.mov(__dword_ptr[r11 + cycleUnit], ecx); a.mov(__dword_ptr[r11 + cycleMode], edx); a.xor(eax, eax); a.ret();
            });
            PatchHelper(0x191070, a => {
                a.mov(r11, receiptAddress); a.inc(__dword_ptr[r11 + rests]);
                a.mov(__dword_ptr[r11 + restUnit], ecx); a.mov(__dword_ptr[r11 + restMode], edx);
                a.mov(eax, 1); a.ret(); // Take the real short completion/rest branch.
            });

            int ReadCount(int at) => Marshal.ReadInt32(receipt, at);
            void Reset(eStructs building, int cargo, int outcome, int globalMutation, int wireOption = option, int storedFlag = 1)
            {
                for (int at = 0; at < 256; at += 8) Marshal.WriteInt64(receipt, at, 0);
                for (int at = 0; at < optionCount; at++) Marshal.WriteByte(options, at, 0);
                Marshal.WriteByte(control, home, 1); Marshal.WriteInt32(enabled, 1);
                Marshal.WriteByte(wire, UnitId * wireStride + optionOffset, (byte)wireOption);
                Marshal.WriteByte(options, option, (byte)storedFlag);
                Marshal.WriteInt32(receipt, decision, outcome); Marshal.WriteInt32(receipt, changeGlobal, globalMutation);
                Put(WorkerProductionBonusNativeDefinition.CurrentUnitRva, UnitId); Put(score, 0);
                foreach (int record in new[] { unit, otherUnit })
                    for (int at = 0x650; at < 0xAE0; at += 8) Marshal.WriteInt64(image, record + at, 0);
                Word(unit + 0x990, home); Word(unit + 0x98E, destination); Put(unit + 0x6F8, 0x345678);
                Word(unit + 0x6EE, owner); Word(unit + 0x9E0, cargo); Word(otherUnit + 0x9E0, 9);
                Word(WorkerProductionBonusNativeDefinition.BuildingManagerRva + home * 0x32C + 0x2EA,
                    (int)StoredNativeTemplate(building));
            }

            foreach (WeaponStoredDeliverySite site in WeaponProductionNativeDefinition.StoredDeliverySites)
            {
                Check(At(site.Rva, site.Bytes.Length).SequenceEqual(site.Bytes), "Stored-delivery gate matches pinned native bytes.");
                Check(At(site.Rva + site.Bytes.Length, site.Next.Length).SequenceEqual(site.Next), "Stored-delivery native continuation matches.");
                Check(Read(site.Rva, site.Bytes.Length).SequenceEqual(site.Bytes) &&
                    Read(site.FooterRva, 24).SequenceEqual(At(site.FooterRva, 24)) &&
                    Read(site.EpilogueRva, site.EpilogueBytes.Length).SequenceEqual(site.EpilogueBytes),
                    "Prior private fixtures restored the real stored-delivery gate, footer and epilogue bytes.");
                var original = WorkerProductionBonusNativeDefinition.Decode(site.Bytes, Address(site.Rva));
                ulong callbackAddress = unchecked((ulong)callback.Address.ToInt64());
                void Generate(Assembler a) => WeaponProductionNativeAssembly.GenerateStoredDelivery(a, original,
                    Address(site.Rva + site.Bytes.Length), site, Address(0), unchecked((ulong)enabled.ToInt64()),
                    unchecked((ulong)control.ToInt64()), unchecked((ulong)wire.ToInt64()), unchecked((ulong)options.ToInt64()), callbackAddress);

                using (var prepared = new X64InlineHook(Address(site.Rva), site.Bytes.Length))
                {
                    Check(prepared.DisplacedByteCount == site.Bytes.Length, "Stored-delivery backend displaces the exact complete span.");
                    prepared.Generate((a, instructions, back) => WeaponProductionNativeAssembly.GenerateStoredDelivery(a,
                        instructions, back, site, Address(0), unchecked((ulong)enabled.ToInt64()), unchecked((ulong)control.ToInt64()),
                        unchecked((ulong)wire.ToInt64()), unchecked((ulong)options.ToInt64()), callbackAddress));
                    byte[] expected = WeaponProductionNativeAssembly.ExpectedStoredDeliveryStub(site, Address(0),
                        unchecked((ulong)prepared.StubAddress.ToInt64()), unchecked((ulong)enabled.ToInt64()),
                        unchecked((ulong)control.ToInt64()), unchecked((ulong)wire.ToInt64()), unchecked((ulong)options.ToInt64()), callbackAddress);
                    var actual = new byte[expected.Length]; Marshal.Copy(prepared.StubAddress, actual, 0, actual.Length);
                    Check(actual.SequenceEqual(expected), "Prepared stored-delivery bytes equal the full production generator.");
                }

                // First isolate each generated exit. No original helper executes in these
                // cases, so input/output sentinels test every callback save/restore directly.
                using (var capture = new MachineHarness(Generate, new Dictionary<int, int> {
                    [site.Rva + site.Bytes.Length] = 1, [site.FooterRva] = 2, [site.EpilogueRva] = 3 }))
                {
                    foreach (var sample in new[] {
                        (Enabled: 0, Demand: 1, Option: option, Flag: 1, Decision: 1, Mutate: 0),
                        (Enabled: 1, Demand: 0, Option: option, Flag: 1, Decision: 1, Mutate: 0),
                        (Enabled: 1, Demand: 1, Option: 6, Flag: 0, Decision: 1, Mutate: 0),
                        (Enabled: 1, Demand: 1, Option: 32, Flag: 1, Decision: 1, Mutate: 0),
                        (Enabled: 1, Demand: 1, Option: option, Flag: 2, Decision: 1, Mutate: 0),
                        (Enabled: 1, Demand: 1, Option: option, Flag: 1, Decision: 0, Mutate: 0),
                        (Enabled: 1, Demand: 1, Option: option, Flag: 1, Decision: 1, Mutate: 1),
                        (Enabled: 1, Demand: 1, Option: option, Flag: 1, Decision: 2, Mutate: 0),
                        (Enabled: 1, Demand: 1, Option: option, Flag: 1, Decision: 99, Mutate: 0) })
                    {
                        Reset(site.Building, 3, sample.Decision, sample.Mutate, sample.Option, sample.Flag);
                        Marshal.WriteInt32(enabled, sample.Enabled); Marshal.WriteByte(control, home, (byte)sample.Demand);
                        PrepareStoredDeliveryInputs(capture, site.Building);
                        bool active = sample.Enabled == 1 && sample.Demand == 1 && sample.Option == option && sample.Flag == 1;
                        bool accepted = active && sample.Decision == 1;
                        if (accepted)
                        {
                            // Poison the incoming ID/index independently of the valid demand
                            // globals. Accepted repair must use the pre-callback captured ID.
                            var repaired = ExpectedStoredFooterRegisters(site.Building);
                            capture.Set(repaired.Id, 777); capture.Set(repaired.Offset, 0xDEADBEEF);
                        }
                        capture.Run();
                        Check(capture.Marker == (!active || sample.Decision == 0 ? 1 : accepted ? 2 : 3), "Full stored-delivery disposition selects its audited exit.");
                        Check(ReadCount(callbackCalls) == (active ? 1 : 0), "Dormant/native-option paths avoid the callback entirely.");
                        Check(ReadCount(adds) == 0 && ReadCount(cycles) == 0, "Capture cases stop before native helper boundaries.");
                        if (active)
                        {
                            Check(ReadCount(callbackUnit) == UnitId, "Stored callback receives the original physical worker ID.");
                            Check((unchecked((ulong)Marshal.ReadInt64(receipt, callbackRsp)) & 15UL) == 8, "Stored callback stack is aligned with return address/home space.");
                            Check((unchecked((ulong)Marshal.ReadInt64(receipt, callbackFlags)) & 0x400UL) == 0, "Callback enters with DF clear.");
                        }
                        if (accepted) CheckStoredFooterRegisters(capture, site.Building);
                        else if (!active || sample.Decision == 0) CheckStoredNativePrefix(capture, site.Building, destination);
                        else capture.CheckPreserved();
                        if (!accepted) Check(capture.Flags == 0x246UL, "Native/refused paths restore caller flags after the callback.");
                        Check(Word(unit + 0x9E0) == (accepted ? 2 : 3) && Word(otherUnit + 0x9E0) == 9,
                            "Only one accepted captured worker is debited, including when the global changes.");
                    }
                }

                // Unlike capture mode, this continues through original mapped arrival
                // and footer instructions. No footer is replaced: its cargo test, +9CC
                // flag and rest branch really run. The seven external helpers above are
                // doubles; the epilogue is captured before consuming a real game stack.
                using (var execution = new MachineHarness(Generate, new Dictionary<int, int> { [site.EpilogueRva] = 3 }))
                foreach (var sample in new[] {
                    (Enabled: 0, Option: option, Decision: 1, Cargo: 2, Mutate: 0),
                    (Enabled: 1, Option: 6, Decision: 1, Cargo: 2, Mutate: 0),
                    (Enabled: 1, Option: option, Decision: 0, Cargo: 2, Mutate: 0),
                    (Enabled: 1, Option: option, Decision: 1, Cargo: 1, Mutate: 0),
                    (Enabled: 1, Option: option, Decision: 1, Cargo: 3, Mutate: 1),
                    (Enabled: 1, Option: option, Decision: 2, Cargo: 3, Mutate: 0),
                    (Enabled: 1, Option: option, Decision: 99, Cargo: 3, Mutate: 0) })
                {
                    Reset(site.Building, sample.Cargo, sample.Decision, sample.Mutate, sample.Option);
                    Marshal.WriteInt32(enabled, sample.Enabled); PrepareStoredDeliveryInputs(execution, site.Building);
                    int state = site.Building == eStructs.STRUCT_FLETCHERS_WORKSHOP ? 7 : 8; Word(unit + 0x918, state);
                    bool active = sample.Enabled == 1 && sample.Option == option;
                    bool accepted = active && sample.Decision == 1;
                    bool nativePath = !active || sample.Decision == 0;
                    execution.Run();
                    int remainder = sample.Cargo - (accepted || nativePath ? 1 : 0);
                    Check(execution.Marker == 3, "Full stored/native execution reaches the qualified epilogue capture.");
                    Check(ReadCount(callbackCalls) == (active ? 1 : 0), "Full path callback demand is exact.");
                    Check(ReadCount(customStock) == (accepted ? 1 : 0) && Word(unit + 0x9E0) == remainder && Word(otherUnit + 0x9E0) == 9,
                        "Accepted unit credit and cargo debit occur once; refusal retains the complete cargo.");
                    Check(ReadCount(adds) == (nativePath ? 1 : 0) && ReadCount(observers) == (nativePath ? 1 : 0) &&
                        ReadCount(templateStock) == (nativePath ? 1 : 0), "Only the native path reaches Add/observer/template boundaries.");
                    if (nativePath)
                        Check(unchecked((ulong)Marshal.ReadInt64(receipt, addManager)) == Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva) &&
                            ReadCount(addSlot) == destination && ReadCount(addGlobal) == 0x345678 && ReadCount(addGood) == (int)StoredNativeTemplate(site.Building) &&
                            ReadCount(addAmount) == 1 && ReadCount(addCapacity) == 50 && ReadCount(addFlag) == 1 &&
                            (unchecked((ulong)Marshal.ReadInt64(receipt, addRsp)) & 15UL) == 8,
                            "Actual native setup supplies the audited manager, identities, good, stack arguments and alignment.");
                    Check(Marshal.ReadInt32(image, score) == (nativePath ? 1 : 0) && ReadCount(achievements) == (nativePath ? 1 : 0) &&
                        ReadCount(cues) == (nativePath ? 1 : 0), "Actual native score instructions and achievement/cue boundaries are skipped for custom output.");
                    Check(Marshal.ReadByte(image, unit + 0xAA6) == (nativePath ? 1 : 0), "Custom paths also skip the actual native stock-cue latch write.");
                    Check(ReadCount(validations) == (nativePath ? 1 : 0) && ReadCount(finders) == (nativePath ? 1 : 0),
                        "Custom paths bypass native destination/capacity failure and discard paths.");
                    Check(Word(unit + 0x98E) == destination && Marshal.ReadInt32(image, unit + 0x6F8) == 0x345678,
                        "Disposition branches retain destination slot and global identity.");
                    bool complete = (accepted || nativePath) && remainder == 0;
                    Check(Word(unit + 0x9CC) == (complete ? 1 : 0) && ReadCount(cycles) == (complete ? 1 : 0) &&
                        ReadCount(rests) == (complete ? 1 : 0), "The real footer completes once only when cargo reaches zero.");
                    if (complete)
                        Check(ReadCount(cycleUnit) == UnitId && ReadCount(cycleMode) == 1 && ReadCount(restUnit) == UnitId &&
                            ReadCount(restMode) == (site.Building == eStructs.STRUCT_FLETCHERS_WORKSHOP ? 3 : 4),
                            "Real completion uses the captured worker and audited role-specific rest mode.");
                    if (!nativePath)
                        Check(Word(unit + 0x918) == state, "The emitter does not invent an AI-state transition; managed retarget policy is tested separately.");
                }
            }
            Console.WriteLine("PASS: stored-delivery full emitters for all workshop roles; real cargo footers and counted synthetic helpers.");
        }
        finally
        {
            foreach (var entry in restorations) Copy(entry.Rva, entry.Bytes);
            Marshal.FreeHGlobal(control);
        }
    }

    private static eGoods StoredNativeTemplate(eStructs building)
    {
        switch (building)
        {
            case eStructs.STRUCT_FLETCHERS_WORKSHOP: return eGoods.STORED_BOWS;
            case eStructs.STRUCT_POLETURNERS_WORKSHOP: return eGoods.STORED_SPEARS;
            case eStructs.STRUCT_BLACKSMITHS_WORKSHOP: return eGoods.STORED_MACES;
            case eStructs.STRUCT_ARMOURERS_WORKSHOP: return eGoods.STORED_METAL_ARMOUR;
            case eStructs.STRUCT_TANNERS_WORKSHOP: return eGoods.STORED_LEATHER_ARMOUR;
            default: throw new InvalidOperationException("Missing native template for stored-delivery fixture.");
        }
    }

    private static void PrepareStoredDeliveryInputs(MachineHarness h, eStructs building)
    {
        h.ResetInput(); h.Set(3, UnitId * UnitStride);
        ulong units = Address(WorkerProductionBonusNativeDefinition.UnitManagerRva), buildings = Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva);
        switch (building)
        {
            case eStructs.STRUCT_FLETCHERS_WORKSHOP:
                h.Set(12, units); h.Set(5, buildings); h.Set(14, UnitId); h.Set(7, 0); h.Set(6, 1); h.Set(15, 3); h.Set(13, 2); break;
            case eStructs.STRUCT_POLETURNERS_WORKSHOP:
                h.Set(12, units); h.Set(7, buildings); h.Set(5, UnitId); h.Set(6, 0); h.Set(13, 1); h.Set(14, 3); break;
            case eStructs.STRUCT_BLACKSMITHS_WORKSHOP:
                h.Set(12, units); h.Set(6, buildings); h.Set(5, UnitId); h.Set(13, 0); break;
            case eStructs.STRUCT_ARMOURERS_WORKSHOP:
                h.Set(13, units); h.Set(7, UnitId); h.Set(2, UnitId); h.Set(5, 0); h.Set(6, 8); h.Set(14, units + UnitId * UnitStride); break;
            case eStructs.STRUCT_TANNERS_WORKSHOP:
                h.Set(12, units); h.Set(6, UnitId); h.Set(2, UnitId); h.Set(7, 0); h.Set(13, 1); break;
            default: throw new InvalidOperationException("Missing audited stored-delivery entry registers.");
        }
    }

    private static (int Id, int Offset, int Zero, int One, int Three, int Units, int Buildings) ExpectedStoredFooterRegisters(eStructs building)
    {
        // Independent native-footer ABI, not read back from the generator's metadata.
        switch (building)
        {
            case eStructs.STRUCT_FLETCHERS_WORKSHOP: return (14, 3, 7, 6, 15, 12, 5);
            case eStructs.STRUCT_POLETURNERS_WORKSHOP: return (5, 3, 6, 13, 14, 12, 7);
            case eStructs.STRUCT_BLACKSMITHS_WORKSHOP: return (5, 3, 13, 7, 14, 12, 6);
            case eStructs.STRUCT_ARMOURERS_WORKSHOP: return (15, 14, 5, 6, 7, 13, 3);
            case eStructs.STRUCT_TANNERS_WORKSHOP: return (3, 1, 7, 13, 5, 12, 6);
            default: throw new InvalidOperationException("Missing audited stored-delivery footer registers.");
        }
    }

    private static void CheckStoredFooterRegisters(MachineHarness h, eStructs building)
    {
        var r = ExpectedStoredFooterRegisters(building);
        Check(h.Get(r.Id) == UnitId && h.Get(r.Offset) == UnitId * UnitStride, "Accepted ID/index come from the pre-callback physical worker.");
        Check(h.Get(r.Zero) == 0 && h.Get(r.One) == 1 && h.Get(r.Three) == 3, "Skipped native tail constants are repaired.");
        Check(h.Get(r.Units) == Address(WorkerProductionBonusNativeDefinition.UnitManagerRva) &&
            h.Get(r.Buildings) == Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva), "Accepted footer manager bases are repaired.");
        h.CheckPreserved(r.Id, r.Offset, r.Zero, r.One, r.Three, r.Units, r.Buildings);
        Check((h.Flags & 0x400UL) == 0, "Accepted frame also restores DF before footer execution.");
    }

    private static void CheckStoredNativePrefix(MachineHarness h, eStructs building, int destination)
    {
        Check(h.Get(8) == (ulong)destination, "Replayed native prefix loads the original destination.");
        if (building == eStructs.STRUCT_ARMOURERS_WORKSHOP)
        {
            Check(h.Get(3) == Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva), "Armourer RIP-relative prefix is relocated.");
            h.CheckPreserved(3, 8);
        }
        else if (building == eStructs.STRUCT_TANNERS_WORKSHOP)
        {
            Check(h.Get(6) == Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva), "Tanner RIP-relative prefix is relocated.");
            h.CheckPreserved(6, 8);
        }
        else
        {
            Check(h.Get(1) == Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva) && h.Get(2) == UnitId,
                "Replayed native prefix preserves the original helper arguments.");
            h.CheckPreserved(1, 2, 8);
        }
    }
}
