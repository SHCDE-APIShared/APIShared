using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Economy;
using APIShared.Internal;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class AllyTransferChatNativeTests
    {
        [TestMethod]
        public void ActualWriterSuppressesOnlyMatchingGiftCallersAndCategoriesInsideOwnedScope()
        {
            using (var fixture = new WriterFixture())
            {
                fixture.Recipient(10);
                Assert.AreEqual(0, fixture.Callbacks, "Dormant demand must stay entirely native.");
                fixture.AssertRows(10); fixture.Clear();
                using (AllyTransferChatScope.Enter(true))
                {
                    fixture.Recipient(10); fixture.Sender(15); fixture.AssertRows();
                    fixture.Recipient(15); fixture.Sender(10); fixture.Unrelated(10); fixture.Recipient(7);
                    fixture.AssertRows(15, 10, 10, 7);
                    int calls = fixture.Callbacks;
                    using (AllyTransferChatScope.Suspend()) fixture.Recipient(10);
                    Assert.AreEqual(calls, fixture.Callbacks, "Suspended demand executes the original writer directly.");
                    Assert.IsTrue(AllyTransferChatScope.IsSuppressed);
                    fixture.Sender(15); fixture.AssertRows(15, 10, 10, 7, 10);
                }
                Assert.IsFalse(AllyTransferChatScope.IsSuppressed);
                Assert.AreEqual(7, fixture.Callbacks);
                fixture.Sender(15); fixture.AssertRows(15, 10, 10, 7, 10, 15);
                Assert.AreEqual(7, fixture.Callbacks);
            }
        }

        [TestMethod]
        public void GlobalDemandDoesNotSuppressAnotherThreadsUnownedGift()
        {
            using (var fixture = new WriterFixture())
            {
                Exception failure = null;
                using (AllyTransferChatScope.Enter(true))
                {
                    var worker = fixture.Worker = new Thread(() => {
                        try
                        {
                            Assert.IsFalse(AllyTransferChatScope.IsSuppressed);
                            fixture.Recipient(10);
                        }
                        catch (Exception error) { failure = error; }
                    }) { IsBackground = true };
                    worker.Start(); Assert.IsTrue(worker.Join(10000), "Private native writer worker did not return.");
                    if (failure != null) throw new InvalidOperationException("Worker failed.", failure);
                    fixture.AssertRows(10);
                    fixture.Recipient(10); fixture.AssertRows(10);
                    Assert.AreEqual(2, fixture.Callbacks);
                }
                fixture.Recipient(10); fixture.AssertRows(10, 10);
                Assert.AreEqual(2, fixture.Callbacks);
            }
        }

        [TestMethod]
        public void ExecutedStubPreservesMachineStateAgainstNativeVolatileClobberingFilter()
        {
            using (var fixture = new WriterFixture()) fixture.AssertMachineState();
        }

        // This fixture executes the production custom stub and all 144 original writer
        // bytes in private memory. It is not a runtime patch/publication or game UI test.
        private sealed class WriterFixture : IDisposable
        {
            private const int ImageSize = 0x100000, StubRva = 0xE0000, UnrelatedReturnRva = 0xD7D00;
            private const int IndexOffset = 0x275A00, RecordsOffset = 0x275A08;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void Caller(int category);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void SentinelCaller();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int Filter(ulong physicalReturnAddress, int category);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);
            [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
            private readonly IntPtr image, manager;
            private readonly Filter filter;
            internal readonly Caller Recipient, Sender, Unrelated;
            internal int Callbacks;
            internal Thread Worker;

            internal WriterFixture()
            {
                image = VirtualAlloc(IntPtr.Zero, (UIntPtr)ImageSize, 0x3000, 0x40);
                if (image == IntPtr.Zero) throw new InvalidOperationException("Private writer image allocation failed.");
                try
                {
                    manager = Marshal.AllocHGlobal(RecordsOffset + 100); Clear();
                    byte[] snapshot = new byte[ImageSize];
                    Array.Copy(AllyTransferChatAssembly.Body, 0, snapshot, AllyTransferChatAssembly.Rva, AllyTransferChatAssembly.Body.Length);
                    WriteRelativeCall(snapshot, AllyTransferChatAssembly.RecipientReturnRva, AllyTransferChatAssembly.Rva);
                    WriteRelativeCall(snapshot, AllyTransferChatAssembly.SenderReturnRva, AllyTransferChatAssembly.Rva);
                    AllyTransferChatAssembly.ValidatePristine(snapshot);
                    Assert.AreEqual(144, AllyTransferChatAssembly.Body.Length, "The fixture executes the audited complete leaf writer.");
                    Marshal.Copy(snapshot, 0, image, snapshot.Length);
                    filter = (address, category) => {
                        Interlocked.Increment(ref Callbacks);
                        return AllyTransferChatScope.IsSuppressed && AllyTransferChatAssembly.IsGiftReturn(address, category,
                            unchecked((ulong)image.ToInt64())) ? 1 : 0;
                    };
                    byte[] original = new byte[AllyTransferChatAssembly.Length];
                    Array.Copy(AllyTransferChatAssembly.Body, original, original.Length);
                    byte[] stub = NativeInlineStub.ExpectedStub(unchecked((ulong)image.ToInt64()) + AllyTransferChatAssembly.Rva,
                        original, original.Length, Generate, unchecked((ulong)image.ToInt64()) + StubRva, "Writer span changed.");
                    if (StubRva + stub.Length > ImageSize) throw new InvalidOperationException("Writer stub escaped its private image.");
                    Marshal.Copy(stub, 0, image + StubRva, stub.Length);
                    Recipient = PrepareCaller(AllyTransferChatAssembly.RecipientReturnRva);
                    Sender = PrepareCaller(AllyTransferChatAssembly.SenderReturnRva);
                    Unrelated = PrepareCaller(UnrelatedReturnRva);
                }
                catch { Dispose(); throw; }
            }
            private void Generate(Assembler assembler, ReadOnlySpan<Instruction> original, ulong back) =>
                AllyTransferChatAssembly.Generate(assembler, original, back,
                    unchecked((ulong)AllyTransferChatScope.DemandAddress.ToInt64()),
                    unchecked((ulong)Marshal.GetFunctionPointerForDelegate(filter).ToInt64()));

            internal void AssertMachineState()
            {
                const int FilterRva = 0xE1000, CheckedStubRva = 0xE2000, CallerRva = 0xE3000;
                const int Before = 0x20, After = 0x120, Seeds = 0x240, SnapshotLength = 240, StackSlot = 15 * 8;
                IntPtr output = Marshal.AllocHGlobal(0x300);
                SentinelCaller caller = null;
                try
                {
                    for (int offset = 0; offset < 0x300; offset++) Marshal.WriteByte(output, offset, 0);
                    for (int index = 0; index < 6 * 16; index++) Marshal.WriteByte(output, Seeds + index, (byte)(0x10 + index));
                    ulong outputAddress = unchecked((ulong)output.ToInt64());
                    var simd = new[] { xmm0, xmm1, xmm2, xmm3, xmm4, xmm5 };
                    var volatileGp = new[] { rax, rcx, rdx, r8, r9, r10, r11 };
                    var filterCode = new Assembler(64);
                    filterCode.mov(rax, outputAddress); filterCode.inc(__dword_ptr[rax + 12]);
                    for (int index = 0; index < volatileGp.Length; index++)
                        filterCode.mov(volatileGp[index], 0xF100000000000000UL + (ulong)index);
                    foreach (var register in simd) filterCode.pcmpeqb(register, register);
                    filterCode.stc(); // Entry CF is clear. The callback intentionally changes flags too.
                    filterCode.mov(rax, outputAddress); filterCode.mov(eax, __dword_ptr[rax + 8]); filterCode.ret();
                    WriteCode(FilterRva, filterCode);

                    byte[] first = new byte[AllyTransferChatAssembly.Length];
                    Array.Copy(AllyTransferChatAssembly.Body, first, first.Length);
                    byte[] checkedStub = NativeInlineStub.ExpectedStub(unchecked((ulong)image.ToInt64()) + AllyTransferChatAssembly.Rva,
                        first, first.Length, (a, original, back) => AllyTransferChatAssembly.Generate(a, original, back,
                            unchecked((ulong)AllyTransferChatScope.DemandAddress.ToInt64()),
                            unchecked((ulong)image.ToInt64()) + FilterRva),
                        unchecked((ulong)image.ToInt64()) + CheckedStubRva, "Sentinel writer boundary changed.");
                    Assert.IsTrue(checkedStub.Length < 0x1000);
                    Marshal.Copy(checkedStub, 0, image + CheckedStubRva, checkedStub.Length);

                    var registers = new[] { rax, rbx, rcx, rdx, rsi, rdi, rbp, r8, r9, r10, r11, r12, r13, r14, r15, rsp };
                    ulong[] sentinels = { 0x1111111111111111UL, 0x2222222222222222UL, unchecked((ulong)manager.ToInt64()), 10,
                        0x5555555555555555UL, 0x6666666666666666UL, 0x7777777777777777UL, 1, 2,
                        0xAAAAAAAAAAAAAAAAUL, 0xBBBBBBBBBBBBBBBBUL, 0xCCCCCCCCCCCCCCCCUL,
                        0xDDDDDDDDDDDDDDDDUL, 0xEEEEEEEEEEEEEEEEUL };
                    var code = new Assembler(64);
                    code.push(rbx); code.push(rsi); code.push(rdi); code.push(rbp);
                    code.push(r12); code.push(r13); code.push(r14); code.push(r15);
                    code.sub(rsp, 0x38); // Win64 shadow32, two stack arguments and alignment.
                    code.mov(r15, outputAddress);
                    for (int index = 0; index < simd.Length; index++) code.movdqu(simd[index], __xmmword_ptr[r15 + Seeds + index * 16]);
                    code.mov(__dword_ptr[rsp + 0x20], 5);
                    code.mov(__dword_ptr[rsp + 0x28], (int)eGoods.STORED_STONE_BLOCKS);
                    code.mov(r11, 0x246UL); code.push(r11); code.popfq();
                    for (int index = 0; index < sentinels.Length; index++) code.mov(registers[index], sentinels[index]);
                    void Snapshot(int offset)
                    {
                        for (int index = 0; index < registers.Length; index++) code.mov(__qword_ptr[r15 + offset + index * 8], registers[index]);
                        code.pushfq(); code.pop(__qword_ptr[r15 + offset + 128]);
                        for (int index = 0; index < simd.Length; index++) code.movdqu(__xmmword_ptr[r15 + offset + 144 + index * 16], simd[index]);
                    }
                    Snapshot(Before); code.call(__qword_ptr[r15]); Snapshot(After);
                    code.add(rsp, 0x38); code.pop(r15); code.pop(r14); code.pop(r13); code.pop(r12);
                    code.pop(rbp); code.pop(rdi); code.pop(rsi); code.pop(rbx); code.ret();
                    WriteCode(CallerRva, code);
                    Assert.IsTrue(FlushInstructionCache(GetCurrentProcess(), image, (UIntPtr)ImageSize));
                    caller = (SentinelCaller)Marshal.GetDelegateForFunctionPointer(image + CallerRva, typeof(SentinelCaller));

                    byte[] ReadSnapshot(int offset)
                    { var bytes = new byte[SnapshotLength]; Marshal.Copy(output + offset, bytes, 0, bytes.Length); return bytes; }
                    void InvokeAt(int targetRva, int decision)
                    {
                        Clear(); Marshal.WriteInt64(output, unchecked(image.ToInt64() + targetRva));
                        Marshal.WriteInt32(output, 8, decision);
                        using (AllyTransferChatScope.Enter(true)) caller();
                        Assert.AreEqual(Marshal.ReadInt64(output, Before + StackSlot), Marshal.ReadInt64(output, After + StackSlot),
                            "The exact caller stack pointer must be restored on every path.");
                    }
                    InvokeAt(AllyTransferChatAssembly.Rva, 0); AssertRows(10);
                    byte[] baseline = ReadSnapshot(After);
                    byte[] baselineRow = new byte[10]; Marshal.Copy(manager + RecordsOffset, baselineRow, 0, baselineRow.Length);
                    Assert.AreEqual(0, Marshal.ReadInt32(output, 12));

                    InvokeAt(CheckedStubRva, 1); AssertRows();
                    CollectionAssert.AreEqual(ReadSnapshot(Before), ReadSnapshot(After),
                        "Suppression preserves every GP register, exact RSP, flags and XMM0..5 despite callback clobbering.");
                    Assert.AreEqual(1, Marshal.ReadInt32(output, 12));

                    InvokeAt(CheckedStubRva, 0); AssertRows(10);
                    byte[] forwarded = ReadSnapshot(After);
                    // Separate managed-to-native calls may begin on different stack frames;
                    // exact within-call RSP equality was checked above for both executions.
                    Array.Clear(baseline, StackSlot, 8); Array.Clear(forwarded, StackSlot, 8);
                    CollectionAssert.AreEqual(baseline, forwarded,
                        "Forwarding must match the entire unmodified writer's GP/flags/XMM effects.");
                    var forwardedRow = new byte[10]; Marshal.Copy(manager + RecordsOffset, forwardedRow, 0, forwardedRow.Length);
                    CollectionAssert.AreEqual(baselineRow, forwardedRow);
                    Assert.AreEqual(2, Marshal.ReadInt32(output, 12));
                }
                finally { Marshal.FreeHGlobal(output); GC.KeepAlive(caller); }
            }

            private void WriteCode(int rva, Assembler code)
            {
                byte[] bytes = NativeInlineStub.Assemble(code, unchecked((ulong)image.ToInt64()) + (uint)rva);
                Assert.IsTrue(rva >= 0 && rva <= ImageSize - bytes.Length);
                Marshal.Copy(bytes, 0, image + rva, bytes.Length);
            }

            private Caller PrepareCaller(int physicalReturnRva)
            {
                // 44 bytes set the writer's Win64 arguments; call ends at the exact
                // audited physical return RVA. Neighboring callers do not overlap.
                var bytes = new List<byte> { 0x48, 0x83, 0xEC, 0x38, 0x89, 0xCA, 0x48, 0xB9 };
                bytes.AddRange(BitConverter.GetBytes(manager.ToInt64()));
                bytes.AddRange(new byte[] { 0x41, 0xB8, 1, 0, 0, 0, 0x41, 0xB9, 2, 0, 0, 0,
                    0xC7, 0x44, 0x24, 0x20, 5, 0, 0, 0, 0xC7, 0x44, 0x24, 0x28 });
                bytes.AddRange(BitConverter.GetBytes((int)eGoods.STORED_STONE_BLOCKS));
                Assert.AreEqual(44, bytes.Count);
                int start = physicalReturnRva - bytes.Count - 5;
                bytes.Add(0xE8); bytes.AddRange(BitConverter.GetBytes(StubRva - physicalReturnRva));
                bytes.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x38, 0xC3 });
                Marshal.Copy(bytes.ToArray(), 0, image + start, bytes.Count);
                return (Caller)Marshal.GetDelegateForFunctionPointer(image + start, typeof(Caller));
            }
            private static void WriteRelativeCall(byte[] image, int returnRva, int targetRva)
            {
                image[returnRva - 5] = 0xE8;
                Array.Copy(BitConverter.GetBytes(targetRva - returnRva), 0, image, returnRva - 4, 4);
            }
            internal void Clear()
            {
                Marshal.WriteInt32(manager, IndexOffset, 0);
                for (int offset = 0; offset < 100; offset += 2) Marshal.WriteInt16(manager, RecordsOffset + offset, 0);
            }
            internal void AssertRows(params int[] categories)
            {
                Assert.AreEqual(categories.Length, Marshal.ReadInt32(manager, IndexOffset));
                for (int index = 0; index < categories.Length; index++)
                {
                    int row = RecordsOffset + index * 10;
                    Assert.AreEqual(categories[index], (int)Marshal.ReadInt16(manager, row));
                    Assert.AreEqual(1, (int)Marshal.ReadInt16(manager, row + 2));
                    Assert.AreEqual(2, (int)Marshal.ReadInt16(manager, row + 4));
                    Assert.AreEqual(5, (int)Marshal.ReadInt16(manager, row + 6));
                    Assert.AreEqual((int)eGoods.STORED_STONE_BLOCKS, (int)Marshal.ReadInt16(manager, row + 8));
                }
            }
            public void Dispose()
            {
                // Private CLI memory is retired only after every own synchronous caller returned.
                if (Worker?.IsAlive == true) return;
                if (manager != IntPtr.Zero) Marshal.FreeHGlobal(manager);
                if (image != IntPtr.Zero) VirtualFree(image, UIntPtr.Zero, 0x8000);
                GC.KeepAlive(filter); GC.KeepAlive(Recipient); GC.KeepAlive(Sender); GC.KeepAlive(Unrelated);
            }
        }
    }
}
