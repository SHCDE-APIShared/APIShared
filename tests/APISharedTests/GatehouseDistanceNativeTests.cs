using APIShared;
using Iced.Intel;
using RedBird.X64.Hooks;
using System;
using System.Runtime.InteropServices;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    internal static partial class GateBridgeAutomationTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeGateDistance(int beginX, int beginY);

        private static void RunDistanceOrigin(Action<bool, string> assert, byte[] installedImage)
        {
            const int unitSlot = 0x490;
            const int unitX = 0x67E8B0E, unitY = 0x67E8B10;
            const int gridSize = 0x64CCD04, accessX = 0x64CCD0A, accessY = 0x64CCD0C;
            const int originFlag = OutputRva + 0x400;
            IntPtr allocation = VirtualAlloc(IntPtr.Zero, (UIntPtr)0x7000000, 0x3000, 0x40);
            if (allocation == IntPtr.Zero) throw new InvalidOperationException("Gatehouse distance test allocation failed.");
            ulong module = unchecked((ulong)allocation.ToInt64());
            try
            {
                const int start = GatehousePermanentRuntimeState.DistanceHookRva;
                const int count = GatehousePermanentRuntimeState.DistanceDisplacedBytes;
                Marshal.Copy(installedImage, start, Ptr(module + start), count);
                var endpoint = new Assembler(64);
                endpoint.mov(eax, r8d);
                endpoint.pop(r15);
                endpoint.pop(r12);
                endpoint.pop(rbp);
                endpoint.pop(rbx);
                endpoint.ret();
                Write(endpoint, module + start + count);

                var wrapper = new Assembler(64);
                wrapper.push(rbx);
                wrapper.push(rbp);
                wrapper.push(r12);
                wrapper.push(r15);
                // Registers at B7B70: gate slot, module base, candidate offset and begin coordinates.
                wrapper.mov(rbx, Slot);
                wrapper.mov(rbp, module);
                wrapper.mov(r15d, ecx);
                wrapper.mov(r12d, edx);
                wrapper.mov(edx, unitSlot);
                wrapper.jmp(module + start);
                Write(wrapper, module + WrapperRva);
                var distance = Marshal.GetDelegateForFunctionPointer<NativeGateDistance>(Ptr(module + WrapperRva));

                using (var hook = new X64InlineHook(module + start, count))
                {
                    assert(hook.DisplacedByteCount == count, "distance hook matches the complete native block");
                    hook.Generate((asm, displaced, continuation) =>
                        GatehousePermanentRuntimeState.GenerateDistanceOrigin(asm, displaced.ToArray(), module + originFlag));
                    hook.Enable();
                    // The editor uses tile * 8 + 4 for unit positions. Anchor the expected centers
                    // independently so the managed helper and native stub cannot share an offset bug.
                    foreach (var (beginX, beginY, size, centerX, centerY) in new[]
                    {
                        (100, 50, 7, 103 * 8 + 4, 53 * 8 + 4),
                        (100, 50, 5, 102 * 8 + 4, 52 * 8 + 4),
                        (0, 0, 2, 8, 8)
                    })
                    {
                        Marshal.WriteInt32(Ptr(module + gridSize + Slot), size);
                        foreach (var (deltaX, deltaY) in new[] { (0, 0), (31, 0), (-31, 0), (0, 31), (0, -31), (-17, 43) })
                        {
                            int candidateX = centerX + deltaX, candidateY = centerY + deltaY;
                            Marshal.WriteInt16(Ptr(module + unitX + unitSlot), checked((short)candidateX));
                            Marshal.WriteInt16(Ptr(module + unitY + unitSlot), checked((short)candidateY));
                            Marshal.WriteInt32(Ptr(module + originFlag), 1);
                            int centeredDistance = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
                            foreach (var (x, y) in new[] { (beginX + size / 2, beginY - 1), (beginX + size, beginY + size / 2) })
                            {
                                Marshal.WriteInt16(Ptr(module + accessX + Slot), (short)x);
                                Marshal.WriteInt16(Ptr(module + accessY + Slot), (short)y);
                                assert(distance(beginX, beginY) == centeredDistance,
                                    "the production native center agrees with editor coordinates for every approach and access tile");
                            }
                            Marshal.WriteInt32(Ptr(module + originFlag), 0);
                            int vanillaDistance = Math.Max(Math.Abs(beginX * 8 - candidateX), Math.Abs(beginY * 8 - candidateY));
                            assert(distance(beginX, beginY) == vanillaDistance,
                                "the disabled center executes the original corner distance");
                        }
                    }

                    // Mirror editor placements at 25 tiles and move each one fine unit inward/outward.
                    Marshal.WriteInt32(Ptr(module + gridSize + Slot), 7);
                    Marshal.WriteInt32(Ptr(module + originFlag), 1);
                    foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, 1) })
                    {
                        foreach (int expected in new[] { 200, 199, 201 })
                        {
                            Marshal.WriteInt16(Ptr(module + unitX + unitSlot), (short)(103 * 8 + 4 + dx * expected));
                            Marshal.WriteInt16(Ptr(module + unitY + unitSlot), (short)(53 * 8 + 4 + dy * expected));
                            assert(distance(100, 50) == expected,
                                $"native centered distance differs at ({dx},{dy}), distance {expected}");
                        }
                    }

                    Marshal.WriteInt16(Ptr(module + unitX + unitSlot), 1000);
                    Marshal.WriteInt16(Ptr(module + unitY + unitSlot), 400);
                    Marshal.WriteInt32(Ptr(module + originFlag), 1);
                    foreach (int invalidSize in new[] { 0, 14, -1 })
                    {
                        Marshal.WriteInt32(Ptr(module + gridSize + Slot), invalidSize);
                        assert(distance(100, 50) == 200, "an invalid grid size takes the native corner path");
                    }
                }
                Console.WriteLine("PASS: productive native gatehouse distance agrees with square footprint geometry and the Vanilla corner fallback.");
            }
            finally
            {
                VirtualFree(allocation, UIntPtr.Zero, 0x8000);
            }
        }
    }
}
