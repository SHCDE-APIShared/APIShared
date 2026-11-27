using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    // The production decision emitter executes on one owned 64 KiB allocation. Its callback
    // and exits are synthetic ABI boundaries, not game routines or managed/native transitions.
    // Full-generator decoding separately verifies replay, demand and native destinations.
    [TestClass, DoNotParallelize]
    public sealed class WeaponStoredDeliveryAssemblyTests
    {
        private const ulong ArithmeticFlags = 0x8D5, InitialFlags = 0x246;
        private const ulong Guard = 0x7162534455667788UL, SimdSeed = 0x61728394A5B6C7D8UL;
        private const int NativeExit = 1, AcceptedExit = 2, RefusedExit = 3;
        private const int CallbackCalls = 0, NativeCalls = 4, ExitMarker = 8;
        private const int EntryRsp = 16, ExitRsp = 24, CallbackRsp = 32, Flags = 40;
        private const int CallbackUnitId = 48, MutableCurrentUnit = 52, CallbackFlags = 56, CapturedUnitId = 37;
        private const int ReturnedGp = 64, ReturnedSimd = 192, FirstGuard = 0x2F0, LastGuard = 0x3F8;
        private static readonly AssemblerRegister64[] Gp = {
            rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8, r9, r10, r11, r12, r13, r14, r15 };
        private static readonly AssemblerRegisterXMM[] Simd = {
            xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7, xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15 };
        private static ulong GpSeed(int index) => 0x1122334400000000UL + (ulong)index * 0x01010101UL;

        private const ulong TestImage = 0x180000000, Enabled = 0x610000000, BuildingDemand = 0x620000000, Callback = 0x710000000000;
        private const ulong WorkerWire = 0x630000000, StoredOptions = 0x640000000;
        // These are independently qualified native exits/register roles. Pinning them is
        // necessary: a self-consistent but wrong repair can corrupt a different worker.
        private static readonly SiteContract[] Contracts = {
            new SiteContract(eStructs.STRUCT_FLETCHERS_WORKSHOP, 0x12DC6D, "460FBF84238E090000418BD6488BCD", 0x12DEB6, 0x12E3DB,
                Register.R14, Register.RBX, Register.RDI, Register.RSI, Register.R15, Register.R12, Register.RBP),
            new SiteContract(eStructs.STRUCT_POLETURNERS_WORKSHOP, 0x13B654, "8BD5460FBF84238E090000488BCF", 0x13B894, 0x13BDAA,
                Register.RBP, Register.RBX, Register.RSI, Register.R13, Register.R14, Register.R12, Register.RDI),
            new SiteContract(eStructs.STRUCT_BLACKSMITHS_WORKSHOP, 0x13C7DC, "8BD5460FBF84238E090000488BCE", 0x13CA2E, 0x13CF02,
                Register.RBP, Register.RBX, Register.R13, Register.RDI, Register.R14, Register.R12, Register.RSI),
            new SiteContract(eStructs.STRUCT_ARMOURERS_WORKSHOP, 0x13D97E, "460FBF842B8E090000488D1D22F23806", 0x13DB8C, 0x13E085,
                Register.R15, Register.R14, Register.RBP, Register.RSI, Register.RDI, Register.R13, Register.RBX),
            new SiteContract(eStructs.STRUCT_TANNERS_WORKSHOP, 0x13EEAF, "488D35FADC3806460FBF84238E090000", 0x13F0BC, 0x13E6C2,
                Register.RBX, Register.RCX, Register.RDI, Register.R13, Register.RBP, Register.R12, Register.RSI)
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ExecuteFixture();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
        [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [TestMethod]
        [DataRow(0, NativeExit)]
        [DataRow(1, AcceptedExit)]
        [DataRow(2, RefusedExit)]
        [DataRow(-1, RefusedExit)]
        [DataRow(3, RefusedExit)]
        public void ProductionDecisionUsesOnlyItsInt32ResultAndRestoresEveryNativeMachineValue(int decision, int expectedExit)
        {
            RequireWindowsX64();
            using (var fixture = new DecisionFixture(decision))
            {
                fixture.Run();
                AssertMachineValues(fixture, expectedExit, false);
            }
        }

        [TestMethod]
        [DataRow(0, NativeExit)]
        [DataRow(1, AcceptedExit)]
        [DataRow(2, RefusedExit)]
        public void CapturedWorkerSurvivesGlobalMutationAndChangesOnlyAcceptedIdentity(int decision, int expectedExit)
        {
            RequireWindowsX64();
            using (var fixture = new DecisionFixture(decision, true))
            {
                fixture.Run();
                Assert.AreEqual(CapturedUnitId, fixture.Int32(CallbackUnitId), "Pass the pre-call physical ID as the Int32 callback argument.");
                Assert.AreEqual(888, fixture.Int32(MutableCurrentUnit), "The callback really changed the synthetic current-unit global.");
                AssertMachineValues(fixture, expectedExit, expectedExit == AcceptedExit);
            }
        }

        [TestMethod]
        [DataRow(0, NativeExit)]
        [DataRow(1, AcceptedExit)]
        [DataRow(2, RefusedExit)]
        public void IncomingDirectionFlagIsClearedForCallbackAndRestoredForEveryExit(int decision, int expectedExit)
        {
            RequireWindowsX64();
            using (var fixture = new DecisionFixture(decision, false, true))
            { fixture.Run(); AssertMachineValues(fixture, expectedExit, false); }
        }

        [TestMethod]
        [DataRow(1, 0, NativeExit, 0)]
        [DataRow(1, 1, AcceptedExit, 1)]
        [DataRow(1, 2, NativeExit, 0)]
        [DataRow(0, 1, NativeExit, 0)]
        [DataRow(31, 1, AcceptedExit, 1)]
        [DataRow(32, 1, NativeExit, 0)]
        [DataRow(255, 1, NativeExit, 0)]
        public void ProductionDemandGateCallsOnlyForAnExactStoredOutputWorkerRow(int option, int flag, int expectedExit, int callbackCalls)
        {
            RequireWindowsX64();
            using (var fixture = new DecisionFixture(1, false, true, option, flag))
            { fixture.Run(); AssertMachineValues(fixture, expectedExit, false, callbackCalls); }
        }

        private static void AssertMachineValues(DecisionFixture fixture, int expectedExit, bool capturedAccepted, int callbackCalls = 1)
        {
            Assert.AreEqual(expectedExit, fixture.Int32(ExitMarker));
            Assert.AreEqual(callbackCalls, fixture.Int32(CallbackCalls), "Dormant rows bypass managed code; active rows ask exactly once.");
            Assert.AreEqual(expectedExit == NativeExit ? 1 : 0, fixture.Int32(NativeCalls),
                "Only the Native decision enters the synthetic ordinary-replay boundary.");
            if (callbackCalls != 0)
                Assert.AreEqual(8UL, fixture.UInt64(CallbackRsp) & 15UL, "Callback entry must follow the Windows x64 stack ABI.");
            Assert.AreEqual(0UL, fixture.UInt64(EntryRsp) & 15UL, "The fixture enters an aligned worker interior.");
            Assert.AreEqual(fixture.UInt64(EntryRsp), fixture.UInt64(ExitRsp), "Every decision must unwind its entire callback frame.");
            Assert.AreEqual(fixture.IncomingFlags & ArithmeticFlags, fixture.UInt64(Flags) & ArithmeticFlags,
                "The decision branches must not leak callback/CMP flags into the native continuation.");
            Assert.AreEqual(fixture.IncomingFlags & 0x400UL, fixture.UInt64(Flags) & 0x400UL, "Restore the incoming direction flag.");
            if (callbackCalls != 0)
                Assert.AreEqual(0UL, fixture.UInt64(CallbackFlags) & 0x400UL, "The Windows x64 callback must enter with DF clear.");
            for (int index = 0; index < Gp.Length; index++)
                Assert.AreEqual(index == 4 ? fixture.UInt64(EntryRsp) : capturedAccepted && index == 14 ? (ulong)CapturedUnitId : GpSeed(index),
                    fixture.UInt64(ReturnedGp + index * 8), "Restore the original register before branching: " + Gp[index]);
            for (int index = 0; index < Simd.Length; index++)
            {
                Assert.AreEqual(SimdSeed + (ulong)index, fixture.UInt64(ReturnedSimd + index * 16), "SIMD low lane " + index);
                Assert.AreEqual(SimdSeed + (ulong)index, fixture.UInt64(ReturnedSimd + index * 16 + 8), "SIMD high lane " + index);
            }
            Assert.AreEqual(Guard, fixture.UInt64(FirstGuard));
            Assert.AreEqual(Guard, fixture.UInt64(LastGuard));
        }

        [TestMethod]
        public void EveryWorkshopReplaysOnlyNativeAndRepairsOnlyTheAcceptedFooter()
        {
            foreach (SiteContract contract in Contracts)
            {
                WeaponStoredDeliverySite site = WeaponProductionNativeDefinition.StoredDeliverySites.Single(s => s.Building == contract.Building);
                Assert.AreEqual(contract.Rva, site.Rva);
                CollectionAssert.AreEqual(Hex(contract.Bytes), site.Bytes);
                Assert.AreEqual(contract.Footer, site.FooterRva); Assert.AreEqual(contract.Epilogue, site.EpilogueRva);
                CollectionAssert.AreEqual(contract.Registers, new[] { site.UnitIdRegister, site.UnitOffsetRegister, site.ZeroRegister,
                    site.OneRegister, site.ThreeRegister, site.UnitManagerRegister, site.BuildingManagerRegister });

                // RIP-relative manager LEAs in the original prefix require a near-image
                // stub. The callback is deliberately outside rel32 range in every case.
                foreach (ulong image in new[] { TestImage, 0x700000000000UL })
                foreach (long displacement in new[] { 0x20000000L, -0x10000000L })
                {
                    ulong stub = unchecked((ulong)((long)image + displacement));
                    byte[] bytes = WeaponProductionNativeAssembly.ExpectedStoredDeliveryStub(site, image, stub, Enabled, BuildingDemand, WorkerWire, StoredOptions, Callback);
                    Instruction[] original = Decode(site.Bytes, image + (uint)site.Rva);
                    Assert.IsTrue(original.All(i => i.FlowControl == FlowControl.Next),
                        "These early apertures end before the ordinary storage/reselection call.");
                    foreach (int decision in new[] { 0, 1, 2, -1, 3 })
                    {
                        DeliveryPath path = Walk(bytes, stub, site, image, -1, decision);
                        Assert.AreEqual(1, path.Calls.Count, "An active delivery must ask once.");
                        Assert.AreEqual(Callback, path.Calls[0]);
                        Assert.AreEqual(decision == 0 ? image + (uint)site.Rva + (uint)site.Bytes.Length :
                            image + (uint)(decision == 1 ? contract.Footer : contract.Epilogue), path.Destination);
                        Assert.AreEqual(decision == 0 ? 1 : 0, CountReplay(path.Instructions, original),
                            "Native replays its entire prefix exactly once; custom exits skip it entirely.");
                        if (decision == 1) AssertFooterRepair(path, contract, image);
                        else if (decision != 0)
                        {
                            int restored = path.Instructions.FindLastIndex(i => i.Mnemonic == Mnemonic.Popfq);
                            Assert.IsTrue(restored >= 0);
                            Assert.IsTrue(path.Instructions.Skip(restored + 1).All(i => i.FlowControl == FlowControl.UnconditionalBranch ||
                                i.FlowControl == FlowControl.IndirectBranch), "Refusal must preserve worker state through the exact epilogue.");
                        }
                    }
                    // Disabled service, invalid unit/building, zero home demand, invalid
                    // option index and a non-custom worker each bypass the callback frame.
                    for (int rejectGuard = 0; rejectGuard < 8; rejectGuard++)
                    {
                        DeliveryPath path = Walk(bytes, stub, site, image, rejectGuard, 1);
                        Assert.AreEqual(0, path.Calls.Count);
                        Assert.AreEqual(image + (uint)site.Rva + (uint)site.Bytes.Length, path.Destination);
                        Assert.AreEqual(1, CountReplay(path.Instructions, original));
                        Assert.IsFalse(path.Instructions.Any(i => i.Mnemonic == Mnemonic.Movdqu || i.Mnemonic == Mnemonic.Cld ||
                            (i.Op0Register == Register.RSP && (i.Mnemonic == Mnemonic.And || i.Mnemonic == Mnemonic.Sub))),
                            "A rejected demand guard must not prepare a callback frame.");
                    }
                }
            }
        }

        [TestMethod]
        public void ChangedNativeInstructionsOrCallbackBindingAreRejectedBeforeEmission()
        {
            foreach (WeaponStoredDeliverySite site in WeaponProductionNativeDefinition.StoredDeliverySites)
            {
                Instruction[] original = Decode(site.Bytes, TestImage + (uint)site.Rva);
                ulong back = TestImage + (uint)site.Rva + (uint)site.Bytes.Length;
                for (int index = 0; index < original.Length; index++)
                {
                    var changed = (Instruction[])original.Clone(); changed[index].IP++;
                    Reject(site, changed, back, TestImage, Enabled, BuildingDemand, Callback);
                    changed = (Instruction[])original.Clone(); changed[index].HasLockPrefix = true;
                    Reject(site, changed, back, TestImage, Enabled, BuildingDemand, Callback);
                }
                var changedOperand = (Instruction[])original.Clone(); changedOperand[0].Op0Register = Register.RAX;
                Reject(site, changedOperand, back, TestImage, Enabled, BuildingDemand, Callback);
                Reject(site, original.Take(original.Length - 1).ToArray(), back, TestImage, Enabled, BuildingDemand, Callback);
                Reject(site, original, back + 1, TestImage, Enabled, BuildingDemand, Callback);
                Reject(site, original, back, TestImage + 1, Enabled, BuildingDemand, Callback);
                Reject(site, original, back, TestImage, 0, BuildingDemand, Callback);
                Reject(site, original, back, TestImage, Enabled, 0, Callback);
                Reject(site, original, back, TestImage, Enabled, BuildingDemand, 0);
                Reject(site, original, back, TestImage, Enabled, BuildingDemand, Callback, 0, StoredOptions);
                Reject(site, original, back, TestImage, Enabled, BuildingDemand, Callback, WorkerWire, 0);
            }
        }

        [TestMethod]
        public void TannerStoredDeliveryRequiresBothUnchangedAdjacentCallBoundaries()
        {
            var image = new byte[0x13EF48];
            byte[] yieldBytes = Hex("E844ED0400"), route = Hex("468B8421F8060000488BCEE8B871F7FF");
            Array.Copy(yieldBytes, 0, image, 0x13EBF7, yieldBytes.Length);
            Array.Copy(route, 0, image, 0x13EF38, route.Length);
            Assert.IsTrue(WeaponProductionNativeDefinition.HasVanillaStoredDeliveryRoute(image, eStructs.STRUCT_TANNERS_WORKSHOP));
            foreach (int boundary in new[] { 0x13EBF7, 0x13EF38 })
            {
                image[boundary] ^= 1;
                Assert.IsFalse(WeaponProductionNativeDefinition.HasVanillaStoredDeliveryRoute(image, eStructs.STRUCT_TANNERS_WORKSHOP),
                    "Either changed caller block must refuse the new Tanner stored-output capability.");
                foreach (SiteContract contract in Contracts.Where(c => c.Building != eStructs.STRUCT_TANNERS_WORKSHOP))
                    Assert.IsTrue(WeaponProductionNativeDefinition.HasVanillaStoredDeliveryRoute(image, contract.Building));
                image[boundary] ^= 1;
            }
            Assert.IsTrue(WeaponProductionNativeDefinition.HasVanillaStoredDeliveryRoute(image, eStructs.STRUCT_TANNERS_WORKSHOP));
        }

        [TestMethod]
        public unsafe void RetargetWritesOnlyAuthorizedPositiveCargoDeliveryStateAndTimerWords()
        {
            Assert.IsFalse(WeaponProductionNativeRuntime.TryRetargetStoredDelivery((GameUnit*)1, eStructs.STRUCT_FLETCHERS_WORKSHOP, false),
                "Permission denial must precede even the first pointer read.");
            Assert.IsFalse(WeaponProductionNativeRuntime.TryRetargetStoredDelivery(null, eStructs.STRUCT_FLETCHERS_WORKSHOP, true));
            foreach (SiteContract contract in Contracts)
            {
                ushort delivery = contract.Building == eStructs.STRUCT_FLETCHERS_WORKSHOP ? (ushort)7 : (ushort)8;
                foreach (short cargo in new[] { short.MinValue, (short)-1, (short)0, (short)1, short.MaxValue })
                foreach (ushort state in new[] { delivery, (ushort)0, (ushort)0x6E })
                foreach (bool authorized in new[] { false, true })
                {
                    // A nonzero pattern exposes wide writes into both neighboring WORDs,
                    // destination IDs/global IDs, cargo, and every other worker field.
                    byte[] worker = Enumerable.Range(0, 0x490).Select(i => unchecked((byte)(i * 37 + 0xA5))).ToArray();
                    Array.Copy(BitConverter.GetBytes(state), 0, worker, 0x2BC, 2);
                    Array.Copy(BitConverter.GetBytes((ushort)0xFFFF), 0, worker, 0x2C4, 2);
                    Array.Copy(BitConverter.GetBytes(cargo), 0, worker, 0x384, 2);
                    byte[] expected = (byte[])worker.Clone();
                    bool changed = authorized && state == delivery && cargo > 0;
                    if (changed)
                    {
                        Array.Copy(BitConverter.GetBytes((ushort)(delivery - 1)), 0, expected, 0x2BC, 2);
                        Array.Copy(BitConverter.GetBytes((ushort)0), 0, expected, 0x2C4, 2);
                    }
                    fixed (byte* pointer = worker)
                        Assert.AreEqual(changed, WeaponProductionNativeRuntime.TryRetargetStoredDelivery((GameUnit*)pointer, contract.Building, authorized));
                    CollectionAssert.AreEqual(expected, worker, "Only exact state/timer WORDs may change; all native neighbors remain intact.");
                }
            }
            var unsupported = new byte[0x490];
            Array.Copy(BitConverter.GetBytes((ushort)7), 0, unsupported, 0x2BC, 2);
            Array.Copy(BitConverter.GetBytes((short)1), 0, unsupported, 0x384, 2);
            byte[] unchanged = (byte[])unsupported.Clone();
            fixed (byte* pointer = unsupported)
                Assert.IsFalse(WeaponProductionNativeRuntime.TryRetargetStoredDelivery((GameUnit*)pointer, (eStructs)0, true));
            CollectionAssert.AreEqual(unchanged, unsupported);
        }

        private static void AssertFooterRepair(DeliveryPath path, SiteContract contract, ulong image)
        {
            int callback = path.Instructions.FindIndex(i => i.FlowControl == FlowControl.Call || i.FlowControl == FlowControl.IndirectCall);
            Assert.IsTrue(path.Instructions.Take(callback).Any(i => i.Mnemonic == Mnemonic.Mov &&
                i.Op1Kind == OpKind.Immediate64 && i.Immediate64 == image + 0x9302C4), "Snapshot the physical worker before calling managed code.");
            Assert.IsTrue(path.Instructions.Skip(callback + 1).Any(i => i.Mnemonic == Mnemonic.Mov &&
                i.Op0Register == Low32(contract.Registers[0]) && i.Op1Kind == OpKind.Memory && i.MemoryBase == Register.RSP),
                "Accepted identity must come from the private captured slot.");
            Assert.IsFalse(path.Instructions.Skip(callback + 1).Any(i => i.Mnemonic == Mnemonic.Mov &&
                i.Op1Kind == OpKind.Immediate64 && i.Immediate64 == image + 0x9302C4), "Never reread a mutable current-unit global after the callback.");
            Instruction[] repair = path.Instructions.Skip(path.Instructions.FindLastIndex(i => i.Mnemonic == Mnemonic.Popfq) + 1).ToArray();
            Assert.IsTrue(repair.Any(i => i.Mnemonic == Mnemonic.Imul && i.Op0Register == contract.Registers[1] &&
                i.Op1Register == contract.Registers[0] && i.Immediate32 == 0x490), "Rebuild the worker record offset from the captured ID.");
            for (int index = 0; index < 3; index++)
            {
                uint value = index == 2 ? 3U : (uint)index;
                Instruction assignment = repair.Last(i => i.Mnemonic == Mnemonic.Mov && i.Op0Register == Low32(contract.Registers[index + 2]));
                Assert.AreEqual(OpKind.Immediate32, assignment.Op1Kind); Assert.AreEqual(value, assignment.Immediate32);
            }
            foreach (int index in new[] { 5, 6 })
            {
                Instruction assignment = repair.Last(i => i.Mnemonic == Mnemonic.Mov && i.Op0Register == contract.Registers[index]);
                Assert.AreEqual(OpKind.Immediate64, assignment.Op1Kind);
                Assert.AreEqual(image + (index == 5 ? 0x67E8400UL : 0x64CCBB0UL), assignment.Immediate64);
            }
            var information = new InstructionInfoFactory();
            foreach (Instruction instruction in repair)
                foreach (UsedMemory memory in information.GetInfo(instruction).GetUsedMemory())
                    Assert.IsFalse(memory.Access == OpAccess.Write || memory.Access == OpAccess.CondWrite ||
                        memory.Access == OpAccess.ReadWrite || memory.Access == OpAccess.ReadCondWrite,
                        "The repair must not write native stock, statistics, timer or cargo.");
        }

        private static void Reject(WeaponStoredDeliverySite site, Instruction[] original, ulong back, ulong image,
            ulong enabled, ulong demand, ulong callback, ulong workerWire = WorkerWire, ulong storedOptions = StoredOptions)
        {
            var a = new Assembler(64);
            Assert.ThrowsExactly<InvalidOperationException>(() => WeaponProductionNativeAssembly.GenerateStoredDelivery(
                a, original, back, site, image, enabled, demand, workerWire, storedOptions, callback));
            Assert.AreEqual(0, Assemble(a, TestImage + 0x20000000).Length, "Fail closed before emitting any instruction.");
        }

        private sealed class SiteContract
        {
            internal readonly eStructs Building; internal readonly int Rva, Footer, Epilogue;
            internal readonly string Bytes; internal readonly Register[] Registers;
            internal SiteContract(eStructs building, int rva, string bytes, int footer, int epilogue, params Register[] registers)
            { Building = building; Rva = rva; Bytes = bytes; Footer = footer; Epilogue = epilogue; Registers = registers; }
        }
        private sealed class DeliveryPath
        {
            internal readonly List<Instruction> Instructions = new List<Instruction>();
            internal readonly List<ulong> Calls = new List<ulong>();
            internal ulong Destination;
        }

        // This is a bounded control-flow walk, not a native game emulator. Guard outcomes
        // and callback return are inputs; the executable fixture above covers real frame
        // behavior. Decoding follows only reachable code, never inline pointer data.
        private static DeliveryPath Walk(byte[] bytes, ulong origin, WeaponStoredDeliverySite site, ulong image, int rejectGuard, int decision)
        {
            var path = new DeliveryPath(); var literals = new Dictionary<Register, ulong>();
            ulong ip = origin; int guard = 0, decisionBranch = 0;
            var guards = new[] { Mnemonic.Je, Mnemonic.Jl, Mnemonic.Jg, Mnemonic.Jl, Mnemonic.Jg, Mnemonic.Je, Mnemonic.Ja, Mnemonic.Jne };
            for (int steps = 0; steps < 256; steps++)
            {
                if (ip == image + (uint)site.Rva + (uint)site.Bytes.Length || ip == image + (uint)site.FooterRva || ip == image + (uint)site.EpilogueRva)
                { path.Destination = ip; return path; }
                Assert.IsTrue(ip >= origin && ip < origin + (ulong)bytes.Length, "Unexpected generated target " + ip.ToString("X"));
                int offset = checked((int)(ip - origin));
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes, offset, bytes.Length - offset)); decoder.IP = ip;
                Instruction instruction = decoder.Decode(); Assert.AreNotEqual(Code.INVALID, instruction.Code);
                path.Instructions.Add(instruction); ip = instruction.NextIP;
                if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Immediate64)
                    literals[instruction.Op0Register] = instruction.Immediate64;
                if (instruction.FlowControl == FlowControl.Call || instruction.FlowControl == FlowControl.IndirectCall)
                {
                    Assert.AreEqual(8, guard, "The callback must be gated by all eight demand conditions.");
                    path.Calls.Add(Target(instruction, bytes, origin, literals));
                    foreach (Register register in new[] { Register.RAX, Register.RCX, Register.RDX, Register.R8, Register.R9, Register.R10, Register.R11 })
                        literals.Remove(register);
                }
                else if (instruction.FlowControl == FlowControl.UnconditionalBranch || instruction.FlowControl == FlowControl.IndirectBranch)
                    ip = Target(instruction, bytes, origin, literals);
                else if (instruction.FlowControl == FlowControl.ConditionalBranch)
                {
                    bool taken;
                    if (path.Calls.Count == 0)
                    {
                        Assert.IsTrue(guard < guards.Length); Assert.AreEqual(guards[guard], instruction.Mnemonic);
                        taken = guard++ == rejectGuard;
                    }
                    else
                    {
                        Assert.AreEqual(Mnemonic.Je, instruction.Mnemonic); Assert.IsTrue(decisionBranch < 2);
                        taken = decision == decisionBranch++;
                    }
                    if (taken) ip = instruction.NearBranchTarget;
                }
            }
            Assert.Fail("Delivery path did not reach one qualified external continuation."); return path;
        }

        private static ulong Target(Instruction instruction, byte[] bytes, ulong origin, Dictionary<Register, ulong> literals)
        {
            if (instruction.Op0Kind == OpKind.NearBranch64) return instruction.NearBranchTarget;
            if (instruction.Op0Kind == OpKind.Register)
            { Assert.IsTrue(literals.TryGetValue(instruction.Op0Register, out ulong address)); return address; }
            Assert.IsTrue(instruction.Op0Kind == OpKind.Memory && instruction.IsIPRelativeMemoryOperand);
            ulong pointer = instruction.IPRelativeMemoryAddress;
            Assert.IsTrue(pointer >= origin && pointer <= origin + (ulong)bytes.Length - 8);
            return BitConverter.ToUInt64(bytes, checked((int)(pointer - origin)));
        }
        private static int CountReplay(List<Instruction> path, Instruction[] original)
        {
            int count = 0;
            for (int start = 0; start <= path.Count - original.Length; start++)
                if (original.Select((instruction, index) => SameReplayInstruction(instruction, path[start + index])).All(same => same)) count++;
            return count;
        }
        private static bool SameReplayInstruction(Instruction expected, Instruction actual) =>
            expected.Code == actual.Code && expected.OpCount == actual.OpCount &&
            expected.Op0Kind == actual.Op0Kind && expected.Op1Kind == actual.Op1Kind && expected.Op2Kind == actual.Op2Kind &&
            expected.Op0Register == actual.Op0Register && expected.Op1Register == actual.Op1Register && expected.Op2Register == actual.Op2Register &&
            expected.MemoryBase == actual.MemoryBase && expected.MemoryIndex == actual.MemoryIndex &&
            expected.MemoryIndexScale == actual.MemoryIndexScale && expected.MemoryDisplacement64 == actual.MemoryDisplacement64;
        private static bool Move(Instruction instruction, Register target, Register source) => instruction.Mnemonic == Mnemonic.Mov &&
            instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register && instruction.Op0Register == target && instruction.Op1Register == source;
        private static Register Low32(Register register) => (Register)((int)Register.EAX + (int)register - (int)Register.RAX);
        private static byte[] Hex(string text) => Enumerable.Range(0, text.Length / 2).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray();
        private static Instruction[] Decode(byte[] bytes, ulong address)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
            var result = new List<Instruction>();
            while (decoder.IP < address + (ulong)bytes.Length)
            { Instruction instruction = decoder.Decode(); Assert.AreNotEqual(Code.INVALID, instruction.Code); result.Add(instruction); }
            return result.ToArray();
        }
        private static byte[] Assemble(Assembler a, ulong address)
        { using (var stream = new MemoryStream()) { a.Assemble(new StreamCodeWriter(stream), address); return stream.ToArray(); } }

        private static void RequireWindowsX64()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8)
                Assert.Inconclusive("The bounded delivery emitter fixture requires Windows x64.");
        }

        private sealed class DecisionFixture : IDisposable
        {
            private const int AllocationBytes = 0x10000, EntryRva = 0, StubRva = 0x1000, CallbackRva = 0x2000, ReceiptRva = 0x8000;
            private const int EnabledRva = 0x3000, HomeDemandRva = 0x3100, WorkerWireRva = 0x3200, StoredOptionsRva = 0x3400, UnitsRva = 0x4000;
            // Native P/Invoke entry RSP is 8 mod 16; eight saved GPRs plus 200 bytes
            // produce an aligned worker aperture with home space and original XMM6–15.
            private const int FrameBytes = 200;
            private readonly int decision;
            private readonly bool captureUnit;
            private readonly bool demandGate;
            internal readonly ulong IncomingFlags;
            private IntPtr memory;
            private ExecuteFixture execute;
            private ulong Image => unchecked((ulong)memory.ToInt64());
            private ulong Receipt => Image + ReceiptRva;

            internal DecisionFixture(int decision, bool captureUnit = false, bool directionFlag = false, int? workerOption = null, int storedFlag = 1)
            {
                this.decision = decision; this.captureUnit = captureUnit; demandGate = workerOption.HasValue;
                IncomingFlags = InitialFlags | (directionFlag ? 0x400UL : 0);
                memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)AllocationBytes, 0x3000, 0x40);
                if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Delivery fixture allocation failed.");
                try
                {
                    Marshal.WriteInt64(At(ReceiptRva + FirstGuard), unchecked((long)Guard));
                    Marshal.WriteInt64(At(ReceiptRva + LastGuard), unchecked((long)Guard));
                    Marshal.WriteInt32(At(ReceiptRva + MutableCurrentUnit), CapturedUnitId);
                    if (demandGate)
                    {
                        Marshal.WriteInt32(At(EnabledRva), 1);
                        Marshal.WriteByte(At(HomeDemandRva + 1), 1);
                        Marshal.WriteInt32(At(ReceiptRva + MutableCurrentUnit), 1);
                        Marshal.WriteInt16(At(UnitsRva + 0x490 + 0x990), 1);
                        // Use the actual runtime's row stride. Byte 12 and the 0..31
                        // option domain are independently exercised wire ABI inputs.
                        Marshal.WriteByte(At(WorkerWireRva + WeaponProductionRuntime.WorkerWireBytes + 12), checked((byte)workerOption.Value));
                        Marshal.WriteByte(At(StoredOptionsRva + 1), checked((byte)storedFlag));
                        Marshal.WriteByte(At(StoredOptionsRva + 31), checked((byte)storedFlag));
                        // Out-of-table sentinels make a missing unsigned range guard
                        // observably wrong, rather than accidentally reading a zero.
                        Marshal.WriteByte(At(StoredOptionsRva + 32), 1);
                        Marshal.WriteByte(At(StoredOptionsRva + 255), 1);
                    }
                    Write(EntryRva, Entry()); Write(CallbackRva, Callback()); Write(StubRva, Decision());
                    if (!FlushInstructionCache(GetCurrentProcess(), memory, (UIntPtr)AllocationBytes))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Delivery fixture instruction-cache flush failed.");
                    execute = (ExecuteFixture)Marshal.GetDelegateForFunctionPointer(At(EntryRva), typeof(ExecuteFixture));
                }
                catch { Dispose(); throw; }
            }

            internal void Run()
            {
                // Affect only this owned test process, and restore its prior mode even on failure.
                const uint suppressDialogs = 0x0001 | 0x0002 | 0x8000;
                uint previous = GetErrorMode();
                try
                {
                    SetErrorMode(previous | suppressDialogs);
                    if ((GetErrorMode() & suppressDialogs) != suppressDialogs)
                        throw new InvalidOperationException("Native fault-dialog suppression failed; fixture execution refused.");
                    execute();
                }
                finally { SetErrorMode(previous); }
            }
            internal int Int32(int offset) => Marshal.ReadInt32(At(ReceiptRva + offset));
            internal ulong UInt64(int offset) => unchecked((ulong)Marshal.ReadInt64(At(ReceiptRva + offset)));
            private IntPtr At(int rva) => IntPtr.Add(memory, rva);

            private Assembler Entry()
            {
                var a = new Assembler(64);
                a.push(rbx); a.push(rbp); a.push(rsi); a.push(rdi); a.push(r12); a.push(r13); a.push(r14); a.push(r15);
                a.sub(rsp, FrameBytes);
                for (int index = 6; index < Simd.Length; index++) a.movdqu(__xmmword_ptr[rsp + 32 + (index - 6) * 16], Simd[index]);
                a.mov(rax, Receipt); a.mov(__qword_ptr[rax + EntryRsp], rsp);
                for (int index = 0; index < Simd.Length; index++)
                {
                    a.mov(rax, SimdSeed + (ulong)index); a.movq(Simd[index], rax); a.punpcklqdq(Simd[index], Simd[index]);
                }
                for (int index = 0; index < Gp.Length; index++) if (index != 4) a.mov(Gp[index], GpSeed(index));
                a.push((int)IncomingFlags); a.popfq(); a.jmp(Image + StubRva);
                return a;
            }

            private Assembler Callback()
            {
                var a = new Assembler(64);
                a.mov(rax, Receipt); a.inc(__dword_ptr[rax + CallbackCalls]); a.mov(__qword_ptr[rax + CallbackRsp], rsp);
                a.pushfq(); a.pop(rdx); a.mov(__qword_ptr[rax + CallbackFlags], rdx);
                if (captureUnit)
                { a.mov(__dword_ptr[rax + CallbackUnitId], ecx); a.mov(__dword_ptr[rax + MutableCurrentUnit], 888); }
                // Overwrite all four legal home slots and every volatile GPR/SIMD value.
                a.mov(rax, 0xA1B2C3D4E5F60718UL);
                for (int index = 0; index < 4; index++) a.mov(__qword_ptr[rsp + 8 + index * 8], rax);
                for (int index = 0; index < 6; index++) a.pxor(Simd[index], Simd[index]);
                foreach (int index in new[] { 1, 2, 8, 9, 10, 11 }) a.mov(Gp[index], ~GpSeed(index));
                a.xor(eax, eax); a.cmp(eax, 1);
                // Upper return bits are deliberately unrelated. The contract returns Int32.
                a.mov(rax, 0xDEADBEEF00000000UL | unchecked((uint)decision)); a.ret();
                return a;
            }

            private Assembler Decision()
            {
                var a = new Assembler(64);
                Label native = a.CreateLabel(), accepted = a.CreateLabel(), refused = a.CreateLabel();
                Label dormant = a.CreateLabel();
                if (demandGate)
                {
                    WeaponProductionNativeAssembly.EmitStoredDeliveryDemandCheck(a, dormant,
                        Image + EnabledRva, Image + HomeDemandRva, Image + WorkerWireRva, Image + StoredOptionsRva,
                        Receipt + MutableCurrentUnit, Image + UnitsRva);
                    WeaponProductionNativeAssembly.RestoreStoredDeliveryDemandRegisters(a);
                }
                WeaponProductionNativeAssembly.EmitStoredDeliveryDecision(a, Image + CallbackRva, native, accepted, refused,
                    captureUnit ? Receipt + MutableCurrentUnit : 0, captureUnit ? Register.R14 : Register.None);
                if (demandGate)
                {
                    a.Label(ref dormant);
                    WeaponProductionNativeAssembly.RestoreStoredDeliveryDemandRegisters(a);
                }
                a.Label(ref native);
                a.pushfq(); a.push(r11); a.mov(r11, Receipt); a.inc(__dword_ptr[r11 + NativeCalls]); a.pop(r11); a.popfq();
                CaptureAndReturn(a, NativeExit);
                a.Label(ref accepted); CaptureAndReturn(a, AcceptedExit);
                a.Label(ref refused); CaptureAndReturn(a, RefusedExit);
                return a;
            }

            private void CaptureAndReturn(Assembler a, int marker)
            {
                // Save scratch registers and flags before using the absolute receipt pointer.
                a.pushfq(); a.push(rax); a.push(r11); a.mov(r11, Receipt);
                for (int index = 0; index < Gp.Length; index++)
                    if (index != 4 && index != 11) a.mov(__qword_ptr[r11 + ReturnedGp + index * 8], Gp[index]);
                a.mov(rax, __qword_ptr[rsp]); a.mov(__qword_ptr[r11 + ReturnedGp + 11 * 8], rax);
                a.lea(rax, __qword_ptr[rsp + 24]);
                a.mov(__qword_ptr[r11 + ReturnedGp + 4 * 8], rax); a.mov(__qword_ptr[r11 + ExitRsp], rax);
                a.mov(rax, __qword_ptr[rsp + 16]); a.mov(__qword_ptr[r11 + Flags], rax);
                a.mov(__dword_ptr[r11 + ExitMarker], marker);
                for (int index = 0; index < Simd.Length; index++) a.movdqu(__xmmword_ptr[r11 + ReturnedSimd + index * 16], Simd[index]);
                a.pop(r11); a.pop(rax); a.popfq();
                for (int index = 6; index < Simd.Length; index++) a.movdqu(Simd[index], __xmmword_ptr[rsp + 32 + (index - 6) * 16]);
                a.cld(); a.add(rsp, FrameBytes);
                a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12); a.pop(rdi); a.pop(rsi); a.pop(rbp); a.pop(rbx); a.ret();
            }

            private void Write(int rva, Assembler a)
            {
                using (var stream = new MemoryStream())
                {
                    a.Assemble(new StreamCodeWriter(stream), Image + (uint)rva);
                    byte[] bytes = stream.ToArray();
                    if (bytes.Length > 0x1000 || rva < 0 || rva > ReceiptRva - bytes.Length)
                        throw new InvalidOperationException("Delivery fixture code exceeds its owned page.");
                    Marshal.Copy(bytes, 0, At(rva), bytes.Length);
                }
            }
            public void Dispose()
            {
                GC.KeepAlive(execute);
                IntPtr owned = memory; memory = IntPtr.Zero;
                if (owned != IntPtr.Zero && !VirtualFree(owned, UIntPtr.Zero, 0x8000))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Delivery fixture release failed.");
            }
        }
    }
}
