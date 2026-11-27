using System;
using Iced.Intel;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    // An optional early delivery boundary, independently admitted from the original
    // recipe adapter. Native execution replays this prefix; custom execution skips
    // the capacity/reselection/add/statistics tail and reaches only a qualified exit.
    internal sealed class WeaponStoredDeliverySite
    {
        internal WeaponStoredDeliverySite(eStructs building, int rva, string bytes, string next,
            int footer, int epilogue, string epilogueBytes, Register unitId, Register unitOffset,
            Register zero, Register one, Register three, Register unitManager, Register buildingManager)
        {
            Building = building; Rva = rva; Bytes = WorkerProductionBonusNativeDefinition.Hex(bytes);
            Next = WorkerProductionBonusNativeDefinition.Hex(next); FooterRva = footer; EpilogueRva = epilogue;
            EpilogueBytes = WorkerProductionBonusNativeDefinition.Hex(epilogueBytes);
            UnitIdRegister = unitId; UnitOffsetRegister = unitOffset; ZeroRegister = zero;
            OneRegister = one; ThreeRegister = three; UnitManagerRegister = unitManager; BuildingManagerRegister = buildingManager;
        }
        internal eStructs Building { get; }
        internal int Rva { get; }
        internal byte[] Bytes { get; }
        internal byte[] Next { get; }
        internal int FooterRva { get; }
        internal int EpilogueRva { get; }
        internal byte[] EpilogueBytes { get; }
        // All metadata registers use their 64-bit names. ID/constants are emitted
        // through their low DWORDs; record offsets and manager pointers stay 64-bit.
        internal Register UnitIdRegister { get; }
        internal Register UnitOffsetRegister { get; }
        internal Register ZeroRegister { get; }
        internal Register OneRegister { get; }
        internal Register ThreeRegister { get; }
        internal Register UnitManagerRegister { get; }
        internal Register BuildingManagerRegister { get; }
    }

    internal static partial class WeaponProductionNativeDefinition
    {
        internal const int StoredCargoOffset = 0x384, StoredDestinationOffset = 0x332, StoredDestinationGlobalOffset = 0x9C;
        internal const int StoredAiStateOffset = 0x2BC, StoredRetryTimerOffset = 0x2C4;
        internal const int StoredWorkerWireStride = 20, StoredWorkerOptionOffset = 12, StoredOptionCount = 32;
        internal const string StoredDeliveryBoundaryError = "Weapon stored-delivery native boundary differs.";
        // ImprovedHunters may make the same Tanner batch carry meat. Without a
        // durable route marker an absent/reused destination cannot distinguish that
        // meat from custom armour. Require the original two adjacent call blocks for
        // NEW stored-output recipes; the existing native recipe cohort stays usable.
        internal const int TannerStoredYieldBoundaryRva = 0x13EBF7, TannerStoredRouteBoundaryRva = 0x13EF38;
        internal static readonly byte[] TannerStoredYieldBoundaryBytes = WorkerProductionBonusNativeDefinition.Hex("E844ED0400");
        internal static readonly byte[] TannerStoredRouteBoundaryBytes = WorkerProductionBonusNativeDefinition.Hex("468B8421F8060000488BCEE8B871F7FF");
        internal static bool HasVanillaStoredDeliveryRoute(ReadOnlySpan<byte> image, eStructs building) =>
            building != eStructs.STRUCT_TANNERS_WORKSHOP ||
                Internal.NativeInlineStub.SnapshotMatches(image, TannerStoredYieldBoundaryRva, TannerStoredYieldBoundaryBytes) &&
                Internal.NativeInlineStub.SnapshotMatches(image, TannerStoredRouteBoundaryRva, TannerStoredRouteBoundaryBytes);

        // FBCB9319 full-worker fingerprints are shared with Functions. All five gates
        // end at complete instructions before C9080 and destructive destination loss.
        // The captured 236382-row static xref audit has no incoming edge to an interior.
        // Footer constants are necessary: the skipped native tail normally establishes
        // some of them. In particular Tanner BP=3 is later stored at native +0x6E4.
        internal static readonly WeaponStoredDeliverySite[] StoredDeliverySites =
        {
            new WeaponStoredDeliverySite(eStructs.STRUCT_FLETCHERS_WORKSHOP, 0x12DC6D,
                "460FBF84238E090000418BD6488BCD", "E8FFB3F9FF84C0", 0x12DEB6, 0x12E3DB,
                "488B742478488B6C2470488B9C2480000000",
                Register.R14, Register.RBX, Register.RDI, Register.RSI, Register.R15, Register.R12, Register.RBP),
            new WeaponStoredDeliverySite(eStructs.STRUCT_POLETURNERS_WORKSHOP, 0x13B654,
                "8BD5460FBF84238E090000488BCF", "E819DAF8FF84C0", 0x13B894, 0x13BDAA,
                "4C8B742478488B742470488B9C2480000000",
                Register.RBP, Register.RBX, Register.RSI, Register.R13, Register.R14, Register.R12, Register.RDI),
            new WeaponStoredDeliverySite(eStructs.STRUCT_BLACKSMITHS_WORKSHOP, 0x13C7DC,
                "8BD5460FBF84238E090000488BCE", "E891C8F8FFBF01000000", 0x13CA2E, 0x13CF02,
                "488B742470488B7C2478488B9C2480000000",
                Register.RBP, Register.RBX, Register.R13, Register.RDI, Register.R14, Register.R12, Register.RSI),
            new WeaponStoredDeliverySite(eStructs.STRUCT_ARMOURERS_WORKSHOP, 0x13D97E,
                "460FBF842B8E090000488D1D22F23806", "488BCBE8EAB6F8FF", 0x13DB8C, 0x13E085,
                "488B6C2470488B5C2478488BB42480000000",
                Register.R15, Register.R14, Register.RBP, Register.RSI, Register.RDI, Register.R13, Register.RBX),
            new WeaponStoredDeliverySite(eStructs.STRUCT_TANNERS_WORKSHOP, 0x13EEAF,
                "488D35FADC3806460FBF84238E090000", "488BCEE8B9A1F8FF", 0x13F0BC, 0x13E6C2,
                "488B6C2478488B9C24880000004883C440",
                Register.RBX, Register.RCX, Register.RDI, Register.R13, Register.RBP, Register.R12, Register.RSI)
        };
    }
}
