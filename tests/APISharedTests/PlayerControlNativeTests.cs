using System;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using APIShared.PlayerControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.X64.Assembly;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using SHCDESE.Interop.Enums;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class PlayerControlNativeTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InputFixture(int nativeView, int spectatorFlag, int ignored, IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong HandlerFixture(IntPtr output);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [TestMethod]
        public void HandlerBridgeTailsExactlyOneDestinationAndPreservesTheWin64CallBoundary()
        {
            IntPtr cells = Marshal.AllocHGlobal(48);
            const ulong Sentinel = 0x123456789ABCDEF0;
            const ulong OriginalResult = 0x7654321012345678, DispatchResult = 0xFEDCBA9876543210;
            try
            {
                ulong address = unchecked((ulong)cells.ToInt64());
                for (int offset = 0; offset < 48; offset += 8) Marshal.WriteInt64(cells, offset, 0);
                using (var original = ExecutableFunction.Create(a => {
                    a.mov(r11, address); a.inc(__dword_ptr[r11 + 8]);
                    a.mov(rax, OriginalResult); a.ret();
                }))
                using (var dispatch = ExecutableFunction.Create(a => {
                    a.mov(r11, address); a.inc(__dword_ptr[r11 + 12]);
                    a.mov(__dword_ptr[r11 + 16], ecx);
                    a.mov(rax, DispatchResult); a.ret();
                }))
                using (var bridge = ExecutableFunction.Create(a => ControlHandlerBridge.Emit(a, address,
                    unchecked((ulong)original.EntryPoint.ToInt64()), unchecked((ulong)dispatch.EntryPoint.ToInt64()),
                    (byte)ChoreType.GiveTribeInstruction)))
                using (var caller = ExecutableFunction.Create(a => {
                    // Enter the stub through a real CALL with Win64 shadow space.
                    // Neither tail destination may leave an extra return address.
                    a.push(rbx); a.push(rdi); a.sub(rsp, 40);
                    a.mov(rdi, rcx); a.mov(rbx, Sentinel);
                    a.mov(__qword_ptr[rdi + 24], rsp);
                    a.mov(r11, unchecked((ulong)bridge.EntryPoint.ToInt64())); a.call(r11);
                    a.mov(__qword_ptr[rdi + 32], rsp);
                    a.mov(__qword_ptr[rdi + 40], rbx);
                    a.add(rsp, 40); a.pop(rdi); a.pop(rbx); a.ret();
                }))
                {
                    var invoke = Marshal.GetDelegateForFunctionPointer<HandlerFixture>(caller.EntryPoint);
                    foreach (int demand in new[] { 0, 1, -1, 0 })
                    {
                        Marshal.WriteInt32(cells, demand);
                        int originalBefore = Marshal.ReadInt32(cells, 8), dispatchBefore = Marshal.ReadInt32(cells, 12);
                        Assert.AreEqual(demand == 0 ? OriginalResult : DispatchResult, invoke(cells));
                        Assert.AreEqual(originalBefore + (demand == 0 ? 1 : 0), Marshal.ReadInt32(cells, 8));
                        Assert.AreEqual(dispatchBefore + (demand == 0 ? 0 : 1), Marshal.ReadInt32(cells, 12));
                        Assert.AreEqual(Marshal.ReadInt64(cells, 24), Marshal.ReadInt64(cells, 32));
                        Assert.AreEqual(Sentinel, unchecked((ulong)Marshal.ReadInt64(cells, 40)));
                    }
                    Assert.AreEqual((int)ChoreType.GiveTribeInstruction, Marshal.ReadInt32(cells, 16));
                    Marshal.WriteInt32(cells, 0);
                    for (int i = 0; i < 20; i++) invoke(cells);
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10000; i++) invoke(cells);
                    Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - allocated);
                    GC.KeepAlive(invoke);
                }
            }
            finally { Marshal.FreeHGlobal(cells); }
        }

        [TestMethod]
        public void ProtectedNativeWriterRetainsWrittenResultIfPostWriteWorldValidationThrows()
        {
            int view = 1, calls = 0;
            bool failWorldCheck = false;
            var guard = new ControlSaveIdentity(() => true,
                epoch => failWorldCheck ? throw new InvalidOperationException("world reader failed") : epoch == 1,
                () => view, value => view = value);
            Assert.IsTrue(guard.TryBind(1, 1, out _, out _)); view = 3;
            const ulong WrittenResult = 0xFEDCBA9876543210;
            Assert.IsNull(ControlSaveNative.TakeFailure());
            try
            {
                Assert.AreEqual(WrittenResult, ControlSaveNative.RunProtectedWriter(guard, 0, value => {
                    calls++; Assert.AreEqual(1, view); failWorldCheck = true; return WrittenResult;
                }));
                Assert.AreEqual(1, calls); Assert.AreEqual(1, guard.ProtectedHumanId);
                Assert.IsNotNull(ControlSaveNative.TakeFailure());
                // An exception before writing does not replay the writer and
                // cannot reuse the successful result of a previous invocation.
                Assert.AreEqual(0UL, ControlSaveNative.RunProtectedWriter(guard, 0, value => {
                    calls++; return WrittenResult;
                }));
                Assert.AreEqual(1, calls); Assert.IsNotNull(ControlSaveNative.TakeFailure());
            }
            finally { ControlSaveNative.TakeFailure(); }
        }

        [TestMethod]
        public void ActualSaveWriterBackendDisplacesTenBytesAndPreservesArgumentsAndOriginalResult()
        {
            using (var target = ExecutableFunction.Create(a => {
                // Production writer prefix, followed by a harmless ABI fixture.
                a.mov(__qword_ptr[rsp + 16], rbx); a.mov(__qword_ptr[rsp + 24], rsi);
                a.push(rbp); a.push(rdi);
                a.pop(rdi); a.pop(rbp);
                a.mov(rax, rcx); a.xor(rax, rdx); a.movzx(r8d, r8b); a.add(rax, r8); a.ret();
            }))
            {
                NativeDetour<ControlSaveNative.WriterCall> hook = null;
                int calls = 0;
                var callback = new ControlSaveNative.WriterCall((manager, sections, mapSave) => {
                    calls++;
                    return hook.Original(manager, sections, mapSave) + 100;
                });
                var request = new DetourRequest<ControlSaveNative.WriterCall> {
                    Name = "Player-control save writer fixture", Callback = callback,
                    TargetAddress = unchecked((ulong)target.EntryPoint.ToInt64())
                };
                try
                {
                    // Match Initialize inside the startup patch window: exactly
                    // the production options with thread suspension disabled.
                    // No other thread can execute this private fixture.
                    var backend = new NativeDetourBackend(ControlSaveNative.Backend.Options with { SuspendThreadsDuringPatch = false });
                    hook = (NativeDetour<ControlSaveNative.WriterCall>)backend.CreateDetour(in request);
                    ControlSaveNative.ValidatePrepared(hook, target.EntryPoint);
                    var invoke = Marshal.GetDelegateForFunctionPointer<ControlSaveNative.WriterCall>(target.EntryPoint);
                    IntPtr first = new IntPtr(0x12345678), second = new IntPtr(0x76543210);
                    ulong original = unchecked((ulong)(first.ToInt64() ^ second.ToInt64())) + 0xAB;
                    Assert.AreEqual(original, invoke(first, second, 0xAB)); Assert.AreEqual(0, calls);
                    hook.Enable();
                    APIShared.NativeSites.NativeIndirectEntry.Validate(hook, target.EntryPoint, ControlSaveNative.Entry);
                    Assert.AreEqual(original + 100, invoke(first, second, 0xAB)); Assert.AreEqual(1, calls);
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    GC.KeepAlive(invoke); GC.KeepAlive(callback);
                }
                finally
                {
                    // Isolated fixture memory only; never a published game hook.
                    hook?.Dispose();
                }
            }
        }

        [TestMethod]
        public void ActualInputBackendRelocatesConditionalMoveAndRipStoreBeforeTheUnchangedContinuation()
        {
            IntPtr page = VirtualAlloc(IntPtr.Zero, new UIntPtr(4096), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, page);
            IntPtr output = Marshal.AllocHGlobal(64);
            NativeDetour<InputFixture> hook = null;
            try
            {
                ulong target = unchecked((ulong)page.ToInt64()), view = target + 2048, actor = view + 8;
                var code = new Assembler(64);
                // Exact two-instruction storage form from RunTick, with only the
                // RIP displacement relocated to this private page's native cell.
                byte[] displaced = { 0x41, 0x0F, 0x45, 0xC4, 0x89, 0x05, 0, 0, 0, 0 };
                Buffer.BlockCopy(BitConverter.GetBytes(2048 - displaced.Length), 0, displaced, 6, 4);
                code.db(displaced);
                code.movzx(r8d, r14b); // The audited unchanged +10 continuation.
                code.pushfq(); code.pop(r10); code.mov(__qword_ptr[r9 + 16], r10);
                code.mov(__qword_ptr[r9 + 24], rsp); code.mov(__qword_ptr[r9 + 32], r11);
                code.mov(__qword_ptr[r9 + 40], r8); code.mov(__dword_ptr[r9 + 56], eax);
                code.pop(r14); code.pop(r12); code.pop(rbx); code.ret();
                byte[] bytes;
                using (var stream = new MemoryStream())
                {
                    code.Assemble(new StreamCodeWriter(stream), target); bytes = stream.ToArray();
                }
                Marshal.Copy(bytes, 0, page, bytes.Length);
                Assert.IsTrue(FlushInstructionCache(GetCurrentProcess(), page, new UIntPtr((uint)bytes.Length)));
                int unexpectedCallbacks = 0;
                InputFixture callback = (v, s, unused, pointer) => unexpectedCallbacks++;
                var bridge = new ControlInputBridge(actor, view, target + 10);
                var request = new DetourRequest<InputFixture> {
                    Name = "private RunTick input boundary", TargetAddress = target, Callback = callback,
                    IntermediaryFactory = bridge
                };
                var backend = new NativeDetourBackend(new NativeDetourOptions {
                    AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false,
                    FunctionScanLimit = bytes.Length, SuspendThreadsDuringPatch = false
                });
                hook = (NativeDetour<InputFixture>)backend.CreateDetour(in request);
                Assert.AreEqual(10, hook.DisplacedByteCount); Assert.AreEqual(DetourScheme.Indirect, hook.Scheme);
                var prefix = new byte[14]; Buffer.BlockCopy(bytes, 0, prefix, 0, prefix.Length);
                bridge.Validate(hook, page, prefix, false);
                hook.Enable(); bridge.Validate(hook, page, prefix);
                var wrongActor = new ControlInputBridge(actor + 8, view, target + 10);
                Assert.ThrowsExactly<InvalidOperationException>(() => wrongActor.Validate(hook, page, prefix));
                var wrongView = new ControlInputBridge(actor, view + 8, target + 10);
                Assert.ThrowsExactly<InvalidOperationException>(() => wrongView.Validate(hook, page, prefix));
                var wrongContinuation = new ControlInputBridge(actor, view, target + 11);
                Assert.ThrowsExactly<InvalidOperationException>(() => wrongContinuation.Validate(hook, page, prefix));
                using (var caller = ExecutableFunction.Create(a => {
                    a.push(rbx); a.push(r12); a.push(r14);
                    a.mov(__qword_ptr[r9], rsp); a.mov(r11, 0x123456789ABCDEF0UL);
                    a.mov(ebx, ecx); a.mov(eax, ebx); a.mov(r12d, 0); a.mov(r14d, 0xAB);
                    a.cmp(edx, 0); a.pushfq(); a.pop(r10); a.mov(__qword_ptr[r9 + 8], r10);
                    a.jmp(target);
                }))
                {
                    var invoke = Marshal.GetDelegateForFunctionPointer<InputFixture>(caller.EntryPoint);
                    foreach (int selected in new[] { 0, 1, 3, 8, 9, -1 })
                    foreach (int permit in new[] { 0, 1, 3, 8, 9, -1 })
                    foreach (int spectator in new[] { 0, 1, -1 })
                    {
                        Marshal.WriteInt32(new IntPtr(unchecked((long)actor)), permit);
                        invoke(selected, spectator, 0, output);
                        int expected = permit == 0 ? spectator == 0 ? selected : 0 :
                            selected >= 1 && selected <= 8 && permit == selected ? selected : 0;
                        Assert.AreEqual(expected, Marshal.ReadInt32(new IntPtr(unchecked((long)view))));
                        Assert.AreEqual(expected, Marshal.ReadInt32(output, 56));
                        Assert.AreEqual(Marshal.ReadInt64(output, 8), Marshal.ReadInt64(output, 16));
                        Assert.AreEqual(Marshal.ReadInt64(output, 0), Marshal.ReadInt64(output, 24));
                        Assert.AreEqual(0x123456789ABCDEF0UL, unchecked((ulong)Marshal.ReadInt64(output, 32)));
                        Assert.AreEqual(0xABL, Marshal.ReadInt64(output, 40));
                    }
                    Assert.AreEqual(0, unexpectedCallbacks, "Both branches must stay native.");
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    GC.KeepAlive(invoke); GC.KeepAlive(callback);
                }
            }
            finally
            {
                // Private fixture only, never a published game hook.
                hook?.Dispose(); Marshal.FreeHGlobal(output);
                Assert.IsTrue(VirtualFree(page, UIntPtr.Zero, 0x8000));
            }
        }

        [TestMethod]
        public void ExecutedInputBridgePreservesFlagsRegistersSimdAndStackOnEveryPermitBranch()
        {
            IntPtr cells = Marshal.AllocHGlobal(16), output = Marshal.AllocHGlobal(64);
            const ulong R11Sentinel = 0x123456789ABCDEF0, SimdSentinel = 0x7654321001234567;
            try
            {
                ulong actor = unchecked((ulong)cells.ToInt64()), view = actor + 8;
                // The unchanged +10 continuation begins with MOVZX R8D,R14B.
                using (var continuation = ExecutableFunction.Create(a => {
                    a.movzx(r8d, r14b);
                    a.pushfq(); a.pop(r10); a.mov(__qword_ptr[r9 + 16], r10);
                    a.mov(__qword_ptr[r9 + 24], rsp); a.mov(__qword_ptr[r9 + 32], r11);
                    a.mov(__qword_ptr[r9 + 40], r8);
                    a.movq(r10, xmm0); a.mov(__qword_ptr[r9 + 48], r10);
                    a.mov(__dword_ptr[r9 + 56], eax);
                    a.pop(r14); a.pop(r12); a.pop(rbx); a.ret();
                }))
                // Isolate only the RIP target of the two original instructions.
                // This block has exactly their semantics, including flag neutrality.
                using (var original = ExecutableFunction.Create(a => {
                    a.cmovne(eax, r12d);
                    a.push(r11); a.mov(r11, view); a.mov(__dword_ptr[r11], eax); a.pop(r11);
                    a.jmp(unchecked((ulong)continuation.EntryPoint.ToInt64()));
                }))
                using (var bridge = ExecutableFunction.Create(a => ControlInputBridge.Emit(a, actor, view,
                    unchecked((ulong)original.EntryPoint.ToInt64()), unchecked((ulong)continuation.EntryPoint.ToInt64()))))
                using (var caller = ExecutableFunction.Create(a => {
                    a.push(rbx); a.push(r12); a.push(r14);
                    a.mov(__qword_ptr[r9], rsp);
                    a.mov(rax, SimdSentinel); a.movq(xmm0, rax);
                    a.mov(r11, R11Sentinel);
                    a.mov(ebx, ecx); a.mov(eax, ebx); a.mov(r12d, 0); a.mov(r14d, 0x1234AB);
                    a.cmp(edx, 0);
                    a.pushfq(); a.pop(r10); a.mov(__qword_ptr[r9 + 8], r10);
                    a.jmp(unchecked((ulong)bridge.EntryPoint.ToInt64()));
                }))
                {
                    var invoke = Marshal.GetDelegateForFunctionPointer<InputFixture>(caller.EntryPoint);
                    foreach (int selected in new[] { 0, 1, 3, 8, 9, -1 })
                    foreach (int permit in new[] { 0, 1, 3, 8, 9, -1 })
                    foreach (int spectator in new[] { 0, 1, -1 })
                    {
                        Marshal.WriteInt32(cells, permit); Marshal.WriteInt32(cells, 8, 77);
                        invoke(selected, spectator, 0, output);
                        int expected = permit == 0 ? spectator == 0 ? selected : 0 :
                            selected >= 1 && selected <= 8 && permit == selected ? selected : 0;
                        Assert.AreEqual(expected, Marshal.ReadInt32(cells, 8));
                        Assert.AreEqual(expected, Marshal.ReadInt32(output, 56));
                        Assert.AreEqual(Marshal.ReadInt64(output, 0), Marshal.ReadInt64(output, 24), "Stack pointer changed.");
                        // Every arithmetic flag, direction flag and the rest of RFLAGS
                        // are returned intact. Only EAX/native view are permitted outputs.
                        Assert.AreEqual(Marshal.ReadInt64(output, 8), Marshal.ReadInt64(output, 16), "Incoming flags changed.");
                        Assert.AreEqual(R11Sentinel, unchecked((ulong)Marshal.ReadInt64(output, 32)));
                        Assert.AreEqual(0xABL, Marshal.ReadInt64(output, 40));
                        Assert.AreEqual(SimdSentinel, unchecked((ulong)Marshal.ReadInt64(output, 48)));
                    }
                    GC.KeepAlive(invoke);
                }
            }
            finally { Marshal.FreeHGlobal(output); Marshal.FreeHGlobal(cells); }
        }
    }
}
