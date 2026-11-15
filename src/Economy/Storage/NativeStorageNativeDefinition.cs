using System;
using System.Runtime.InteropServices;

namespace APIShared.Economy
{
    // Passive instruction/layout audit of the one supported image. Installs nothing;
    // runtime admission must also validate live identity, ownership and hook ownership.
    internal static class NativeStorageNativeDefinition
    {
        internal const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int BuildingStride = 0x32C, BuildingManagerArrayOffset = 0x5C;
        internal const int MaxBuildingId = 3999, NativeGoodsCount = 25, NativeGoodBytes = 4;
        internal const int AliveOffset = 0xD0, BuildingTypeOffset = 0xD2, OwnerOffset = 0xD6;
        internal const int GlobalIdOffset = 0xD8, LocalGoodsOffset = 0x120, CurrentStackOffset = 0x184;
        internal const int ArmoryType = 0xB, GranaryType = 0x13, AliveValue = 2;
        internal const int ArmoryCapacity = 50, GranaryCapacity = 250;

        // Windows x64 integer ABI: RCX/RDX/R8/R9; fifth argument at entry RSP+0x28.
        // Cdecl names the existing delegate contract. Admission returns INPUT SLOT or zero.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int ArmoryAdmissionCall(IntPtr manager, int buildingId, int unusedGood, int ownerId, int amount);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int ArmoryFreeCall(IntPtr manager, int buildingId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GranaryFreeCall(IntPtr manager, int buildingId, int unusedGood, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int GranaryRecomputeCall(IntPtr manager, int buildingId);

        // Validates Armory type/alive/owner, subtracts all 25 native local counters from
        // 50, and compares the requested amount. Good is unused. Returning zero for a
        // full-by-custom candidate preserves the original caller's outer search.
        internal const int ArmoryAdmissionRva = 0xC6EA0, ArmoryAdmissionLength = 287;
        internal const string ArmoryAdmissionSha256 = "02691DC94FF8B13B4BC4565042C132F18CCA58697C4B3948C4C2F176CC2A0561";
        // Four complete instructions: 3+3+7+10; continuation begins at JNE.
        internal const int ArmoryAdmissionDisplacedLength = 23, ArmoryAdmissionContinuationRva = 0xC6EB7;
        internal static readonly byte[] ArmoryAdmissionBytes = Convert.FromBase64String(
            "TIvBSGPKSGnBLAMAAGZCg7wALgEAAAsPhf8AAABmQoO8ACwBAAACD4XvAAAAQg+/hAAyAQAAQTvBD4XdAAAASGnBywAAALkyAAAAQSuMgNwBAABBK4yA2AEAAEErjIDUAQAAQSuMgNABAABBK4yAzAEAAEErjIDIAQAAQSuMgMQBAABBK4yAwAEAAEErjIC8AQAAQSuMgLgBAABBK4yAtAEAAEErjICwAQAAQSuMgKwBAABBK4yAqAEAAEErjICkAQAAQSuMgKABAABBK4yAnAEAAEErjICYAQAAQSuMgJQBAABBK4yAkAEAAEErjICMAQAAQSuMgIgBAABBK4yAhAEAAEErjICAAQAAQSuMgHwBAAA7TCQofAOLwsMzwMM=");

        // Validates type/alive, NOT owner; returns max(0,50-sum(all 25 native fields)).
        internal const int ArmoryFreeRva = 0xC6FC0, ArmoryFreeLength = 239;
        internal const string ArmoryFreeSha256 = "0B81270A791DCD85712CAED2675487E9F819E96F75BF0E464B6A16F1B819BBBA";
        // Three complete instructions: 3+7+9; continuation begins at JNE.
        internal const int ArmoryFreeDisplacedLength = 19, ArmoryFreeContinuationRva = 0xC6FD3;
        internal static readonly byte[] ArmoryFreeBytes = Convert.FromBase64String(
            "TGPCSWnALAMAAGaDvAgsAQAAAg+F0wAAAGaDvAguAQAACw+FxAAAAElpwMsAAAC6MgAAACuUgdwBAAArlIHYAQAAK5SB1AEAACuUgdABAAArlIHMAQAAK5SByAEAACuUgcQBAAArlIHAAQAAK5SBvAEAACuUgbgBAAArlIG0AQAAK5SBsAEAACuUgawBAAArlIGoAQAAK5SBpAEAACuUgaABAAArlIGcAQAAK5SBmAEAACuUgZQBAAArlIGQAQAAK5SBjAEAACuUgYgBAAArlIGEAQAAK5SBgAEAACuUgXwBAAC4AAAAAA9JwsMzwMM=");

        // Signed max(0,capacity-currentStack), with NO type/alive/owner/global checks.
        // Manager+slot*0x32C+0x1E0 is GameBuilding+0x184. Capacity is normally 250,
        // not a guessed r_MaxGoodStackAmount. The good argument is unused.
        internal const int GranaryFreeRva = 0xBD470, GranaryFreeLength = 32;
        internal const string GranaryFreeSha256 = "AAC3392CAE0B5C8A11B067C46579C7E4D53499F21EB42F97EF910028806C6F9D";
        // Three complete instructions: 3+7+7; continuation begins at CMP.
        internal const int GranaryFreeDisplacedLength = 17, GranaryFreeContinuationRva = 0xBD481;
        internal static readonly byte[] GranaryFreeBytes = Convert.FromBase64String(
            "SGPCSGnQLAMAAIuECuABAABBO8F8AzPAw0QryEGLwcM=");

        // BF9F0's Granary branch does NOT call BD470. It validates alive/type/owner,
        // calls C05B0, compares its returned aggregate with 250, then retains native
        // reachability and candidate looping. C05B0 also writes native derived totals:
        // adapt only this caller's RESULT, never globally substitute that function.
        internal const int GranaryFinderRva = 0xBF9F0, GranaryFinderLength = 348;
        internal const string GranaryFinderSha256 = "2364934596CE1B4EA9AF8A0D4E8A82D11E2340A2AEFEE9044D2C2712BE55F2FB";
        internal const int GranaryRecomputeRva = 0xC05B0, GranaryRecomputeLength = 127;
        internal const string GranaryRecomputeSha256 = "67E2476FF157F79A62A5727F69989B8311C6590C20B5AFD6348B3195C0DD14D3";

        // Inline [BFB05,BFB14): MOV EDX,EBX; MOV RCX,RSI; CALL C05B0; CMP EAX,250.
        // A relocated CALL must return into the stub, NOT overwritten BFB0F. Run that
        // native call once, add custom occupancy only to EAX with safe saturation,
        // replay CMP and continue at original JGE. Preserve caller-live registers;
        // RSI is manager, EBX is slot. C90E0 reachability/retry remain untouched.
        // Full finder decode and 236382 captured static xrefs contain no incoming
        // target in the strict interior. Complete instructions and continuation were
        // verified from the pinned image; this is static qualification, not a launch.
        internal const int GranaryFinderApertureRva = 0xBFB05, GranaryFinderApertureLength = 15;
        internal const int GranaryFinderCallRva = 0xBFB0A, GranaryFinderContinuationRva = 0xBFB14;
        internal const int GranaryFinderRetryRva = 0xBFB27, GranaryFinderReachabilityCallRva = 0xBFB1E;
        internal const bool GranaryFinderInlineBindingAudited = true;
        internal const string GranaryFinderApertureSha256 = "0CC2316EA54729A2C0111BB8D175740347A3CFAA497FF4389F27DBDF8028769C";
        internal static readonly byte[] GranaryFinderApertureBytes = Convert.FromBase64String("i9NIi87ooQoAAD36AAAA");
        // JGE BFB27; XOR R8D,R8D; MOV EDX,EBX; MOV RCX,RSI; CALL C90E0; TEST EAX,EAX.
        internal static readonly byte[] GranaryFinderContinuationBytes =
            new byte[] { 0x7D, 0x11, 0x45, 0x33, 0xC0, 0x8B, 0xD3, 0x48, 0x8B, 0xCE, 0xE8, 0xBD, 0x95, 0x00, 0x00, 0x85, 0xC0 };

        // B6100 is already Script-Extender-owned. Coordinate its managed Original
        // invocation with final Pre args/opaque Int64; do not add a native entry hook.
        // Its supplied capacity bounds only selected-good count, NOT pooled space.
        // Capacity zero clears selected stock even when bAdd==0. A neutral refusal
        // must not invoke that branch. This does not fix producer success/accounting tails.
        internal const int AddGoodRva = 0xB6100, AddGoodLength = 281;
        internal const string AddGoodSha256 = "AA634FA3A59866B105E7C577FEAB60B7662516592C757332F007299D6488A2D1";
    }
}
