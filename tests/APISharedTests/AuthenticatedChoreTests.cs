using System;
using APIShared.PlayerControl;
using APIShared.Networking;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SHCDESE.Detours;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Transport-level tests for the native-issuer-aware Chore channel; control protocol tests stay in PlayerControlTests.
    [TestClass]
    public sealed class AuthenticatedChoreTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [TestMethod]
        public void NativeQueueBindingRequiresTheExactSingleNativeTarget()
        {
            // Export addresses supply real, distinct native targets. These delegates are
            // never invoked: only binding identity is tested, independent of the exports' ABI.
            IntPtr module = GetModuleHandle("kernel32.dll");
            Assert.AreNotEqual(IntPtr.Zero, module);
            IntPtr address = GetProcAddress(module, "GetCurrentProcessId");
            IntPtr otherAddress = GetProcAddress(module, "GetCurrentThreadId");
            Assert.AreNotEqual(IntPtr.Zero, address);
            Assert.AreNotEqual(IntPtr.Zero, otherAddress);
            Assert.AreNotEqual(address, otherAddress);
            var queue = Marshal.GetDelegateForFunctionPointer<BulkChoreDetours.c_game_queue_chore_delegate>(address);
            var other = Marshal.GetDelegateForFunctionPointer<BulkChoreDetours.c_game_queue_chore_delegate>(otherAddress);
            BulkChoreDetours.c_game_queue_chore_delegate managed = (manager, opcode) => Assert.Fail("Binding validation must not invoke delegates.");

            Assert.IsTrue(NativeChoreQueueBinding.Matches(queue, address));
            Assert.IsFalse(NativeChoreQueueBinding.Matches(queue, otherAddress));
            Assert.IsFalse(NativeChoreQueueBinding.Matches(other, address));
            Assert.IsFalse(NativeChoreQueueBinding.Matches(null, address));
            Assert.IsFalse(NativeChoreQueueBinding.Matches(queue, IntPtr.Zero));
            Assert.IsFalse(NativeChoreQueueBinding.Matches(managed, address));
            Assert.IsFalse(NativeChoreQueueBinding.Matches(queue + queue, address));
            Assert.IsFalse(NativeChoreQueueBinding.Matches(managed + queue, address));
            Assert.IsFalse(NativeChoreQueueBinding.Matches(queue + managed, address));
        }

        [TestMethod]
        public void NativeIssuerMustMatchPendingSlotAndExecutionContextNotPayloadOrView()
        {
            const long manager = 0x1000;
            const long entry = manager + 0xB0BF8 + 0x500;
            var ints = new Dictionary<long, int> {
                [manager + 0x84CCC] = 0, [manager + 0x84CC8] = 1,
                [entry + 4] = 3, [entry + 12] = 300000017, [entry] = 100,
                [manager + 0x36FA4C] = 3
            };
            int opcode = 106;
            Func<IntPtr, int> read = p => ints[p.ToInt64()];
            Func<IntPtr, byte> readByte = p => { Assert.AreEqual(entry + 8, p.ToInt64()); return (byte)opcode; };
            Assert.IsTrue(NativeChoreContext.TryRead(manager, read, readByte, out var context));
            Assert.AreEqual(3, context.Sender); Assert.AreEqual(300000017, context.CommandId); Assert.AreEqual(100, context.ScheduledTick);
            ints[manager + 0x36FA4C] = 2;
            Assert.IsFalse(NativeChoreContext.TryRead(manager, read, readByte, out _));
            ints[manager + 0x36FA4C] = 3;
            ints[manager + 0x84CCC] = 1;
            Assert.IsFalse(NativeChoreContext.TryRead(manager, read, readByte, out _));
            ints[manager + 0x84CCC] = 0; opcode = 17;
            Assert.IsFalse(NativeChoreContext.TryRead(manager, read, readByte, out _));
            opcode = 106; ints[entry + 4] = 9;
            Assert.IsFalse(NativeChoreContext.TryRead(manager, read, readByte, out _));
            ints[entry + 4] = 3;
            int commandReads = 0;
            Func<IntPtr, int> racing = p => p.ToInt64() == entry + 12 && ++commandReads == 2 ? 300000018 : ints[p.ToInt64()];
            Assert.IsFalse(NativeChoreContext.TryRead(manager, racing, readByte, out _));
        }

        [TestMethod]
        public void StrictNativeSubmissionNeedsTheExactManagerAndCannotOverwriteAFullQueue()
        {
            const long image = 0x180000000;
            long manager = image + NativeChoreContext.ManagerRva;
            long end = manager + 0x370C00;
            Assert.IsTrue(NativeChoreContext.IsManagerBinding(image, end, manager));
            Assert.IsFalse(NativeChoreContext.IsManagerBinding(image, end - 1, manager));
            Assert.IsFalse(NativeChoreContext.IsManagerBinding(image, end + 0x10000, manager + 4));
            var states = new byte[500];
            for (int i = 0; i < states.Length; i++) states[i] = 1;
            int cursor = 499, reads = 0;
            Func<IntPtr, int> readInt = address => {
                Assert.AreEqual(manager + 0x370BF4, address.ToInt64()); return cursor;
            };
            Func<IntPtr, byte> readByte = address => {
                long relative = address.ToInt64() - manager - 0xB0BF8 - 9;
                Assert.AreEqual(0L, relative % 0x500);
                int slot = checked((int)(relative / 0x500));
                Assert.IsTrue(slot >= 0 && slot < 500); reads++; return states[slot];
            };
            Assert.IsFalse(NativeChoreContext.HasFreeQueueSlot(manager, readInt, readByte));
            Assert.AreEqual(500, reads);
            states[0] = 10; reads = 0;
            Assert.IsTrue(NativeChoreContext.HasFreeQueueSlot(manager, readInt, readByte));
            Assert.AreEqual(1, reads);
            states[0] = 0xFF; reads = 0; // signed -1 is NOT a reusable state.
            Assert.IsFalse(NativeChoreContext.HasFreeQueueSlot(manager, readInt, readByte));
            Assert.AreEqual(500, reads);
            cursor = 498; states[499] = 0; reads = 0;
            Assert.IsTrue(NativeChoreContext.HasFreeQueueSlot(manager, readInt, readByte));
            Assert.AreEqual(1, reads);
            cursor = -1; reads = 0;
            Assert.IsFalse(NativeChoreContext.HasFreeQueueSlot(manager, readInt, readByte));
            Assert.AreEqual(0, reads);
            cursor = 500;
            Assert.IsFalse(NativeChoreContext.HasFreeQueueSlot(manager, readInt, readByte));
            Assert.AreEqual(0, reads);
        }

        [TestMethod]
        public void AuthenticatedDeliveryRejectsWorkersBeforeConsumerPredicatesAndRechecksTheNativeContext()
        {
            const short packetId = 42;
            var packet = new object();
            var native = new NativeChoreExecution(2, 200000001, 100, 0x1000, 1);
            bool hasNative = false, changeDuringValidation = false, authorized = true;
            int captures = 0, enabledCalls = 0, validations = 0, authorizations = 0, executions = 0, reports = 0;
            CaptureAuthenticatedChore capture = (short id, out NativeChoreExecution value) => {
                Assert.AreEqual(packetId, id); captures++; value = native; return hasNative;
            };
            Func<bool> enabled = () => { enabledCalls++; return true; };
            Func<object, bool> validate = value => { Assert.AreSame(packet, value); validations++; return true; };
            Func<AuthenticatedChoreMessage<object>, bool> authorize = message => {
                Assert.AreEqual(2, message.SenderPlayerId); authorizations++;
                if (changeDuringValidation) native = new NativeChoreExecution(3, 300000001, 100, 0x1000, 2);
                return authorized;
            };
            Action<AuthenticatedChoreMessage<object>> execute = message =>
            {
                Assert.IsTrue(APIShared.Simulation.SimulationMutationScope.IsAuthenticatedChore);
                executions++;
            };
            Action<string, Exception> report = (reason, error) => reports++;
            Action<SHCDESE.EventAPI.Network.ReceiveCustomPacketEventArgs<object>> receive = args =>
                AuthenticatedChoreDelivery<object>.Receive(args, packetId, capture, enabled, validate, authorize, execute, report);
            var worker = new System.Threading.Thread(() => {
                receive(new SHCDESE.EventAPI.Network.ReceiveCustomPacketEventArgs<object>(
                    SHCDESE.EventAPI.EventHookPhase.Post, packetId, packet, new Steamworks.CSteamID(123)));
                // Even an absent Steam sender is insufficient without this exact
                // thread's verified native dispatcher frame.
                receive(new SHCDESE.EventAPI.Network.ReceiveCustomPacketEventArgs<object>(SHCDESE.EventAPI.EventHookPhase.Post, packetId, packet));
            });
            worker.Start(); worker.Join();
            Assert.AreEqual(1, captures); Assert.AreEqual(2, reports);
            Assert.AreEqual(0, enabledCalls); Assert.AreEqual(0, validations);
            Assert.AreEqual(0, authorizations); Assert.AreEqual(0, executions);
            hasNative = true;
            receive(new SHCDESE.EventAPI.Network.ReceiveCustomPacketEventArgs<object>(SHCDESE.EventAPI.EventHookPhase.Post, packetId + 1, packet));
            Assert.AreEqual(1, captures); Assert.AreEqual(0, enabledCalls);
            receive(new SHCDESE.EventAPI.Network.ReceiveCustomPacketEventArgs<object>(SHCDESE.EventAPI.EventHookPhase.Post, packetId, packet));
            Assert.AreEqual(3, captures); Assert.AreEqual(1, executions);
            Assert.IsFalse(APIShared.Simulation.SimulationMutationScope.IsAuthenticatedChore);
            changeDuringValidation = true;
            receive(new SHCDESE.EventAPI.Network.ReceiveCustomPacketEventArgs<object>(SHCDESE.EventAPI.EventHookPhase.Post, packetId, packet));
            Assert.AreEqual(5, captures); Assert.AreEqual(2, enabledCalls); Assert.AreEqual(2, validations);
            Assert.AreEqual(2, authorizations); Assert.AreEqual(1, executions); Assert.AreEqual(4, reports);
            native = new NativeChoreExecution(2, 200000001, 100, 0x1000, 1);
            changeDuringValidation = false;
            authorized = false;
            receive(new SHCDESE.EventAPI.Network.ReceiveCustomPacketEventArgs<object>(
                SHCDESE.EventAPI.EventHookPhase.Post, packetId, packet));
            Assert.AreEqual(3, authorizations);
            Assert.AreEqual(1, executions);
        }

        [TestMethod]
        public void StrictChoreSubmissionSerializesOnceAndNeverFallsBackOrRetries()
        {
            var packet = new object(); int serializations = 0, queues = 0;
            byte[] body = { 7, 8, 9 };
            Func<object, byte[]> serialize = value => { Assert.AreSame(packet, value); serializations++; return body; };
            var result = ChoreSendPolicy.SendSerializedOnce(packet, 0x1234, true, serialize, blob => {
                queues++; CollectionAssert.AreEqual(new byte[] { 0x34, 0x12, 7, 8, 9 }, blob); return true;
            }, 1200);
            Assert.IsTrue(result.Submitted); Assert.AreEqual(1, serializations); Assert.AreEqual(1, queues);
            body[0] = 99;
            Assert.AreEqual(7, result.CopySerializedBody()[0]);
            result = ChoreSendPolicy.SendSerializedOnce(packet, 1, true, serialize, blob => { queues++; return false; }, 1200);
            Assert.AreEqual(NetworkSendStatus.Unavailable, result.Status);
            Assert.AreEqual(2, serializations); Assert.AreEqual(2, queues);
            result = ChoreSendPolicy.SendSerializedOnce(packet, 1, true, serialize, blob => { queues++; throw new InvalidOperationException("queue"); }, 1200);
            Assert.AreEqual(NetworkSendStatus.TransportFailed, result.Status);
            Assert.AreEqual(3, serializations); Assert.AreEqual(3, queues);
            Assert.AreEqual(NetworkSendStatus.Unavailable,
                ChoreSendPolicy.SendSerializedOnce(packet, 1, false, serialize, blob => { queues++; return true; }, 1200).Status);
            Assert.AreEqual(NetworkSendStatus.TooLarge,
                ChoreSendPolicy.SendSerializedOnce(packet, 1, true, value => new byte[1199], blob => { queues++; return true; }, 1200).Status);
            Assert.AreEqual(3, serializations); Assert.AreEqual(3, queues);
        }

        private static bool UnreviewedSubmit(byte[] blob) => blob.Length != 0;
        private static bool SubmissionWithUnknownHelper(byte[] blob) => UnreviewedSubmit(blob);

        [TestMethod]
        public void StrictTransportValidatesTheInstalledExtenderMethodAndRejectsUnauditedHelpers()
        {
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var method = typeof(SHCDESE.API.GameNetworkAPI).GetMethod("SendScriptExtenderChorePayload", flags);
            Assert.IsNotNull(method);
            NativeChoreTransport.ValidateSubmissionMethod(method);
            using (var definition = new MonoMod.Utils.DynamicMethodDefinition(method))
            {
                foreach (var instruction in definition.Definition.Body.Instructions)
                    if (instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_S && Convert.ToInt32(instruction.Operand) == 106)
                        instruction.Operand = (sbyte)107;
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeChoreTransport.ValidateSubmissionBody(definition.Definition));
            }
            using (var definition = new MonoMod.Utils.DynamicMethodDefinition(method))
            {
                bool changed = false;
                foreach (var instruction in definition.Definition.Body.Instructions)
                {
                    if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Ldsfld ||
                        !(instruction.Operand is Mono.Cecil.FieldReference field) ||
                        field.DeclaringType.FullName != "EngineInterface" || field.Name != "threadLock") continue;
                    instruction.OpCode = Mono.Cecil.Cil.OpCodes.Ldnull; instruction.Operand = null; changed = true;
                }
                Assert.IsTrue(changed);
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeChoreTransport.ValidateSubmissionBody(definition.Definition));
            }
            using (var definition = new MonoMod.Utils.DynamicMethodDefinition(method))
            {
                foreach (var instruction in definition.Definition.Body.Instructions)
                    if (instruction.Operand is Mono.Cecil.MethodReference call &&
                        call.DeclaringType.FullName.Contains("c_game_queue_chore_delegate") && call.Name == "Invoke")
                        definition.Definition.Body.ExceptionHandlers[0].TryEnd = instruction;
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeChoreTransport.ValidateSubmissionBody(definition.Definition));
            }
            using (var definition = new MonoMod.Utils.DynamicMethodDefinition(method))
            {
                foreach (var instruction in definition.Definition.Body.Instructions)
                    if (instruction.Operand is Mono.Cecil.MethodReference call && call.DeclaringType.FullName == "System.Threading.Monitor" &&
                        call.Name == "Exit") instruction.Previous.OpCode = Mono.Cecil.Cil.OpCodes.Ldloc_1;
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeChoreTransport.ValidateSubmissionBody(definition.Definition));
            }
            var foreign = typeof(AuthenticatedChoreTests).GetMethod(nameof(SubmissionWithUnknownHelper), flags);
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeChoreTransport.ValidateSubmissionMethod(foreign));
        }

        [TestMethod]
        public void InstalledNativeDispatcherMustKeepTheExactPacketIdBodyCopyAndAbsentSteamSender()
        {
            var method = typeof(SHCDESE.API.GameNetworkAPI).GetMethod("DispatchReceivedScriptExtenderPayload",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            NativeChoreDispatchContract.Validate(method);
            using (var definition = new MonoMod.Utils.DynamicMethodDefinition(method))
            {
                foreach (var instruction in definition.Definition.Body.Instructions)
                {
                    if (!(instruction.Operand is Mono.Cecil.MethodReference call) ||
                        call.DeclaringType.FullName != "System.Buffer" || call.Name != "BlockCopy") continue;
                    var prefix = instruction.Previous;
                    for (int i = 0; i < 5; i++) prefix = prefix.Previous;
                    Assert.AreEqual(Mono.Cecil.Cil.OpCodes.Ldc_I4_2, prefix.OpCode);
                    prefix.OpCode = Mono.Cecil.Cil.OpCodes.Ldc_I4_3;
                }
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeChoreDispatchContract.ValidateBody(definition.Definition));
            }
        }

        [TestMethod]
        public void NativeDispatchIdentityIsScopedToTheExactPacketAndCannotLeakAcrossNestedCallsOrThreads()
        {
            var first = new NativeChoreExecution(1, 100000001, 7, 0x1000, 3);
            var second = new NativeChoreExecution(2, 200000002, 7, 0x1000, 4);
            var outer = new NativeChoreDispatch.Frame(42, first);
            var inner = new NativeChoreDispatch.Frame(43, second);
            byte[] blob = Array.Empty<byte>();
            Assert.IsFalse(NativeChoreDispatch.Current.Valid);
            NativeChoreDispatch.DispatchCaptured(outer, blob, bytes => {
                Assert.IsTrue(NativeChoreDispatch.Matches(NativeChoreDispatch.Current, 42, true, first));
                Assert.IsFalse(NativeChoreDispatch.Matches(NativeChoreDispatch.Current, 43, true, first));
                Assert.IsFalse(NativeChoreDispatch.Matches(NativeChoreDispatch.Current, 42, true, second));
                NativeChoreDispatch.DispatchCaptured(default, blob, nested => Assert.IsFalse(NativeChoreDispatch.Current.Valid));
                Assert.IsTrue(NativeChoreDispatch.Matches(NativeChoreDispatch.Current, 42, true, first));
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeChoreDispatch.DispatchCaptured(inner, blob, nested => {
                    Assert.IsTrue(NativeChoreDispatch.Matches(NativeChoreDispatch.Current, 43, true, second));
                    Assert.IsFalse(NativeChoreDispatch.Matches(outer, 42, true, second));
                    throw new InvalidOperationException("nested dispatcher failure");
                }));
                Assert.IsTrue(NativeChoreDispatch.Matches(NativeChoreDispatch.Current, 42, true, first));
                bool inherited = true;
                var otherThread = new System.Threading.Thread(() => inherited = NativeChoreDispatch.Current.Valid);
                otherThread.Start(); otherThread.Join(); Assert.IsFalse(inherited);
            });
            Assert.IsFalse(NativeChoreDispatch.Current.Valid);
            Assert.IsFalse(NativeChoreDispatch.Matches(outer, 42, false, first));
            Action<byte[]> noOp = bytes => { };
            for (int i = 0; i < 32; i++) NativeChoreDispatch.DispatchCaptured(outer, blob, noOp);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) NativeChoreDispatch.DispatchCaptured(outer, blob, noOp);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [TestMethod]
        public void NativeDispatchBlobMustMatchTheCompletedNativePayloadExactly()
        {
            var blob = new byte[] { 42, 0, 17 };
            Assert.IsTrue(NativeChoreContext.MatchesPayloadFields(blob, 3, 7, 7, new byte[] { 42, 0, 17 }));
            Assert.IsFalse(NativeChoreContext.MatchesPayloadFields(blob, 3, 7, 7, new byte[] { 42, 0, 18 }));
            Assert.IsFalse(NativeChoreContext.MatchesPayloadFields(blob, 4, 8, 8, new byte[] { 42, 0, 17, 0 }));
            Assert.IsFalse(NativeChoreContext.MatchesPayloadFields(blob, 3, 6, 6, blob));
            Assert.IsFalse(NativeChoreContext.MatchesPayloadFields(blob, 3, 7, 6, blob));
            Assert.IsFalse(NativeChoreContext.MatchesPayloadFields(blob, 3, 7, 7, new byte[] { 42, 0 }));
            Assert.IsFalse(NativeChoreContext.MatchesPayloadFields(null, 0, 4, 4, Array.Empty<byte>()));
            Assert.IsFalse(NativeChoreContext.MatchesPayloadFields(new byte[1], 1, 5, 5, new byte[1]));
            var maximum = new byte[1200];
            Assert.IsTrue(NativeChoreContext.MatchesPayloadFields(maximum, 1200, 1204, 1204, maximum));
            var oversized = new byte[1201];
            Assert.IsFalse(NativeChoreContext.MatchesPayloadFields(oversized, 1201, 1205, 1205, oversized));
        }
    }
}
