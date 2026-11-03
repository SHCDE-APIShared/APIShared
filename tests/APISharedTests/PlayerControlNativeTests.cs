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
        private sealed class ViewFixture
        {
            internal readonly object Gate = new object();
            internal long World = 1;
            internal int View = 2;
            internal bool FailRestore;
            internal Action<int> DuringWrite;
            internal readonly System.Collections.Generic.Queue<Action> Pending = new System.Collections.Generic.Queue<Action>();
            internal readonly System.Collections.Generic.List<string> Errors = new System.Collections.Generic.List<string>();
            internal readonly System.Collections.Generic.List<PlayerViewIdentity.FailureRecord> Faults = new System.Collections.Generic.List<PlayerViewIdentity.FailureRecord>();
            internal readonly PlayerViewIdentity Core;
            internal readonly APIShared.PlayerViewIdentityRuntime Runtime;
            internal readonly APIShared.PlayerViewIdentityOwner Owner;
            internal ViewFixture()
            {
                Core = new PlayerViewIdentity(() => System.Threading.Monitor.IsEntered(Gate), e => e == World,
                    () => View, p => { if (p == 6 && FailRestore) throw new InvalidOperationException("restore failed"); View = p; DuringWrite?.Invoke(p); });
                Runtime = new APIShared.PlayerViewIdentityRuntime(Core, Gate, action => { Pending.Enqueue(action); return true; }, Errors.Add);
                Owner = new APIShared.PlayerViewIdentityOwner("spectator-fixture", Faults.Add);
            }
            internal PlayerViewIdentity.Lease Acquire(object owner = null)
            { lock (Gate) { Assert.IsTrue(Core.TryBind(owner ?? Owner, World, View, out var lease, out string reason), reason); return lease; } }
            internal void Drain() { while (Pending.Count > 0) Pending.Dequeue()(); }
        }

        [TestMethod]
        public void DormantParticipantProtectionSerializesSaveAndSendWithoutAllocations()
        {
            var f = new ViewFixture(); byte[] payload = new byte[2];
            bool serialized = true;
            Func<byte[], bool> submit = bytes => {
                serialized &= System.Threading.Monitor.IsEntered(f.Gate);
                return bytes.Length == 2;
            };
            Func<int, ulong> writer = value => {
                serialized &= System.Threading.Monitor.IsEntered(f.Gate);
                return 42UL;
            };
            for (int i = 0; i < 32; i++) {
                f.Runtime.RunSubmission(payload, submit);
                PlayerViewSaveScope.RunProtectedWriter(f.Core, 0, writer, f.Runtime);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) {
                f.Runtime.RunSubmission(payload, submit);
                PlayerViewSaveScope.RunProtectedWriter(f.Core, 0, writer, f.Runtime);
            }
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.IsTrue(serialized, "Even an unbound sender must serialize with concurrent view acquisition.");
        }

        [TestMethod]
        public void ClearingParticipantViewNeverReleasesAReentrantReplacementLease()
        {
            var f = new ViewFixture(); var old = f.Acquire(); f.View = 6;
            f.DuringWrite = p => {
                f.World = 2; f.View = 7;
                Assert.IsTrue(f.Core.RetireEndedWorld(1));
                Assert.IsFalse(f.Core.TryBind(new object(), 2, 7, out _, out _), "The restoration scope is still active.");
            };
            lock (f.Gate) Assert.IsFalse(f.Core.TryRestoreAndRelease(f.Owner, old, out _));
            Assert.AreEqual(7, f.View); f.DuringWrite = null;
            var nextOwner = new object(); var next = f.Acquire(nextOwner);
            Assert.IsTrue(next.Token > old.Token);
            lock (f.Gate) Assert.IsFalse(f.Core.TryRestoreAndRelease(f.Owner, old, out _));
            Assert.AreSame(next, f.Core.CurrentLease); Assert.AreEqual(7, f.View);
        }

        [TestMethod]
        public void UnboundSaveAndSendPreventReentrantViewAcquisitionUntilTheyReturn()
        {
            var f = new ViewFixture(); int written = 0, sent = 0;
            Assert.IsTrue(f.Runtime.RunSubmission(new byte[2], payload => {
                sent++;
                Assert.IsFalse(f.Core.TryBind(f.Owner, 1, 2, out _, out _));
                Assert.AreEqual(42UL, PlayerViewSaveScope.RunProtectedWriter(f.Core, 0, value => {
                    written++;
                    Assert.IsFalse(f.Core.TryBind(f.Owner, 1, 2, out _, out _));
                    Assert.AreEqual(2, f.View);
                    return 42UL;
                }, f.Runtime));
                Assert.IsTrue(f.Core.IsScopeActive, "The outer send scope must survive the nested writer.");
                return true;
            }));
            Assert.AreEqual(1, written); Assert.AreEqual(1, sent);
            Assert.IsFalse(f.Core.IsScopeActive); Assert.IsNull(f.Core.CurrentLease);
            Assert.ThrowsExactly<InvalidOperationException>(() => f.Runtime.RunSubmission(new byte[2], payload => {
                Assert.IsFalse(f.Core.TryBind(f.Owner, 1, 2, out _, out _));
                throw new InvalidOperationException("unbound original failed");
            }));
            Assert.IsFalse(f.Core.IsScopeActive);
            Assert.IsNotNull(f.Acquire(), "An exception must unwind the dormant scope and permit later view acquisition.");
        }

        [TestMethod]
        public void ProtectedFailureDispatchIsOncePerLeaseAndFailureInsteadOfOncePerSend()
        {
            var f = new ViewFixture(); var first = f.Acquire();
            Assert.IsTrue(f.Core.ReportFailure(first, "fixture failure"));
            f.Runtime.ObserveFailure(f.Core); f.Drain();
            for (int i = 0; i < 20; i++) f.Runtime.ObserveFailure(f.Core);
            f.Drain(); Assert.AreEqual(1, f.Errors.Count); Assert.AreEqual(1, f.Faults.Count);
            Assert.IsTrue(f.Core.ReportFailure(first, "different failure")); f.Runtime.ObserveFailure(f.Core); f.Drain();
            Assert.AreEqual(2, f.Errors.Count);
            lock (f.Gate) Assert.IsTrue(f.Core.TryRestoreAndRelease(f.Owner, first, out _));
            var next = f.Acquire(); Assert.IsFalse(f.Core.ReportFailure(first, "stale failure"));
            Assert.IsNull(f.Core.LastFailure);
            Assert.IsTrue(f.Core.ReportFailure(next, "different failure")); f.Runtime.ObserveFailure(f.Core); f.Drain();
            Assert.AreEqual(3, f.Errors.Count); Assert.AreEqual(3, f.Faults.Count);
        }

        [TestMethod]
        public void DelayedFailureCannotNotifyTheReplacementOwner()
        {
            var f = new ViewFixture(); var first = f.Acquire();
            f.Core.ReportFailure(first, "old failure"); f.Runtime.ObserveFailure(f.Core);
            lock (f.Gate) Assert.IsTrue(f.Core.TryRestoreAndRelease(f.Owner, first, out _));
            var secondOwner = new APIShared.PlayerViewIdentityOwner("control-fixture", f.Faults.Add);
            var second = f.Acquire(secondOwner); f.Drain();
            Assert.AreEqual(1, f.Errors.Count); Assert.AreEqual(0, f.Faults.Count);
            Assert.AreSame(second, f.Core.CurrentLease); Assert.IsNull(f.Core.LastFailure);
            Assert.IsFalse(f.Core.ReportFailure(first, "late error"));
        }

        [TestMethod]
        public void CapturedOldFailureCannotResetTheNewOwnersDeliveryState()
        {
            var f = new ViewFixture(); var first = f.Acquire();
            f.Core.ReportFailure(first, "old failure"); var captured = f.Core.LastFailure;
            lock (f.Gate) Assert.IsTrue(f.Core.TryRestoreAndRelease(f.Owner, first, out _));
            var second = f.Acquire(); f.Core.ReportFailure(second, "new failure");
            f.Runtime.ObserveFailure(f.Core);
            f.Runtime.ObserveFailure(f.Core, captured); // An observation delayed across a role change.
            f.Runtime.ObserveFailure(f.Core); f.Drain();
            Assert.AreEqual(1, f.Errors.Count); Assert.AreEqual(1, f.Faults.Count);
            Assert.AreSame(second, f.Faults[0].Lease);
        }

        [TestMethod]
        public void OldPerspectiveFailureCannotInvalidateAClearedOrReplacementView()
        {
            APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
            var f = new ViewFixture(); var first = f.Acquire();
            try
            {
                Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryChangeView(6, () => f.View, p => f.View = p, out _, 2));
                f.Core.ReportFailure(first, "old fault"); var failure = f.Core.LastFailure;
                lock (f.Gate) Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryInvalidateOwnedView(f.Core, f.Owner, first, failure));
                Assert.AreEqual(-1, APIShared.PlayerPerspectiveAPI.GetViewedPlayerId());
                lock (f.Gate) Assert.IsTrue(f.Core.TryRestoreAndRelease(f.Owner, first, out _));
                APIShared.PlayerPerspectiveAPI.ClearSpectatorView();
                var next = f.Acquire();
                Assert.IsTrue(APIShared.PlayerPerspectiveAPI.TryChangeView(4, () => f.View, p => f.View = p, out _, 2));
                lock (f.Gate) Assert.IsFalse(APIShared.PlayerPerspectiveAPI.TryInvalidateOwnedView(f.Core, f.Owner, first, failure));
                Assert.AreEqual(4, APIShared.PlayerPerspectiveAPI.GetViewedPlayerId());
                Assert.AreSame(next, f.Core.CurrentLease);
            }
            finally { APIShared.PlayerPerspectiveAPI.ClearSpectatorView(); }
        }

        [TestMethod]
        public void EliminatedViewSendScopeKeepsIssuerForNestedSendAndSaveAndRestoresTheView()
        {
            var f = new ViewFixture(); var lease = f.Acquire(); f.View = 6;
            Assert.IsTrue(f.Runtime.TryRunWithOriginalIdentity(0, ignored => {
                Assert.AreEqual(2, f.View); Assert.IsTrue(f.Core.IsScopeActive);
                Assert.IsTrue(f.Runtime.RunSubmission(new byte[2], bytes => { Assert.AreEqual(2, f.View); return true; }));
                Assert.AreEqual(42UL, PlayerViewSaveScope.RunProtectedWriter(f.Core, 0,
                    value => { Assert.AreEqual(2, f.View); return 42UL; }, f.Runtime));
                Assert.IsFalse(f.Core.TryRestoreAndRelease(f.Owner, lease, out _));
                return 7;
            }, out int result));
            Assert.AreEqual(7, result); Assert.AreEqual(6, f.View);
            Assert.ThrowsExactly<InvalidOperationException>(() => f.Runtime.TryRunWithOriginalIdentity<int, bool>(0,
                value => throw new InvalidOperationException("native failure"), out _));
            Assert.AreEqual(6, f.View);
            Assert.IsTrue(f.Runtime.TryRunWithOriginalIdentity(0, ignored => { f.World = 2; f.View = 8; return true; }, out _));
            Assert.AreEqual(8, f.View);
        }

        [TestMethod]
        public void ProtectedSendDoesNotRequestSteamFallbackAfterQueuingOrScopeRejection()
        {
            var f = new ViewFixture(); f.Acquire(); f.View = 6; f.FailRestore = true; int submissions = 0;
            Assert.IsTrue(f.Runtime.RunSubmission(new byte[2], bytes => { submissions++; Assert.AreEqual(2, f.View); return true; }));
            Assert.AreEqual(1, submissions); Assert.IsNotNull(f.Core.LastFailure); f.Drain();
            Assert.AreEqual(1, f.Faults.Count);
            f.World = 2;
            Assert.ThrowsExactly<InvalidOperationException>(() => f.Runtime.RunSubmission(new byte[2], bytes => { submissions++; return false; }));
            Assert.AreEqual(1, submissions);
        }

        [TestMethod]
        public void PublishedSaveWriterRejectsAnotherIdentityCore()
        {
            var first = new ViewFixture(); var second = new ViewFixture();
            PlayerViewSaveScope.ValidateIdentityBinding(first.Core, first.Core);
            Assert.ThrowsExactly<InvalidOperationException>(() => PlayerViewSaveScope.ValidateIdentityBinding(first.Core, second.Core));
            Assert.ThrowsExactly<ArgumentNullException>(() => PlayerViewSaveScope.ValidateIdentityBinding(first.Core, null));
            bool written = false;
            Assert.AreEqual(0UL, PlayerViewSaveScope.RunProtectedWriter(first.Core, 0, value => { written = true; return 1UL; }, second.Runtime));
            Assert.IsFalse(written, "A writer adapter cannot use a coordinator with a different core.");
        }

        [TestMethod]
        public void ViewReleaseRequiresTheExactOwnerAndHandleEvenInTheSameWorld()
        {
            var f = new ViewFixture(); var lease = f.Acquire(); f.View = 6;
            var forged = new PlayerViewIdentity.Lease(f.Owner, lease.Epoch, lease.Token, lease.Human);
            lock (f.Gate)
            {
                Assert.IsFalse(f.Core.TryRestoreAndRelease(new object(), lease, out _));
                Assert.IsFalse(f.Core.TryRestoreAndRelease(f.Owner, forged, out _));
                Assert.AreSame(lease, f.Core.CurrentLease); Assert.AreEqual(6, f.View);
                f.World = 2; f.View = 7;
                Assert.IsFalse(f.Core.TryRestoreAndRelease(f.Owner, lease, out _), "A role cannot retire a dead native world's lease.");
                Assert.AreSame(lease, f.Core.CurrentLease); Assert.AreEqual(7, f.View);
            }
            f.Runtime.RetireEndedWorld();
            Assert.IsNull(f.Core.CurrentLease); Assert.AreEqual(7, f.View);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InputFixture(int nativeView, int spectatorFlag, int ignored, IntPtr output);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        [TestMethod]
        public void ProtectedNativeWriterRetainsWrittenResultIfPostWriteWorldValidationThrows()
        {
            int view = 1, calls = 0;
            var gate = new object();
            bool failWorldCheck = false;
            var guard = new PlayerViewIdentity(() => System.Threading.Monitor.IsEntered(gate),
                epoch => failWorldCheck ? throw new InvalidOperationException("world reader failed") : epoch == 1,
                () => view, value => view = value);
            var runtime = new APIShared.PlayerViewIdentityRuntime(guard, gate, action => false, message => { });
            lock (gate) Assert.IsTrue(guard.TryBind(new object(), 1, 1, out _, out _)); view = 3;
            const ulong WrittenResult = 0xFEDCBA9876543210;
            Assert.IsNull(guard.LastFailure);
            Assert.AreEqual(WrittenResult, PlayerViewSaveScope.RunProtectedWriter(guard, 0, value => {
                calls++; Assert.AreEqual(1, view); failWorldCheck = true; return WrittenResult;
            }, runtime));
            Assert.AreEqual(1, calls); Assert.AreEqual(1, guard.ProtectedHumanId);
            Assert.IsNotNull(guard.LastFailure);
            // An exception before writing does not replay the writer and
            // cannot reuse the successful result of a previous invocation.
            Assert.AreEqual(0UL, PlayerViewSaveScope.RunProtectedWriter(guard, 0, value => {
                calls++; return WrittenResult;
            }, runtime));
            Assert.AreEqual(1, calls); Assert.IsNotNull(guard.LastFailure);
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
                NativeDetour<PlayerViewSaveScope.WriterCall> hook = null;
                int calls = 0;
                var callback = new PlayerViewSaveScope.WriterCall((manager, sections, mapSave) => {
                    calls++;
                    return hook.Original(manager, sections, mapSave) + 100;
                });
                var request = new DetourRequest<PlayerViewSaveScope.WriterCall> {
                    Name = "Player-control save writer fixture", Callback = callback,
                    TargetAddress = unchecked((ulong)target.EntryPoint.ToInt64())
                };
                try
                {
                    // Match Initialize inside the startup patch window: exactly
                    // the production options with thread suspension disabled.
                    // No other thread can execute this private fixture.
                    var backend = new NativeDetourBackend(PlayerViewSaveScope.Backend.Options with { SuspendThreadsDuringPatch = false });
                    hook = (NativeDetour<PlayerViewSaveScope.WriterCall>)backend.CreateDetour(in request);
                    PlayerViewSaveScope.ValidatePrepared(hook, target.EntryPoint);
                    var invoke = Marshal.GetDelegateForFunctionPointer<PlayerViewSaveScope.WriterCall>(target.EntryPoint);
                    IntPtr first = new IntPtr(0x12345678), second = new IntPtr(0x76543210);
                    ulong original = unchecked((ulong)(first.ToInt64() ^ second.ToInt64())) + 0xAB;
                    Assert.AreEqual(original, invoke(first, second, 0xAB)); Assert.AreEqual(0, calls);
                    hook.Enable();
                    APIShared.NativeSites.NativeIndirectEntry.Validate(hook, target.EntryPoint, PlayerViewSaveScope.Entry);
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
                        int expected = permit == 0 || spectator == 0 ? spectator == 0 ? selected : 0 :
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

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DispatcherFixture(int nativeView, int spectatorFlag, int ignored, IntPtr output);

        // Regression: the dispatcher's nested re-dispatch zeroes the view a second time. With the
        // production backend options Indirect displaces exactly CMOVNE+XOR (6 bytes); the unchanged
        // RIP store at +6 must write the stub's decision, and the displaced XOR R9D must still run.
        [TestMethod]
        public void ActualDispatcherBackendDisplacesSixBytesAndTheUnchangedStoreWritesTheDecision()
        {
            IntPtr page = VirtualAlloc(IntPtr.Zero, new UIntPtr(4096), 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, page);
            IntPtr output = Marshal.AllocHGlobal(64);
            NativeDetour<DispatcherFixture> hook = null;
            try
            {
                ulong target = unchecked((ulong)page.ToInt64()), view = target + 2048, actor = view + 8;
                var code = new Assembler(64);
                // Exact dispatcher window: CMOVNE EAX,EDI; XOR R9D,R9D; MOV [RIP+disp],EAX.
                byte[] window = { 0x0F, 0x45, 0xC7, 0x45, 0x33, 0xC9, 0x89, 0x05, 0, 0, 0, 0 };
                Buffer.BlockCopy(BitConverter.GetBytes(2048 - window.Length), 0, window, 8, 4);
                code.db(window);
                // R9 is zeroed by the original, so the recorder uses R13 for the output buffer.
                code.pushfq(); code.pop(r10); code.mov(__qword_ptr[r13 + 16], r10);
                code.mov(__qword_ptr[r13 + 24], rsp); code.mov(__qword_ptr[r13 + 32], r11);
                code.mov(__qword_ptr[r13 + 40], r9); code.mov(__dword_ptr[r13 + 56], eax);
                code.pop(r13); code.pop(rdi); code.pop(rbx); code.ret();
                byte[] bytes;
                using (var stream = new MemoryStream())
                {
                    code.Assemble(new StreamCodeWriter(stream), target); bytes = stream.ToArray();
                }
                Marshal.Copy(bytes, 0, page, bytes.Length);
                Assert.IsTrue(FlushInstructionCache(GetCurrentProcess(), page, new UIntPtr((uint)bytes.Length)));
                int unexpectedCallbacks = 0;
                DispatcherFixture callback = (v, s, unused, pointer) => unexpectedCallbacks++;
                var bridge = new ControlInputBridge(ControlInputSite.Dispatcher, actor, view, target + 6);
                var request = new DetourRequest<DispatcherFixture> {
                    Name = "private dispatcher input boundary", TargetAddress = target, Callback = callback,
                    IntermediaryFactory = bridge
                };
                hook = (NativeDetour<DispatcherFixture>)ControlInputNative.CreateBackend(window.Length, false).CreateDetour(in request);
                Assert.AreEqual(6, hook.DisplacedByteCount); Assert.AreEqual(DetourScheme.Indirect, hook.Scheme);
                bridge.Validate(hook, page, window, false);
                hook.Enable(); bridge.Validate(hook, page, window);
                var wrongContinuation = new ControlInputBridge(ControlInputSite.Dispatcher, actor, view, target + 10);
                Assert.ThrowsExactly<InvalidOperationException>(() => wrongContinuation.Validate(hook, page, window));
                var wrongSite = new ControlInputBridge(ControlInputSite.RunTick, actor, view, target + 6);
                Assert.ThrowsExactly<InvalidOperationException>(() => wrongSite.Validate(hook, page, window));
                using (var caller = ExecutableFunction.Create(a => {
                    a.push(rbx); a.push(rdi); a.push(r13);
                    a.mov(r13, r9); a.mov(__qword_ptr[r13], rsp); a.mov(r11, 0x123456789ABCDEF0UL);
                    a.mov(ebx, ecx); a.mov(eax, ebx); a.mov(edi, 0); a.mov(r9, 0x5555AAAA5555AAAAUL);
                    a.cmp(edx, 0); a.pushfq(); a.pop(r10); a.mov(__qword_ptr[r13 + 8], r10);
                    a.jmp(target);
                }))
                {
                    var invoke = Marshal.GetDelegateForFunctionPointer<DispatcherFixture>(caller.EntryPoint);
                    foreach (int selected in new[] { 0, 1, 3, 8, 9, -1 })
                    foreach (int permit in new[] { 0, 1, 3, 8, 9, -1 })
                    foreach (int spectator in new[] { 0, 1, -1 })
                    {
                        Marshal.WriteInt32(new IntPtr(unchecked((long)actor)), permit);
                        Marshal.WriteInt32(new IntPtr(unchecked((long)view)), 77);
                        invoke(selected, spectator, 0, output);
                        int expected = permit == 0 || spectator == 0 ? spectator == 0 ? selected : 0 :
                            selected >= 1 && selected <= 8 && permit == selected ? selected : 0;
                        Assert.AreEqual(expected, Marshal.ReadInt32(new IntPtr(unchecked((long)view))));
                        Assert.AreEqual(expected, Marshal.ReadInt32(output, 56));
                        // No flag assertion: Vanilla's own XOR R9D rewrites them on the original path,
                        // and the next real instruction (XOR R8D,R8D) overwrites them before any use.
                        Assert.AreEqual(Marshal.ReadInt64(output, 0), Marshal.ReadInt64(output, 24), "Stack pointer changed.");
                        Assert.AreEqual(0x123456789ABCDEF0UL, unchecked((ulong)Marshal.ReadInt64(output, 32)));
                        Assert.AreEqual(0L, Marshal.ReadInt64(output, 40), "The displaced XOR R9D,R9D must run on every branch.");
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
        public void ProductionInputWindowsMatchTheAuditedInstructionForms()
        {
            // The reference bytes are the audited contract for the hash-bound installer; decode them
            // at their real RVAs so a constant typo cannot pass the validator's instruction checks.
            ulong module = 0x180000000UL, view = module + ControlInputNative.ViewRva;
            var runTick = Decoder.Create(64, new ByteArrayCodeReader(ControlInputNative.RunTickWindow));
            runTick.IP = module + ControlInputNative.RunTickRva;
            runTick.Decode(); var runStore = runTick.Decode();
            Assert.AreEqual(view, runStore.IPRelativeMemoryAddress);
            Assert.AreEqual(14, ControlInputNative.RunTickWindow.Length);
            var dispatcher = Decoder.Create(64, new ByteArrayCodeReader(ControlInputNative.DispatcherWindow));
            dispatcher.IP = module + ControlInputNative.DispatcherRva;
            dispatcher.Decode(); dispatcher.Decode(); var dispatchStore = dispatcher.Decode();
            Assert.AreEqual(view, dispatchStore.IPRelativeMemoryAddress);
            Assert.AreEqual(Code.Mov_rm32_r32, dispatchStore.Code);
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
                        int expected = permit == 0 || spectator == 0 ? spectator == 0 ? selected : 0 :
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
