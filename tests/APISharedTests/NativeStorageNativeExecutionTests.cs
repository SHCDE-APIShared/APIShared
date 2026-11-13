using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    // Executes actual production ExpectedStub bytes on bounded private pages. Recompute,
    // filter and continuation routines are synthetic; no game routine or native JGE runs.
    // Captured CMP flags imply its signed JGE outcome. This does not exercise native finder
    // retry/reachability, a managed callback transition, installed hooks or gameplay behavior.
    // All targets, demand and receipts are owned; no game/process memory is accessed.
    [TestClass, DoNotParallelize]
    public sealed class NativeStorageNativeExecutionTests
    {
        private const int NativeAggregate = 249, FilteredAggregate = 250, CandidateSlot = 137;
        private const ulong NativeReturnBits = 0xA55AA55A000000F9UL;
        private const ulong CallbackReturnBits = 0xDEADBEEF000000FAUL;
        private const ulong ArithmeticFlags = 0x8D5;
        private const ulong GuardValue = 0x7162534455667788UL;
        private const ulong SimdSeed = 0x61728394A5B6C7D8UL;
        // Win32 SetErrorMode constants: critical errors, native-fault/WER reporting,
        // and legacy OpenFile prompts. This changes only the owned test process.
        private const uint SemFailCriticalErrors = 0x0001, SemNoGpFaultErrorBox = 0x0002, SemNoOpenFileErrorBox = 0x8000;
        private const uint NativeErrorMode = SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox;
        private static readonly ulong[] VolatileSeeds = {
            0x1122334455667788UL, 0x2233445566778899UL, 0x33445566778899AAUL,
            0x445566778899AABBUL, 0x5566778899AABBCCUL, 0x66778899AABBCCDDUL };
        private static readonly AssemblerRegister64[] Volatiles = { rcx, rdx, r8, r9, r10, r11 };
        private static readonly AssemblerRegisterXMM[] Simd = { xmm0, xmm1, xmm2, xmm3, xmm4, xmm5 };

        // Receipt offsets are independent observations made by the synthetic native callees.
        private const int NativeCalls = 0, CallbackCalls = 4, Sequence = 8, NativeOrder = 12, CallbackOrder = 16;
        private const int NativeSlot = 20, NativeManager = 24, CallbackManager = 32, CallbackSlot = 40, CallbackAggregate = 44;
        private const int EntryRsp = 48, ExitRsp = 56, NativeRsp = 64, CallbackRsp = 72, Flags = 80, ReturnedRax = 88;
        private const int ReturnedVolatiles = 96, ReturnedRsi = 144, ReturnedRbx = 152, ReturnedSimd = 160;
        private const int FirstGuard = 0x2F0, LastGuard = 0x3F8;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ExecuteFixture();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
        [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [TestMethod]
        public void DormantActualStubRecomputesOnceWithoutCallbackAndPreservesNativeResultAndComparison()
        {
            RequireWindowsX64();
            using (var fixture = new Fixture(false))
            {
                Assert.AreEqual(NativeAggregate, fixture.Run());
                CheckCommon(fixture, NativeReturnBits, 0x95UL, false);
                Assert.AreEqual(0, fixture.Int32(CallbackCalls));
                Assert.AreEqual(0, fixture.Int32(CallbackOrder));
                Assert.AreEqual(1, fixture.Int32(Sequence));
                Assert.AreEqual(0UL, fixture.UInt64(CallbackManager));
            }
        }

        [TestMethod]
        public void ActiveActualStubPassesNativeResultAndRestoresVolatileMachineStateAfterClobberingCallback()
        {
            RequireWindowsX64();
            using (var fixture = new Fixture(true))
            {
                Assert.AreEqual(FilteredAggregate, fixture.Run());
                CheckCommon(fixture, FilteredAggregate, 0x44UL, true);
                Assert.AreEqual(1, fixture.Int32(CallbackCalls));
                Assert.AreEqual(2, fixture.Int32(CallbackOrder));
                Assert.AreEqual(2, fixture.Int32(Sequence));
                Assert.AreEqual(fixture.Manager, fixture.UInt64(CallbackManager));
                Assert.AreEqual(CandidateSlot, fixture.Int32(CallbackSlot));
                Assert.AreEqual(NativeAggregate, fixture.Int32(CallbackAggregate));
                Assert.AreEqual(8UL, fixture.UInt64(CallbackRsp) & 15UL, "Callback entry must follow the Windows x64 stack ABI.");
            }
        }

        private static void CheckCommon(Fixture fixture, ulong resultBits, ulong expectedFlags, bool expectedJge)
        {
            Assert.AreEqual(1, fixture.Int32(NativeCalls));
            Assert.AreEqual(1, fixture.Int32(NativeOrder));
            Assert.AreEqual(fixture.Manager, fixture.UInt64(NativeManager));
            Assert.AreEqual(CandidateSlot, fixture.Int32(NativeSlot));
            Assert.AreEqual(0UL, fixture.UInt64(EntryRsp) & 15UL, "The fixture jumps into an interior aperture with aligned RSP.");
            Assert.AreEqual(8UL, fixture.UInt64(NativeRsp) & 15UL, "The original recomputation has its own return address and home space.");
            Assert.AreEqual(fixture.UInt64(EntryRsp), fixture.UInt64(ExitRsp), "The callback frame must restore the exact aperture stack pointer.");
            Assert.AreEqual(resultBits, fixture.UInt64(ReturnedRax), "Dormant RAX is unchanged; active Int32 results are zero-extended.");
            ulong flags = fixture.UInt64(Flags);
            Assert.AreEqual(expectedFlags, flags & ArithmeticFlags, "Capture the production CMP EAX,250 flags before synthetic cleanup.");
            Assert.AreEqual(expectedJge, ((flags >> 7) & 1) == ((flags >> 11) & 1), "Captured CMP flags must imply the intended signed JGE outcome; the continuation is synthetic.");
            for (int index = 0; index < VolatileSeeds.Length; index++)
                Assert.AreEqual(VolatileSeeds[index], fixture.UInt64(ReturnedVolatiles + index * 8), "Native volatile register " + Volatiles[index]);
            Assert.AreEqual(fixture.Manager, fixture.UInt64(ReturnedRsi));
            Assert.AreEqual((ulong)CandidateSlot, fixture.UInt64(ReturnedRbx));
            for (int index = 0; index < Simd.Length; index++)
            {
                Assert.AreEqual(SimdSeed + (ulong)index, fixture.UInt64(ReturnedSimd + index * 16), "Native SIMD low lane " + index);
                Assert.AreEqual(SimdSeed + (ulong)index, fixture.UInt64(ReturnedSimd + index * 16 + 8), "Native SIMD high lane " + index);
            }
            Assert.AreEqual(GuardValue, fixture.UInt64(FirstGuard));
            Assert.AreEqual(GuardValue, fixture.UInt64(LastGuard));
            Assert.AreEqual(GuardValue, fixture.ManagerMarker());
        }

        private static void RequireWindowsX64()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8)
                Assert.Inconclusive("The bounded native emitter fixture requires Windows x64.");
        }

        private sealed class Fixture : IDisposable
        {
            // 1.125 MiB, including native RVA space and disjoint code/data pages; well below
            // the 4 MiB fixture limit. VirtualAlloc zeroes the complete owned allocation.
            private const int AllocationBytes = 0x120000;
            private const int EntryRva = 0x100000, StubRva = 0x101000, CallbackRva = 0x102000;
            private const int ReceiptRva = 0x110000, DemandRva = 0x111000, ManagerRva = 0x112000;
            private IntPtr memory;
            private ExecuteFixture execute;
            private ulong Image => unchecked((ulong)memory.ToInt64());
            internal ulong Manager => Image + ManagerRva;

            internal Fixture(bool active)
            {
                memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)AllocationBytes, 0x3000, 0x40);
                if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Native storage fixture allocation failed.");
                try
                {
                    Marshal.WriteInt32(At(DemandRva), active ? 1 : 0);
                    Marshal.WriteInt64(At(ReceiptRva + FirstGuard), unchecked((long)GuardValue));
                    Marshal.WriteInt64(At(ReceiptRva + LastGuard), unchecked((long)GuardValue));
                    Marshal.WriteInt64(At(ManagerRva), unchecked((long)GuardValue));
                    Write(NativeStorageNativeDefinition.GranaryRecomputeRva, Recompute(), 0x400);
                    Write(NativeStorageNativeDefinition.GranaryFinderContinuationRva, Continuation(), 0x400);
                    Write(CallbackRva, Filter(), 0x1000);
                    Write(EntryRva, Entry(), 0x1000);
                    byte[] stub = NativeStorageAssembly.ExpectedStub(Image, Image + StubRva, Image + DemandRva, Image + CallbackRva);
                    Write(StubRva, stub, 0x1000);
                    if (!FlushInstructionCache(GetCurrentProcess(), memory, (UIntPtr)AllocationBytes))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Native storage fixture instruction cache flush failed.");
                    execute = (ExecuteFixture)Marshal.GetDelegateForFunctionPointer(At(EntryRva), typeof(ExecuteFixture));
                }
                catch { Dispose(); throw; }
            }

            internal int Run()
            {
                uint previous = GetErrorMode();
                try
                {
                    SetErrorMode(previous | NativeErrorMode);
                    if ((GetErrorMode() & NativeErrorMode) != NativeErrorMode)
                        throw new InvalidOperationException("Native fault-dialog suppression was not applied; execution was refused.");
                    return execute();
                }
                finally { SetErrorMode(previous); }
            }
            internal int Int32(int offset) => Marshal.ReadInt32(At(ReceiptRva + offset));
            internal ulong UInt64(int offset) => unchecked((ulong)Marshal.ReadInt64(At(ReceiptRva + offset)));
            internal ulong ManagerMarker() => unchecked((ulong)Marshal.ReadInt64(At(ManagerRva)));
            private IntPtr At(int rva) => IntPtr.Add(memory, rva);

            private Assembler Entry()
            {
                var a = new Assembler(64);
                // P/Invoke entry RSP is 8 mod 16. Three saved nonvolatiles align it;
                // reserve the native callee's 32-byte home, then JMP rather than CALL.
                a.push(rbx); a.push(rsi); a.push(r15); a.sub(rsp, 32);
                a.mov(r15, Image + ReceiptRva); a.mov(rsi, Manager); a.mov(ebx, CandidateSlot);
                a.mov(__qword_ptr[r15 + EntryRsp], rsp);
                a.jmp(Image + StubRva);
                return a;
            }

            private Assembler Recompute()
            {
                var a = new Assembler(64);
                a.mov(rax, Image + ReceiptRva);
                a.mov(__qword_ptr[rax + NativeManager], rcx); a.mov(__dword_ptr[rax + NativeSlot], edx);
                a.mov(__qword_ptr[rax + NativeRsp], rsp);
                a.inc(__dword_ptr[rax + NativeCalls]); a.inc(__dword_ptr[rax + Sequence]);
                a.mov(ecx, __dword_ptr[rax + Sequence]); a.mov(__dword_ptr[rax + NativeOrder], ecx);
                WriteHome(a, 0x1827364554637281UL);
                for (int index = 0; index < Simd.Length; index++)
                {
                    a.mov(rax, SimdSeed + (ulong)index); a.movq(Simd[index], rax);
                    a.punpcklqdq(Simd[index], Simd[index]);
                }
                for (int index = 0; index < Volatiles.Length; index++) a.mov(Volatiles[index], VolatileSeeds[index]);
                // An Int32 callee need not define the upper half of RAX. The dormant stub
                // preserves those bits; its callback input is deliberately only EAX.
                a.mov(rax, NativeReturnBits); a.ret();
                return a;
            }

            private Assembler Filter()
            {
                var a = new Assembler(64);
                a.mov(rax, Image + ReceiptRva);
                a.mov(__qword_ptr[rax + CallbackManager], rcx); a.mov(__dword_ptr[rax + CallbackSlot], edx);
                a.mov(__dword_ptr[rax + CallbackAggregate], r8d); a.mov(__qword_ptr[rax + CallbackRsp], rsp);
                a.inc(__dword_ptr[rax + CallbackCalls]); a.inc(__dword_ptr[rax + Sequence]);
                a.mov(ecx, __dword_ptr[rax + Sequence]); a.mov(__dword_ptr[rax + CallbackOrder], ecx);
                WriteHome(a, 0x897A6B5C4D3E2F10UL);
                // A real ABI-compatible callee may overwrite every volatile GPR and XMM0–5.
                // Leave all nonvolatiles and RSP untouched so failures isolate the emitter.
                for (int index = 0; index < Simd.Length; index++) a.pxor(Simd[index], Simd[index]);
                for (int index = 0; index < Volatiles.Length; index++) a.mov(Volatiles[index], ~VolatileSeeds[index]);
                a.mov(rax, CallbackReturnBits); a.ret();
                return a;
            }

            private Assembler Continuation()
            {
                var a = new Assembler(64);
                // Record CMP flags before cleanup arithmetic. MOV and MOVDQU preserve them.
                a.pushfq(); a.pop(__qword_ptr[r15 + Flags]);
                a.mov(__qword_ptr[r15 + ReturnedRax], rax); a.mov(__qword_ptr[r15 + ExitRsp], rsp);
                for (int index = 0; index < Volatiles.Length; index++) a.mov(__qword_ptr[r15 + ReturnedVolatiles + index * 8], Volatiles[index]);
                a.mov(__qword_ptr[r15 + ReturnedRsi], rsi); a.mov(__qword_ptr[r15 + ReturnedRbx], rbx);
                for (int index = 0; index < Simd.Length; index++) a.movdqu(__xmmword_ptr[r15 + ReturnedSimd + index * 16], Simd[index]);
                a.cld(); a.add(rsp, 32); a.pop(r15); a.pop(rsi); a.pop(rbx); a.ret();
                return a;
            }

            private static void WriteHome(Assembler a, ulong value)
            {
                a.mov(rax, value);
                for (int index = 0; index < 4; index++) a.mov(__qword_ptr[rsp + 8 + index * 8], rax);
            }

            private void Write(int rva, Assembler assembler, int maximum)
            {
                using (var stream = new MemoryStream())
                {
                    assembler.Assemble(new StreamCodeWriter(stream), Image + (uint)rva);
                    Write(rva, stream.ToArray(), maximum);
                }
            }
            private void Write(int rva, byte[] bytes, int maximum)
            {
                if (rva < 0 || bytes.Length > maximum || rva > AllocationBytes - bytes.Length)
                    throw new InvalidOperationException("Native storage fixture code exceeds its owned window.");
                Marshal.Copy(bytes, 0, At(rva), bytes.Length);
            }
            public void Dispose()
            {
                GC.KeepAlive(execute);
                IntPtr owned = memory; memory = IntPtr.Zero;
                if (owned != IntPtr.Zero && !VirtualFree(owned, UIntPtr.Zero, 0x8000))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Native storage fixture release failed.");
            }
        }
    }
}
