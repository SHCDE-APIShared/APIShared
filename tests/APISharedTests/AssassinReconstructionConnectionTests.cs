using APIShared;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.X64.Hooks;
using System;
using System.IO;
using System.Runtime.InteropServices;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void AssassinReconstruct(IntPtr manager, int startX, int startY, int targetX, int targetY, int mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

        // The installed E1640 body runs inside a scratch image at its own RVA, so every RIP-relative
        // table and global keeps its native layout. A straight route target -> start contains one
        // one-way step: the start-side tile has the connection bit, the target-side tile does not.
        [TestMethod]
        [TestCategory("NativeContracts")]
        public void AssassinReconstructionAcceptsStartSideConnectionOnlyWhenEnabled()
        {
            byte[] image = MapPeImage(File.ReadAllBytes(Path.Combine(GetGameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll")));
            const int reconstructRva = 0xE1640, managerRva = 0x60AD660, scratchRva = 0x6600000;
            const int walkableRva = 0x3A11EA4, rowsRva = 0x402FF2C, distanceRva = 0x5225B10, stampRva = 0x52C2550;
            const int flagsRva = 0x48F71B0, buildingRva = 0x4B6AA50, directionRva = 0x2D2E50;
            int[] repairCallees = { 0xDAA50, 0xE49D0, 0xC3E50, 0xDE6A0 };
            const int successCallee = 0xE4E90, specialPredicate = 0x107160;
            IntPtr allocation = VirtualAlloc(IntPtr.Zero, (UIntPtr)(scratchRva + 0x10000), 0x3000, 0x40);
            Assert(allocation != IntPtr.Zero, "reconstruction fixture allocation");
            ulong module = unchecked((ulong)allocation.ToInt64());
            IntPtr At(long rva) => new IntPtr(allocation.ToInt64() + rva);
            X64InlineHook hook = null;
            try
            {
                Marshal.Copy(image, reconstructRva, At(reconstructRva), 0x600);
                Marshal.Copy(image, AssassinPathNativeDefinition.DirectionMasksRva, At(AssassinPathNativeDefinition.DirectionMasksRva), 8);
                Marshal.Copy(image, directionRva, At(directionRva), 64);
                // Callees count their calls (scratch+0 repair, +4 success, +8 special predicate).
                for (int index = 0; index < repairCallees.Length + 2; index++)
                {
                    int rva = index < repairCallees.Length ? repairCallees[index] : index == repairCallees.Length ? successCallee : specialPredicate;
                    int counter = index < repairCallees.Length ? 0 : index == repairCallees.Length ? 4 : 8;
                    var stub = new Assembler(64);
                    stub.mov(rax, module + scratchRva + (ulong)counter);
                    stub.inc(__dword_ptr[rax]);
                    stub.xor(eax, eax);
                    stub.ret();
                    byte[] code = AssembleAndDecode(stub, module + (ulong)rva);
                    Marshal.Copy(code, 0, At(rva), code.Length);
                }
                IntPtr flags = At(scratchRva + 0x100);
                IntPtr directions = At(scratchRva + 0x1000);
                // Synthetic row-major grid with the native direction table (dx, dy pairs).
                for (int y = 0; y < 800; y++)
                {
                    Marshal.WriteInt32(At(rowsRva + y * 12), y * 800);
                    for (int direction = 0; direction < 8; direction++)
                    {
                        int dx = Marshal.ReadInt32(At(directionRva + direction * 8));
                        int dy = Marshal.ReadInt32(At(directionRva + direction * 8 + 4));
                        Marshal.WriteInt32(At(AssassinPathNativeDefinition.NeighborOffsetsRva + (y * 8 + direction) * 4), dx + dy * 800);
                    }
                }
                Assert(Marshal.ReadInt32(At(directionRva + 2 * 8)) == 1 && Marshal.ReadInt32(At(directionRva + 6 * 8)) == -1,
                    "native direction 2 is east and 6 is west");
                const int y0 = 10, startX = 10, targetX = 14, stamp = 7;
                Func<int, int> tile = x => y0 * 800 + x;
                Marshal.WriteByte(At(walkableRva + tile(startX)), 1);
                Marshal.WriteByte(At(walkableRva + tile(targetX)), 1);
                for (int x = startX; x <= targetX; x++)
                {
                    Marshal.WriteInt16(At(distanceRva + tile(x) * 2), (short)(x - startX + 1));
                    Marshal.WriteInt16(At(stampRva + tile(x) * 2), stamp);
                    Marshal.WriteInt32(At(flagsRva + tile(x) * 4), 0);
                    Marshal.WriteInt16(At(buildingRva + tile(x) * 2), 0);
                    byte connections = 0;
                    if (x < targetX) connections |= 1 << 2; // east, toward the target
                    if (x > startX && x != 13) connections |= 1 << 6; // west, missing on the one-way step 13 -> 12
                    Marshal.WriteByte(At(AssassinPathNativeDefinition.ConnectionsRva + tile(x)), connections);
                }
                hook = new X64InlineHook(module + 0xE19C0, 14);
                hook.Generate((asm, displaced, continuation) =>
                {
                    AssassinEndpointEmitter.EmitReverseConnection(asm, displaced, module, unchecked((ulong)flags.ToInt64()));
                    AssembleAndDecode(asm, module + scratchRva + 0x2000);
                });
                Assert(hook.DisplacedByteCount == AssassinPathNativeDefinition.Lengths[AssassinPathNativeDefinition.ReverseConnectionSite],
                    "installed backend displaces the complete 14-byte E19C0 span");
                hook.Enable();
                var reconstruct = Marshal.GetDelegateForFunctionPointer<AssassinReconstruct>(At(reconstructRva));
                IntPtr manager = At(managerRva);
                Marshal.WriteIntPtr(manager, 0x155F60, directions);

                int Run(int policy, int mode)
                {
                    Marshal.WriteInt32(flags, policy);
                    Marshal.WriteInt32(At(scratchRva), 0); Marshal.WriteInt32(At(scratchRva + 4), 0);
                    Marshal.WriteInt32(manager, 0x155F68, 0);
                    for (int index = 0; index < 8; index++) Marshal.WriteByte(directions, index, 0xEE);
                    reconstruct(manager, startX, y0, targetX, y0, mode);
                    return Marshal.ReadInt32(manager, 0x155F68);
                }

                Run(0, 3);
                Assert(Marshal.ReadInt32(At(scratchRva)) == 4 && Marshal.ReadInt32(At(scratchRva + 4)) == 0,
                    "disabled policy keeps Vanilla's failure and its complete four-call repair");
                Run(1, 1);
                Assert(Marshal.ReadInt32(At(scratchRva)) == 4 && Marshal.ReadInt32(At(scratchRva + 4)) == 0,
                    "mode 1 never reaches the Assassin site, even with the policy enabled");
                foreach (int policy in new[] { 1, 2, 3 })
                {
                    int length = Run(policy, 3);
                    Assert(Marshal.ReadInt32(At(scratchRva)) == 0 && Marshal.ReadInt32(At(scratchRva + 4)) == 1,
                        "enabled policy reconstructs without repair (flag " + policy + ")");
                    Assert(length == targetX - startX, "complete target -> start route length (flag " + policy + ")");
                    // Recorded steps point back toward the target (east), two nibbles per byte.
                    Assert(Marshal.ReadByte(directions, 0) == 0x22 && Marshal.ReadByte(directions, 1) == 0x22,
                        "native packed directions of the reconstructed route (flag " + policy + ")");
                }
                // The start-side bit is still required: without it the step stays rejected.
                Marshal.WriteByte(At(AssassinPathNativeDefinition.ConnectionsRva + tile(12)), 1 << 6);
                Run(2, 3);
                Assert(Marshal.ReadInt32(At(scratchRva)) == 4, "a step without either connection bit still fails");
                Assert(Marshal.ReadInt32(At(scratchRva + 8)) == 0, "mode 3 fixture never calls the special-surface predicate");
            }
            finally
            {
                hook?.Dispose();
                VirtualFree(allocation, UIntPtr.Zero, 0x8000);
            }
        }
    }
}
