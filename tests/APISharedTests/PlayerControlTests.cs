using System;
using APIShared.PlayerControl;
using APIShared.Networking;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class PlayerControlTests
    {
        [TestMethod]
        public void GrantsRequireActualHostAndIssuerAndCannotSurviveRevocationOrSessionChange()
        {
            var table = new ControlGrantTable();
            const string foreign = "third.party.mod/control";
            const string other = "independent.mod/control";
            Assert.IsFalse(table.Allows(1, foreign, 1, 2, 3, PlayerControlRights.UnitOrders));
            table.Reset(10, 1);
            Assert.IsFalse(table.Apply(10, 2, foreign, 2, 1 << 3, PlayerControlRights.All, 1));
            Assert.IsTrue(table.Apply(10, 1, foreign, 2, (1 << 2) | (1 << 3), PlayerControlRights.UnitOrders, 1));
            Assert.IsTrue(table.Allows(10, foreign, 1, 2, 2, PlayerControlRights.UnitOrders));
            Assert.IsTrue(table.Allows(10, foreign, 1, 2, 3, PlayerControlRights.UnitOrders));
            Assert.IsFalse(table.Allows(10, foreign, 1, 3, 3, PlayerControlRights.UnitOrders));
            Assert.IsTrue(table.Apply(10, 1, foreign, 4, 1 << 5, PlayerControlRights.UnitOrders, 1));
            Assert.IsTrue(table.Allows(10, foreign, 1, 4, 5, PlayerControlRights.UnitOrders));
            Assert.IsTrue(table.Allows(10, foreign, 1, 2, 3, PlayerControlRights.UnitOrders));
            Assert.IsFalse(table.Allows(10, foreign, 1, 2, 3, PlayerControlRights.UnitOrders | PlayerControlRights.DestructiveActions));
            Assert.IsFalse(table.Allows(10, other, 1, 2, 3, PlayerControlRights.UnitOrders));
            Assert.IsTrue(table.Apply(10, 1, other, 4, 1 << 5, PlayerControlRights.BuildingActions, 1));
            Assert.IsTrue(table.Allows(10, other, 1, 4, 5, PlayerControlRights.BuildingActions));
            Assert.IsTrue(table.Apply(10, 1, foreign, 2, 0, PlayerControlRights.None, 2));
            Assert.IsFalse(table.Apply(10, 1, foreign, 2, 1 << 3, PlayerControlRights.All, 1));
            Assert.IsFalse(table.Allows(10, foreign, 1, 2, 3, PlayerControlRights.UnitOrders));
            table.Reset(11, 1);
            Assert.IsFalse(table.Allows(10, other, 1, 4, 5, PlayerControlRights.BuildingActions));
            Assert.IsFalse(table.Allows(11, other, 1, 4, 5, PlayerControlRights.BuildingActions));
            Assert.IsFalse(table.Apply(10, 1, foreign, 2, 1 << 3, PlayerControlRights.All, 3));
            Assert.IsFalse(table.Apply(11, 1, foreign, 2, 1 << 9, PlayerControlRights.All, 3));
            Assert.IsFalse(table.Apply(11, 1, foreign, 0, 1 << 3, PlayerControlRights.All, 3));
            table.Reset(12, 0, false);
            Assert.IsTrue(table.Apply(12, 0, foreign, 0, 1 << 3, PlayerControlRights.All, 1));
            Assert.IsTrue(table.Allows(12, foreign, 1, 0, 3, PlayerControlRights.BuildingActions));
        }

        [TestMethod]
        public void IdentityIncludesOwnerGlobalIdAndTableAndRejectsInvalidSlots()
        {
            var unit = new PlayerControlObject(PlayerControlObjectKind.Unit, 7, 100, 2);
            Assert.AreEqual(unit, new PlayerControlObject(PlayerControlObjectKind.Unit, 7, 100, 2));
            Assert.AreNotEqual(unit, new PlayerControlObject(PlayerControlObjectKind.Unit, 7, 101, 2));
            Assert.AreNotEqual(unit, new PlayerControlObject(PlayerControlObjectKind.Unit, 7, 100, 3));
            Assert.AreNotEqual(unit, new PlayerControlObject(PlayerControlObjectKind.Building, 7, 100, 2));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PlayerControlObject(PlayerControlObjectKind.Unit, 0, 1, 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PlayerControlObject(PlayerControlObjectKind.Unit, 1, 0, 1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PlayerControlObject(PlayerControlObjectKind.Unit, 1, 1, 9));
        }

        [TestMethod]
        public void MultipartCommandsCompleteOnceWithoutPartialExecutionAndCopyTheirInputs()
        {
            var assembler = new ControlBatchAssembler(); assembler.Reset(9);
            var first = new byte[1024]; first[0] = 42;
            var last = new byte[] { 8, 9 };
            Assert.IsTrue(assembler.Receive(9, "independent.mod", 2, 1, 3, 1, 2, 1026, last, 100, out var result));
            Assert.IsNull(result); last[0] = 99;
            Assert.IsTrue(assembler.Receive(9, "independent.mod", 2, 1, 3, 0, 2, 1026, first, 102, out result));
            Assert.AreEqual(1026, result.Length); Assert.AreEqual(42, result[0]); Assert.AreEqual(8, result[1024]);
            Assert.AreEqual(0, assembler.BufferedBytes); Assert.AreEqual(0, assembler.PendingCount);
            Assert.IsFalse(assembler.Receive(9, "independent.mod", 2, 1, 3, 0, 2, 1026, first, 103, out result));
            Assert.IsNull(result);
            Assert.IsFalse(assembler.Receive(8, "independent.mod", 2, 2, 3, 0, 2, 1026, first, 103, out result));
            Assert.IsFalse(assembler.Receive(9, "independent.mod", 9, 2, 3, 0, 2, 1026, first, 103, out result));
        }

        [TestMethod]
        public void CorruptOrExpiredBatchesCannotBeRevivedAndIssuersHaveIndependentStreams()
        {
            var assembler = new ControlBatchAssembler(); assembler.Reset(9);
            var first = new byte[1024];
            Assert.IsTrue(assembler.Receive(9, "mod", 2, 1, 1, 0, 2, 1025, first, 10, out _));
            Assert.IsTrue(assembler.Receive(9, "mod", 3, 1, 1, 0, 2, 1025, first, 10, out _));
            Assert.IsFalse(assembler.Receive(9, "mod", 2, 1, 2, 1, 2, 1025, new byte[] { 1 }, 11, out _));
            Assert.AreEqual(1, assembler.PendingCount);
            Assert.IsFalse(assembler.Receive(9, "mod", 2, 1, 1, 1, 2, 1025, new byte[] { 1 }, 11, out _));
            assembler.Expire(20, 10);
            Assert.AreEqual(0, assembler.PendingCount); Assert.AreEqual(0, assembler.BufferedBytes);
            Assert.IsFalse(assembler.Receive(9, "mod", 3, 1, 1, 1, 2, 1025, new byte[] { 1 }, 21, out _));
            assembler.Reset(10);
            Assert.IsTrue(assembler.Receive(10, "mod", 3, 1, 1, 0, 1, 1, new byte[] { 7 }, 0, out var complete));
            CollectionAssert.AreEqual(new byte[] { 7 }, complete);
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
            bool hasNative = false, changeDuringValidation = false;
            int captures = 0, enabledCalls = 0, validations = 0, authorizations = 0, executions = 0, reports = 0;
            CaptureAuthenticatedChore capture = (short id, out NativeChoreExecution value) => {
                Assert.AreEqual(packetId, id); captures++; value = native; return hasNative;
            };
            Func<bool> enabled = () => { enabledCalls++; return true; };
            Func<object, bool> validate = value => { Assert.AreSame(packet, value); validations++; return true; };
            Func<AuthenticatedChoreMessage<object>, bool> authorize = message => {
                Assert.AreEqual(2, message.SenderPlayerId); authorizations++;
                if (changeDuringValidation) native = new NativeChoreExecution(3, 300000001, 100, 0x1000, 2);
                return true;
            };
            Action<AuthenticatedChoreMessage<object>> execute = message => executions++;
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
            changeDuringValidation = true;
            receive(new SHCDESE.EventAPI.Network.ReceiveCustomPacketEventArgs<object>(SHCDESE.EventAPI.EventHookPhase.Post, packetId, packet));
            Assert.AreEqual(5, captures); Assert.AreEqual(2, enabledCalls); Assert.AreEqual(2, validations);
            Assert.AreEqual(2, authorizations); Assert.AreEqual(1, executions); Assert.AreEqual(4, reports);
        }

        [TestMethod]
        public void ControlWireRoundTripsLargeSelectionsAndRejectsTruncationVersionsAndTrailingBytes()
        {
            var objects = new PlayerControlObject[9999];
            for (int i = 0; i < objects.Length; i++)
                objects[i] = new PlayerControlObject(PlayerControlObjectKind.Unit, i + 1, (uint)(i + 100), 2 + i % 2);
            var body = new byte[] { 1, 2, 3 };
            var source = new ControlCommandBatch(objects, new[] { new ControlNativeCommand(2, 17, body) });
            body[0] = 99; objects[0] = default;
            byte[] encoded = source.Encode();
            Assert.IsTrue(ControlCommandBatch.TryDecode(encoded, out var decoded));
            Assert.AreEqual(9999, decoded.Objects.Count);
            Assert.AreEqual(100u, decoded.Objects[0].GlobalId);
            Assert.AreEqual(1, decoded.Commands[0].Read(0));
            var copy = decoded.Commands[0].CopyPayload(); copy[0] = 99;
            Assert.AreEqual(1, decoded.Commands[0].Read(0));
            for (int size = 0; size < 28; size++)
            {
                var truncated = new byte[size]; Array.Copy(encoded, truncated, size);
                Assert.IsFalse(ControlCommandBatch.TryDecode(truncated, out _));
            }
            var trailing = new byte[encoded.Length + 1]; encoded.CopyTo(trailing, 0);
            Assert.IsFalse(ControlCommandBatch.TryDecode(trailing, out _));
            encoded[4] = 2;
            Assert.IsFalse(ControlCommandBatch.TryDecode(encoded, out _));
            encoded[4] = 1; encoded[8] = 0; encoded[9] = 0;
            Assert.IsFalse(ControlCommandBatch.TryDecode(encoded, out _));
        }

        private sealed class CommandRules : IControlCommandRules
        {
            internal Action DuringValidation;
            public bool TryValidate(ControlNativeCommand command, System.Collections.ObjectModel.ReadOnlyCollection<PlayerControlObject> objects,
                out PlayerControlRights required)
            {
                DuringValidation?.Invoke();
                required = command.Opcode == 1 ? PlayerControlRights.UnitOrders :
                    command.Opcode == 2 ? PlayerControlRights.UnitOrders | PlayerControlRights.DestructiveActions : PlayerControlRights.None;
                return required != PlayerControlRights.None;
            }
        }

        [TestMethod]
        public void WholeBatchAdmissionChecksIssuerRightsAndAllLiveIdentitiesBeforeAnyOwnerExecutes()
        {
            var table = new ControlGrantTable(); table.Reset(7, 1);
            Assert.IsTrue(table.Apply(7, 1, "mod/control", 2, (1 << 2) | (1 << 3), PlayerControlRights.UnitOrders, 1));
            var own = new PlayerControlObject(PlayerControlObjectKind.Unit, 7, 100, 2);
            var foreign = new PlayerControlObject(PlayerControlObjectKind.Unit, 8, 101, 3);
            var live = new Dictionary<int, PlayerControlObject> { [7] = own, [8] = foreign };
            var rules = new CommandRules();
            var gate = new ControlExecutionGate(table, rules,
                (PlayerControlObject captured, out PlayerControlObject current) => live.TryGetValue(captured.Slot, out current),
                session => session == 7);
            var commands = new[] { new ControlNativeCommand(3, 1, Array.Empty<byte>()), new ControlNativeCommand(2, 1, Array.Empty<byte>()) };
            var batch = new ControlCommandBatch(new[] { own, foreign }, commands);
            int calls = 0;
            Action<ControlExecutionPlan> execute = plan => {
                calls++;
                Assert.AreEqual(2, plan.Issuer); Assert.AreEqual(2, plan.Groups.Count);
                Assert.AreEqual(2, plan.Groups[0].Owner); Assert.AreEqual(3, plan.Groups[1].Owner);
            };
            Assert.IsTrue(gate.TryExecute(7, "mod/control", 1, 2, batch, execute, out _));
            Assert.AreEqual(1, calls);
            Assert.IsFalse(gate.TryExecute(7, "mod/control", 1, 3, batch, execute, out _));
            Assert.IsFalse(gate.TryExecute(7, "other/control", 1, 2, batch, execute, out _));
            live[8] = new PlayerControlObject(PlayerControlObjectKind.Unit, 8, 102, 3);
            Assert.IsFalse(gate.TryExecute(7, "mod/control", 1, 2, batch, execute, out _));
            live[8] = new PlayerControlObject(PlayerControlObjectKind.Unit, 8, 101, 4);
            Assert.IsFalse(gate.TryExecute(7, "mod/control", 1, 2, batch, execute, out _));
            live[8] = foreign;
            var destructive = new ControlCommandBatch(new[] { own, foreign }, new[] { commands[0], new ControlNativeCommand(2, 2, Array.Empty<byte>()) });
            Assert.IsFalse(gate.TryExecute(7, "mod/control", 1, 2, destructive, execute, out _));
            var unknown = new ControlCommandBatch(new[] { own, foreign }, new[] { commands[0], new ControlNativeCommand(2, 255, Array.Empty<byte>()) });
            Assert.IsFalse(gate.TryExecute(7, "mod/control", 1, 2, unknown, execute, out _));
            Assert.AreEqual(1, calls);
            Assert.IsTrue(table.Apply(7, 1, "mod/control", 2, (1 << 2) | (1 << 3), PlayerControlRights.All, 2));
            Assert.IsFalse(gate.TryExecute(7, "mod/control", 1, 2, destructive, execute, out _));
            Assert.IsTrue(gate.TryExecute(7, "mod/control", 2, 2, destructive, execute, out _));
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public void ExecutionAdmissionRejectsReentrantRevocationAndReleasesItsRecursionGuardAfterFailure()
        {
            var table = new ControlGrantTable(); table.Reset(7, 1);
            Assert.IsTrue(table.Apply(7, 1, "mod", 2, 1 << 3, PlayerControlRights.All, 1));
            var rules = new CommandRules();
            var gate = new ControlExecutionGate(table, rules,
                (PlayerControlObject captured, out PlayerControlObject live) => { live = captured; return true; }, session => session == 7);
            var batch = new ControlCommandBatch(Array.Empty<PlayerControlObject>(), new[] { new ControlNativeCommand(3, 1, Array.Empty<byte>()) });
            int calls = 0;
            rules.DuringValidation = () => table.Apply(7, 1, "mod", 2, 0, PlayerControlRights.None, 2);
            Assert.IsFalse(gate.TryExecute(7, "mod", 1, 2, batch, _ => calls++, out _));
            Assert.AreEqual(0, calls);
            rules.DuringValidation = null;
            Assert.IsTrue(table.Apply(7, 1, "mod", 2, 1 << 3, PlayerControlRights.All, 3));
            Assert.ThrowsExactly<InvalidOperationException>(() => gate.TryExecute(7, "mod", 3, 2, batch,
                _ => throw new InvalidOperationException("backend failure"), out _));
            Assert.IsTrue(gate.TryExecute(7, "mod", 3, 2, batch, plan => {
                calls++;
                Assert.IsFalse(gate.TryExecute(7, "mod", 3, 2, batch, __ => calls++, out _));
            }, out _));
            Assert.AreEqual(1, calls);
            var duplicate = new ControlCommandBatch(new[] {
                new PlayerControlObject(PlayerControlObjectKind.Unit, 1, 10, 3),
                new PlayerControlObject(PlayerControlObjectKind.Unit, 1, 11, 4)
            }, batch.Commands);
            Assert.IsFalse(gate.TryExecute(7, "mod", 3, 2, duplicate, _ => calls++, out _));
        }

        [TestMethod]
        public void StrictChoreSubmissionSerializesOnceAndNeverFallsBackOrRetries()
        {
            var packet = new object(); int serializations = 0, queues = 0;
            byte[] body = { 7, 8, 9 };
            Func<object, byte[]> serialize = value => { Assert.AreSame(packet, value); serializations++; return body; };
            var result = StrictChoreSendPolicy.Send(packet, 0x1234, true, serialize, blob => {
                queues++; CollectionAssert.AreEqual(new byte[] { 0x34, 0x12, 7, 8, 9 }, blob); return true;
            }, 1200);
            Assert.IsTrue(result.Submitted); Assert.AreEqual(1, serializations); Assert.AreEqual(1, queues);
            body[0] = 99;
            Assert.AreEqual(7, result.CopySerializedBody()[0]);
            result = StrictChoreSendPolicy.Send(packet, 1, true, serialize, blob => { queues++; return false; }, 1200);
            Assert.AreEqual(NetworkSendStatus.Unavailable, result.Status);
            Assert.AreEqual(2, serializations); Assert.AreEqual(2, queues);
            result = StrictChoreSendPolicy.Send(packet, 1, true, serialize, blob => { queues++; throw new InvalidOperationException("queue"); }, 1200);
            Assert.AreEqual(NetworkSendStatus.TransportFailed, result.Status);
            Assert.AreEqual(3, serializations); Assert.AreEqual(3, queues);
            Assert.AreEqual(NetworkSendStatus.Unavailable,
                StrictChoreSendPolicy.Send(packet, 1, false, serialize, blob => { queues++; return true; }, 1200).Status);
            Assert.AreEqual(NetworkSendStatus.TooLarge,
                StrictChoreSendPolicy.Send(packet, 1, true, value => new byte[1199], blob => { queues++; return true; }, 1200).Status);
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
            var foreign = typeof(PlayerControlTests).GetMethod(nameof(SubmissionWithUnknownHelper), flags);
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

        [TestMethod]
        public void MultipartExpiryUsesTheNativeTickHalfRangeIncludingSignedWrap()
        {
            var assembler = new ControlBatchAssembler(); assembler.Reset(1);
            Assert.IsTrue(assembler.Receive(1, "mod", 2, 1, 1, 0, 2, 1025, new byte[1024], int.MaxValue - 2, out _));
            assembler.Expire(int.MinValue + 1, 5);
            Assert.AreEqual(1, assembler.PendingCount);
            assembler.Expire(int.MinValue + 2, 5);
            Assert.AreEqual(0, assembler.PendingCount);
            Assert.IsTrue(assembler.Receive(1, "mod", 2, 2, 1, 0, 2, 1025, new byte[1024], -100, out _));
            assembler.Expire(-101, 5);
            Assert.AreEqual(1, assembler.PendingCount);
            assembler.Expire(-95, 5);
            Assert.AreEqual(0, assembler.PendingCount);
        }

        [TestMethod]
        public void MissingOrIncompatiblePeerAgreementPreventsControlForTheEntireEpoch()
        {
            var agreement = new ControlProtocolAgreement();
            var digest = new byte[32]; digest[0] = 7;
            const int peers = (1 << 1) | (1 << 3);
            agreement.Reset(1, peers, digest); digest[0] = 99;
            var correct = new byte[32]; correct[0] = 7;
            Assert.IsFalse(agreement.IsReady(1));
            Assert.IsTrue(agreement.Acknowledge(1, 1, correct));
            Assert.IsFalse(agreement.IsReady(1));
            Assert.IsFalse(agreement.Acknowledge(1, 2, correct));
            Assert.IsFalse(agreement.Acknowledge(2, 3, correct));
            Assert.IsFalse(agreement.Acknowledge(1, 3, new byte[32]));
            Assert.IsFalse(agreement.Acknowledge(1, 3, correct));
            Assert.IsFalse(agreement.IsReady(1));
            agreement.Reset(2, peers, correct);
            Assert.IsTrue(agreement.Acknowledge(2, 1, correct));
            Assert.IsTrue(agreement.Acknowledge(2, 3, correct));
            Assert.IsTrue(agreement.IsReady(2));
            agreement.Clear(); Assert.IsFalse(agreement.IsReady(2));
        }

        [TestMethod]
        public void CompleteBatchesNeedAllPeerConfirmationsAndOnlyTheRealHostCanCommitOnce()
        {
            var agreement = new ControlProtocolAgreement();
            const int peers = (1 << 1) | (1 << 3);
            var protocol = new byte[32]; protocol[0] = 5;
            agreement.Reset(1, peers, protocol);
            var host = new ControlCommitBarrier(agreement); host.Reset(1, 1, peers);
            var client = new ControlCommitBarrier(agreement); client.Reset(1, 1, peers);
            var batch = new ControlCommandBatch(Array.Empty<PlayerControlObject>(), new[] { new ControlNativeCommand(2, 1, new byte[] { 1 }) });
            byte[] digest = batch.ComputeDigest();
            var key = new ControlTransactionKey("unrelated.mod/control", 3, 1);
            Assert.IsFalse(host.RegisterComplete(1, key, 2, batch, digest, 10));
            agreement.Acknowledge(1, 1, protocol); agreement.Acknowledge(1, 3, protocol);
            Assert.IsFalse(host.RegisterComplete(1, key, 2, batch, protocol, 10));
            Assert.IsTrue(host.RegisterComplete(1, key, 2, batch, digest, 10));
            Assert.IsTrue(client.RegisterComplete(1, key, 2, batch, digest, 10));
            Assert.IsFalse(host.CanCommit(1, key, 2, digest));
            Assert.IsTrue(host.RecordReady(1, key, 2, 1, digest));
            Assert.IsFalse(host.CanCommit(1, key, 2, digest));
            Assert.IsFalse(host.RecordReady(1, key, 2, 2, digest));
            Assert.IsFalse(host.RecordReady(1, key, 3, 3, digest));
            Assert.IsFalse(host.RecordReady(1, key, 2, 3, protocol));
            Assert.IsTrue(host.RecordReady(1, key, 2, 3, digest));
            Assert.IsTrue(host.CanCommit(1, key, 2, digest));
            Assert.IsFalse(client.TryTakeCommit(1, key, 2, 3, digest, out _));
            // Cross-sender Ready packets need not have reached the client in the same
            // order as the host. The host's native-authenticated commit is authoritative.
            Assert.IsTrue(client.TryTakeCommit(1, key, 2, 1, digest, out var committed));
            Assert.AreSame(batch, committed);
            Assert.IsTrue(host.TryTakeCommit(1, key, 2, 1, digest, out _));
            Assert.IsFalse(client.TryTakeCommit(1, key, 2, 1, digest, out _));
            Assert.IsFalse(client.RegisterComplete(1, key, 2, batch, digest, 11));
            var next = new ControlTransactionKey(key.Registration, 3, 2);
            Assert.IsTrue(client.RegisterComplete(1, next, 3, batch, digest, 11));
            client.Revoke(key.Registration, 3);
            Assert.IsFalse(client.TryTakeCommit(1, next, 3, 1, digest, out _));
            Assert.IsFalse(client.RegisterComplete(1, next, 3, batch, digest, 11));
        }

        [TestMethod]
        public void ComposedProtocolAuthenticatesFragmentsAndCommitSeparatelyAndRechecksOwnersAtExecution()
        {
            var live = new Dictionary<int, PlayerControlObject>();
            var objects = new PlayerControlObject[150];
            for (int i = 0; i < objects.Length; i++)
            {
                objects[i] = new PlayerControlObject(PlayerControlObjectKind.Unit, i + 1, (uint)(700 + i), i % 2 == 0 ? 2 : 4);
                live[i + 1] = objects[i];
            }
            ResolveControlObject resolve = (PlayerControlObject captured, out PlayerControlObject current) => live.TryGetValue(captured.Slot, out current);
            var host = new ControlProtocolSession(new byte[32], new CommandRules(), resolve);
            var client = new ControlProtocolSession(new byte[32], new CommandRules(), resolve);
            Assert.IsTrue(host.Register("a/b", "c", out var main));
            Assert.IsTrue(host.Register("a", "b/c", out var other));
            Assert.AreNotEqual(main, other);
            Assert.IsTrue(client.Register("a", "b/c", out _));
            Assert.IsTrue(client.Register("a/b", "c", out var clientMain));
            Assert.AreEqual(main, clientMain);
            const int participants = (1 << 1) | (1 << 3);
            host.Begin(10, 1, participants); client.Begin(10, 1, participants);
            CollectionAssert.AreEqual(host.CopyFingerprint(), client.CopyFingerprint());
            Assert.IsFalse(host.Register("late", "mod", out _));
            foreach (var session in new[] { host, client })
            {
                Assert.IsFalse(session.ApplyGrant(10, 1, main, 3, (1 << 2) | (1 << 4), PlayerControlRights.UnitOrders, 1));
                Assert.IsTrue(session.Acknowledge(10, 1, host.CopyFingerprint()));
                Assert.IsTrue(session.Acknowledge(10, 3, client.CopyFingerprint()));
                Assert.IsFalse(session.ApplyGrant(10, 3, main, 3, (1 << 2) | (1 << 4), PlayerControlRights.UnitOrders, 1));
                Assert.IsTrue(session.ApplyGrant(10, 1, main, 3, (1 << 2) | (1 << 4), PlayerControlRights.UnitOrders, 1));
            }
            var batch = new ControlCommandBatch(objects, new[] {
                new ControlNativeCommand(4, 1, new byte[] { 1 }), new ControlNativeCommand(2, 1, new byte[] { 2 }) });
            byte[] encoded = batch.Encode();
            Assert.IsTrue(host.TryPreflight(10, main, 1, 3, batch, out _));
            Assert.IsFalse(host.TryPreflight(10, main, 1, 1, batch, out _));
            Assert.IsFalse(host.ReceivePart(10, "unknown", 3, 1, 1, 0, 1, 1, new byte[] { 1 }, 100, out _));
            ControlReadyNotice ready = null;
            int parts = (encoded.Length + 1023) / 1024;
            foreach (var session in new[] { host, client })
            {
                for (int i = 0; i < parts; i++)
                {
                    var part = new byte[Math.Min(1024, encoded.Length - i * 1024)];
                    Buffer.BlockCopy(encoded, i * 1024, part, 0, part.Length);
                    Assert.IsFalse(session.ReceivePart(10, main, 1, 1, 1, i, parts, encoded.Length, part, 100, out _));
                    Assert.IsTrue(session.ReceivePart(10, main, 3, 1, 1, i, parts, encoded.Length, part, 100, out ready));
                    if (i + 1 < parts) Assert.IsNull(ready);
                }
                Assert.IsNotNull(ready);
                Assert.IsFalse(session.CanCommit(10, ready.Key, 1, ready.CopyDigest()));
                Assert.IsTrue(session.ReceiveReady(10, ready.Key, 1, 1, ready.CopyDigest()));
                Assert.IsTrue(session.ReceiveReady(10, ready.Key, 1, 3, ready.CopyDigest()));
                Assert.IsTrue(session.CanCommit(10, ready.Key, 1, ready.CopyDigest()));
            }
            int executions = 0;
            Action<ControlExecutionPlan> execute = plan => {
                executions++; Assert.AreEqual(3, plan.Issuer); Assert.AreEqual(2, plan.Groups.Count);
                Assert.AreEqual(2, plan.Groups[0].Owner); Assert.AreEqual(4, plan.Groups[1].Owner);
            };
            Assert.IsFalse(host.ReceiveCommit(10, ready.Key, 1, 3, ready.CopyDigest(), execute, out _));
            Assert.IsTrue(host.ReceiveCommit(10, ready.Key, 1, 1, ready.CopyDigest(), execute, out _));
            Assert.IsTrue(client.ReceiveCommit(10, ready.Key, 1, 1, ready.CopyDigest(), execute, out _));
            Assert.AreEqual(2, executions);
            Assert.IsFalse(client.ReceiveCommit(10, ready.Key, 1, 1, ready.CopyDigest(), execute, out _));

            var one = new ControlCommandBatch(new[] { objects[0] }, new[] { new ControlNativeCommand(2, 1, Array.Empty<byte>()) }).Encode();
            Assert.IsTrue(host.ReceivePart(10, main, 3, 2, 1, 0, 1, one.Length, one, 101, out ready));
            live[1] = new PlayerControlObject(PlayerControlObjectKind.Unit, 1, 999, 2);
            Assert.IsFalse(host.ReceiveCommit(10, ready.Key, 1, 1, ready.CopyDigest(), execute, out _));
            Assert.AreEqual(2, executions);
            live[1] = objects[0];
            Assert.IsTrue(host.ReceivePart(10, main, 3, 3, 1, 0, 1, one.Length, one, 102, out ready));
            Assert.IsTrue(host.ApplyGrant(10, 1, main, 3, 0, PlayerControlRights.None, 2));
            Assert.IsFalse(host.ReceiveCommit(10, ready.Key, 1, 1, ready.CopyDigest(), execute, out _));
            host.End(); Assert.IsFalse(host.IsReady(10));
            host.Begin(11, 1, participants); Assert.IsFalse(host.IsReady(11));
            Assert.IsFalse(host.ReceiveCommit(10, ready.Key, 1, 1, ready.CopyDigest(), execute, out _));
            Assert.AreEqual(2, executions);
        }

        [TestMethod]
        public void SingleplayerControlNeedsNoProtocolAndRejectsMixedOwnersAndAMultiplayerWorld()
        {
            bool localWorld = true;
            bool modeReaderFails = false;
            var session = new SinglePlayerControlSession(new CommandRules(),
                (PlayerControlObject captured, out PlayerControlObject live) => { live = captured; return true; },
                () => modeReaderFails ? throw new InvalidOperationException("world reader failed") : localWorld);
            Assert.IsTrue(session.Register("third.party", "test-control", out string registration));
            session.Begin(1, 0);
            Assert.IsTrue(session.IsReady(1));
            Assert.IsTrue(session.SetGrant(1, registration, 1 << 2, PlayerControlRights.All, 1));
            Assert.IsFalse(session.SetGrant(1, registration, (1 << 2) | (1 << 3), PlayerControlRights.All, 2));
            var batch = new ControlCommandBatch(Array.Empty<PlayerControlObject>(), new[] { new ControlNativeCommand(2, 1, Array.Empty<byte>()) });
            int executions = 0;
            Assert.IsTrue(session.TryExecute(1, registration, 1, batch,
                plan => { executions++; Assert.AreEqual(0, plan.Issuer); }, out _));
            localWorld = false;
            Assert.IsFalse(session.TryExecute(1, registration, 1, batch, plan => executions++, out _));
            Assert.IsFalse(session.SetGrant(1, registration, 1 << 2, PlayerControlRights.All, 2));
            localWorld = true;
            Assert.IsFalse(session.IsReady(1));
            Assert.IsFalse(session.SetGrant(1, registration, 1 << 2, PlayerControlRights.All, 2));
            Assert.IsFalse(session.TryExecute(1, registration, 1, batch, plan => executions++, out _));
            session.End(); Assert.IsFalse(session.TryExecute(1, registration, 2, batch, plan => executions++, out _));
            session.Begin(2, 0);
            Assert.IsTrue(session.SetGrant(2, registration, 1 << 2, PlayerControlRights.All, 1));
            Assert.IsTrue(session.TryExecute(2, registration, 1, batch, plan => executions++, out _));
            Assert.IsTrue(session.TryCapture(2, registration, 1, batch, out long retired, out _));
            localWorld = false;
            Assert.IsFalse(session.IsReady(2));
            Assert.AreEqual(0, session.PendingCount);
            localWorld = true;
            Assert.IsFalse(session.TryExecuteCaptured(2, retired, plan => executions++, out _));
            session.End(); session.Begin(3, 0);
            Assert.IsTrue(session.SetGrant(3, registration, 1 << 2, PlayerControlRights.All, 1));
            Assert.IsTrue(session.TryCapture(3, registration, 1, batch, out long readerFailure, out _));
            modeReaderFails = true;
            Assert.ThrowsExactly<InvalidOperationException>(() => session.IsReady(3));
            modeReaderFails = false;
            Assert.AreEqual(0, session.PendingCount);
            Assert.IsFalse(session.TryExecuteCaptured(3, readerFailure, plan => executions++, out _));
            Assert.AreEqual(2, executions);

            // A mode read can cross a lifecycle observer. Neither its return nor
            // its exception may revive the old epoch or clear a replacement.
            Action duringModeRead = null;
            var reentrant = new SinglePlayerControlSession(new CommandRules(),
                (PlayerControlObject captured, out PlayerControlObject live) => { live = captured; return true; },
                () => { var callback = duringModeRead; duringModeRead = null; callback?.Invoke(); return true; });
            Assert.IsTrue(reentrant.Register("third.party", "reentrant-control", out _));
            reentrant.Begin(1, 0);
            duringModeRead = () => { reentrant.End(); reentrant.Begin(2, 0); };
            Assert.IsFalse(reentrant.IsReady(1)); Assert.IsTrue(reentrant.IsReady(2));
            duringModeRead = () => {
                reentrant.End(); reentrant.Begin(3, 0);
                throw new InvalidOperationException("old mode reader failed after replacement");
            };
            Assert.ThrowsExactly<InvalidOperationException>(() => reentrant.IsReady(2));
            Assert.IsTrue(reentrant.IsReady(3));
        }

        [TestMethod]
        public void LocalPendingCommandsAreBoundedRevalidatedAndConsumedBeforeBackendFailures()
        {
            var unit = new PlayerControlObject(PlayerControlObjectKind.Unit, 1, 123, 2);
            var current = unit;
            var session = new SinglePlayerControlSession(new CommandRules(),
                (PlayerControlObject captured, out PlayerControlObject live) => { live = current; return true; }, () => true);
            Assert.IsTrue(session.Register("independent.one", "control", out var first));
            Assert.IsTrue(session.Register("independent.two", "control", out var second));
            session.Begin(1, 0);
            Assert.IsTrue(session.SetGrant(1, first, 1 << 2, PlayerControlRights.UnitOrders, 1));
            Assert.IsTrue(session.SetGrant(1, second, 1 << 2, PlayerControlRights.UnitOrders, 1));
            var batch = new ControlCommandBatch(new[] { unit }, new[] { new ControlNativeCommand(2, 1, Array.Empty<byte>()) });
            Assert.IsTrue(session.TryCapture(1, first, 1, batch, out long stale, out _));
            Assert.IsTrue(session.TryCapture(1, second, 1, batch, out long unrelated, out _));
            Assert.IsTrue(session.SetGrant(1, first, 0, PlayerControlRights.None, 2));
            Assert.AreEqual(1, session.PendingCount);
            int executions = 0;
            Assert.IsFalse(session.TryExecuteCaptured(1, stale, plan => executions++, out _));
            Assert.IsTrue(session.TryExecuteCaptured(1, unrelated, plan => executions++, out _));
            Assert.IsFalse(session.TryExecuteCaptured(1, unrelated, plan => executions++, out _));
            Assert.AreEqual(1, executions);

            Assert.IsTrue(session.TryCapture(1, second, 1, batch, out long recycledUnit, out _));
            current = new PlayerControlObject(PlayerControlObjectKind.Unit, 1, 124, 2);
            Assert.IsFalse(session.TryExecuteCaptured(1, recycledUnit, plan => executions++, out _));
            Assert.AreEqual(0, session.PendingCount);
            current = unit;
            Assert.IsTrue(session.TryCapture(1, second, 1, batch, out long throwing, out _));
            Assert.ThrowsExactly<InvalidOperationException>(() => session.TryExecuteCaptured(1, throwing, plan => {
                executions++;
                Assert.IsFalse(session.TryExecuteCaptured(1, throwing, nested => executions++, out _));
                throw new InvalidOperationException("backend failed after acceptance");
            }, out _));
            Assert.IsFalse(session.TryExecuteCaptured(1, throwing, plan => executions++, out _));
            Assert.AreEqual(2, executions);

            for (int i = 0; i < SinglePlayerControlSession.MaximumPendingCommands; i++)
                Assert.IsTrue(session.TryCapture(1, second, 1, batch, out _, out _));
            Assert.IsFalse(session.TryCapture(1, second, 1, batch, out _, out _));
            Assert.AreEqual(SinglePlayerControlSession.MaximumPendingCommands, session.PendingCount);
            session.End();
            session.Begin(2, 0);
            Assert.IsTrue(session.SetGrant(2, second, 1 << 2, PlayerControlRights.UnitOrders, 1));
            Assert.IsTrue(session.TryCapture(2, second, 1, batch, out long nextMission, out _));
            Assert.IsTrue(nextMission > throwing);
            Assert.IsFalse(session.TryExecuteCaptured(1, nextMission, plan => executions++, out _));
            Assert.AreEqual(1, session.PendingCount);
            Assert.IsTrue(session.TryExecuteCaptured(2, nextMission, plan => executions++, out _));
            Assert.AreEqual(3, executions);
        }

        [TestMethod]
        public void LocalNativeQueueKeepsConsumedAndRevokedCommandsDeniedUntilActualSlotReuse()
        {
            bool localWorld = true;
            var session = new SinglePlayerControlSession(new CommandRules(),
                (PlayerControlObject captured, out PlayerControlObject live) => { live = captured; return true; }, () => localWorld);
            Assert.IsTrue(session.Register("independent.mod", "control", out string key));
            session.Begin(1, 0);
            Assert.IsTrue(session.SetGrant(1, key, 1 << 2, PlayerControlRights.UnitOrders, 1));
            var queue = new SinglePlayerNativeQueue(session); queue.Begin(1, 0x1000);
            byte[] body = { 7, 8 };
            // Opcode 1 is a synthetic unit command interpreted only by CommandRules.
            var batch = new ControlCommandBatch(Array.Empty<PlayerControlObject>(), new[] { new ControlNativeCommand(2, 1, body) });
            var entry = new ControlNativeQueueEntry(1, 0x1000, 3, 200000001, 2, 1);
            int executed = 0, released = 0;
            Action<ControlExecutionPlan> execute = plan => { Assert.AreEqual(0, plan.Issuer); executed++; };
            Assert.IsTrue(queue.Capture(entry, body, key, 1, batch, () => released++, out _));
            Assert.AreEqual(ControlNativeQueueResult.Executed, queue.Run(entry, body, execute, out _));
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(entry, body, execute, out _));
            Assert.IsFalse(queue.Capture(entry, body, key, 1, batch, () => Assert.Fail("Duplicate reservation must not replace its owner."), out _));
            Assert.AreEqual(1, executed); Assert.AreEqual(1, released); Assert.AreEqual(0, session.PendingCount);

            var next = new ControlNativeQueueEntry(1, 0x1000, 3, 200000002, 2, 1);
            Assert.IsTrue(queue.Capture(next, body, key, 1, batch, () => released++, out _));
            Assert.IsTrue(session.SetGrant(1, key, 0, PlayerControlRights.None, 2));
            Assert.AreEqual(0, session.PendingCount);
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(next, body, execute, out _));
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(next, body, execute, out _));
            Assert.AreEqual(2, released); Assert.AreEqual(1, executed);

            // This is a different native command occupying the same real slot.
            var own = new ControlNativeQueueEntry(1, 0x1000, 3, 100000003, 1, 1);
            Assert.AreEqual(ControlNativeQueueResult.NotTracked, queue.Run(own, body, execute, out _));
            Assert.AreEqual(1, executed); Assert.AreEqual(2, released);
            queue.End(); session.End();
            Assert.ThrowsExactly<InvalidOperationException>(() => session.Begin(1, 0));
            session.Begin(2, 0);
            queue.Begin(2, 0x1000);
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(next, body, execute, out _));
            var untracked = new ControlNativeQueueEntry(2, 0x1000, 0, 100000004, 1, 1);
            localWorld = false;
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(untracked, body, execute, out _),
                "A lost SP world must not turn an untracked command into a Vanilla fallback.");
            localWorld = true;
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(untracked, body, execute, out _));
            queue.End(); session.End();
        }

        [TestMethod]
        public void LocalNativeQueueBindsPayloadAndOwnerAndNeverRetriesAfterBackendFailure()
        {
            var rules = new CommandRules();
            var session = new SinglePlayerControlSession(rules,
                (PlayerControlObject captured, out PlayerControlObject live) => { live = captured; return true; }, () => true);
            Assert.IsTrue(session.Register("independent.mod", "control", out string key));
            session.Begin(1, 0);
            Assert.IsTrue(session.SetGrant(1, key, 1 << 2, PlayerControlRights.UnitOrders, 1));
            var queue = new SinglePlayerNativeQueue(session); queue.Begin(1, 0x1000);
            var body = new byte[] { 1, 2 };
            var batch = new ControlCommandBatch(Array.Empty<PlayerControlObject>(), new[] { new ControlNativeCommand(2, 1, body) });
            var entry = new ControlNativeQueueEntry(1, 0x1000, 0, 200000001, 2, 1);
            int executed = 0, released = 0;
            Action<ControlExecutionPlan> execute = _ => executed++;
            Assert.IsTrue(queue.Capture(entry, body, key, 1, batch, () => released++, out _));
            body[1] = 3;
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(entry, body, execute, out _));
            body[1] = 2;
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(entry, body, execute, out _));
            Assert.AreEqual(0, session.PendingCount); Assert.AreEqual(0, executed); Assert.AreEqual(1, released);

            var wrongOwner = new ControlNativeQueueEntry(1, 0x1000, 1, 300000001, 3, 1);
            Assert.IsFalse(queue.Capture(wrongOwner, body, key, 1, batch, () => released++, out _));
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(wrongOwner, body, execute, out _));
            var differentBody = new ControlNativeQueueEntry(1, 0x1000, 2, 200000002, 2, 1);
            Assert.IsFalse(queue.Capture(differentBody, new byte[] { 9, 9 }, key, 1, batch, () => released++, out _));
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(differentBody, body, execute, out _));
            Assert.AreEqual(3, released);

            var throwing = new ControlNativeQueueEntry(1, 0x1000, 0, 200000003, 2, 1);
            Assert.IsTrue(queue.Capture(throwing, body, key, 1, batch, () => released++, out _));
            Assert.ThrowsExactly<InvalidOperationException>(() => queue.Run(throwing, body, plan => {
                executed++;
                Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(throwing, body, execute, out _));
                throw new InvalidOperationException("native backend failed after acceptance");
            }, out _));
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(throwing, body, execute, out _));
            Assert.AreEqual(1, executed); Assert.AreEqual(4, released);
            Assert.AreEqual(0, session.PendingCount);

            var replaced = new ControlNativeQueueEntry(1, 0x1000, 4, 200000004, 2, 1);
            rules.DuringValidation = queue.End;
            Assert.IsFalse(queue.Capture(replaced, body, key, 1, batch, () => released++, out _));
            Assert.AreEqual(0, session.PendingCount); Assert.AreEqual(5, released);
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(replaced, body, execute, out _));
            rules.DuringValidation = null;
            Assert.ThrowsExactly<InvalidOperationException>(() => queue.Begin(1, 0x1000));
            session.End(); session.Begin(2, 0); queue.Begin(2, 0x2000);
            Assert.IsTrue(session.SetGrant(2, key, 1 << 2, PlayerControlRights.UnitOrders, 1));
            var current = new ControlNativeQueueEntry(2, 0x2000, 4, 200000004, 2, 1);
            Assert.IsTrue(queue.Capture(current, body, key, 1, batch, () => released++, out _));
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(replaced, body, execute, out _));
            Assert.AreEqual(ControlNativeQueueResult.Executed, queue.Run(current, body, execute, out _));
            Assert.AreEqual(2, executed); Assert.AreEqual(6, released);
            var cleanupFailure = new ControlNativeQueueEntry(2, 0x2000, 5, 200000005, 2, 1);
            Assert.IsTrue(queue.Capture(cleanupFailure, body, key, 1, batch,
                () => throw new InvalidOperationException("reservation release failed"), out _));
            Assert.AreEqual(ControlNativeQueueResult.Executed, queue.Run(cleanupFailure, body, execute, out _));
            Assert.IsNotNull(queue.Failure);
            var untrackedAfterFailure = new ControlNativeQueueEntry(2, 0x2000, 9, 100000009, 1, 1);
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(untrackedAfterFailure, body, execute, out _));
            var blocked = new ControlNativeQueueEntry(2, 0x2000, 6, 200000006, 2, 1);
            Assert.IsFalse(queue.Capture(blocked, body, key, 1, batch, () => released++, out _));
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(blocked, body, execute, out _));
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(cleanupFailure, body, execute, out _));
            Assert.AreEqual(3, executed); Assert.AreEqual(7, released); Assert.AreEqual(0, session.PendingCount);
            queue.End(); session.End();
            session.Begin(3, 0); queue.Begin(3, 0x3000);
            Assert.IsNull(queue.Failure);
            Assert.IsTrue(session.SetGrant(3, key, 1 << 2, PlayerControlRights.UnitOrders, 1));
            var retiring = new ControlNativeQueueEntry(3, 0x3000, 7, 200000007, 2, 1);
            Assert.IsTrue(queue.Capture(retiring, body, key, 1, batch, () => {
                session.End(); session.Begin(4, 0);
                Assert.ThrowsExactly<InvalidOperationException>(() => queue.Begin(4, 0x4000));
                queue.End(); // A recursive retirement must not open another bind window.
            }, out _));
            queue.End();
            queue.Begin(4, 0x4000);
            Assert.IsTrue(session.SetGrant(4, key, 1 << 2, PlayerControlRights.UnitOrders, 1));
            var replacement = new ControlNativeQueueEntry(4, 0x4000, 7, 200000007, 2, 1);
            Assert.IsTrue(queue.Capture(replacement, body, key, 1, batch, null, out _));
            Assert.AreEqual(ControlNativeQueueResult.Executed, queue.Run(replacement, body, execute, out _));
            Assert.AreEqual(4, executed);
            var cleanupReentry = new ControlNativeQueueEntry(4, 0x4000, 8, 200000008, 2, 1);
            var freshDuringCleanup = new ControlNativeQueueEntry(5, 0x5000, 8, 200000008, 2, 1);
            Assert.IsTrue(queue.Capture(cleanupReentry, body, key, 1, batch, () => {
                queue.End(); session.End(); session.Begin(5, 0); queue.Begin(5, 0x5000);
                Assert.IsTrue(session.SetGrant(5, key, 1 << 2, PlayerControlRights.UnitOrders, 1));
                Assert.IsTrue(queue.Capture(freshDuringCleanup, body, key, 1, batch, null, out _));
            }, out _));
            var reusedDuringCleanup = new ControlNativeQueueEntry(4, 0x4000, 8, 100000009, 1, 1);
            Assert.AreEqual(ControlNativeQueueResult.Rejected, queue.Run(reusedDuringCleanup, body, execute, out _),
                "Retirement may replace the world; it must not then permit Vanilla in the old context.");
            Assert.AreEqual(ControlNativeQueueResult.Executed, queue.Run(freshDuringCleanup, body, execute, out _));
            Assert.AreEqual(5, executed);
            queue.End(); session.End();
        }

        [TestMethod]
        public void ProtectedSaveUsesTheOriginalHumanIdentityAndRestoresViewsIncludingNestedAndFailedWriters()
        {
            bool inEngine = true;
            long epoch = 1;
            int nativeView = 1, writes = 0;
            var guard = new ControlSaveIdentity(() => inEngine, expected => epoch == expected,
                () => nativeView, view => { writes++; nativeView = view; });
            Assert.IsTrue(guard.TryBind(1, 1, out long token, out _));
            Assert.AreEqual(1, guard.ProtectedHumanId);
            nativeView = 3; // The separate view-lease adapter performs this transition.
            Assert.IsFalse(guard.TryRelease(token, out _));
            Assert.IsFalse(guard.TryBind(1, 1, out _, out _));
            int writers = 0;
            Assert.IsTrue(guard.TryRunWriter(17, value => {
                writers++; Assert.AreEqual(1, nativeView);
                Assert.AreEqual(1, guard.ProtectedHumanId);
                Assert.IsTrue(guard.TryRunWriter(value, nested => {
                    writers++; Assert.AreEqual(1, nativeView); return nested + 1;
                }, out int nestedResult, out _));
                Assert.AreEqual(18, nestedResult); Assert.AreEqual(1, nativeView);
                Assert.IsFalse(guard.TryBind(1, 1, out _, out _));
                return 0xFEDCBA9876543210UL;
            }, out ulong result, out _));
            Assert.AreEqual(0xFEDCBA9876543210UL, result);
            Assert.AreEqual(2, writers); Assert.AreEqual(3, nativeView);
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.TryRunWriter<int, int>(0, value => {
                Assert.AreEqual(1, nativeView);
                throw new InvalidOperationException("writer failure");
            }, out _, out _));
            Assert.AreEqual(3, nativeView);
            inEngine = false;
            int priorWrites = writes;
            Assert.IsFalse(guard.TryRunWriter(0, value => { writers++; return value; }, out _, out _));
            Assert.AreEqual(priorWrites, writes); Assert.AreEqual(2, writers);
            inEngine = true; nativeView = 1;
            Assert.IsTrue(guard.TryRelease(token, out _)); Assert.AreEqual(0, guard.ProtectedHumanId);

            // A normal, unleased save must not query an optional failed adapter.
            var dormant = new ControlSaveIdentity(() => throw new InvalidOperationException("unused monitor"),
                expected => throw new InvalidOperationException("unused mode"),
                () => throw new InvalidOperationException("unused native reader"),
                value => throw new InvalidOperationException("unused native writer"));
            Assert.IsTrue(dormant.TryRunWriter(9, value => value + 1, out int unchanged, out _));
            Assert.AreEqual(10, unchanged);
            Func<int, int> originalWriter = value => value + 1;
            for (int i = 0; i < 32; i++) dormant.TryRunWriter(i, originalWriter, out _, out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) dormant.TryRunWriter(i, originalWriter, out unchanged, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated, "Dormant saves allocate or touch an optional provider.");
            Assert.AreEqual(10000, unchanged);
        }

        [TestMethod]
        public void SaveIdentityDoesNotReviveReleasedOrReplacedWorldsAndRetainsProtectionAfterRestoreFailure()
        {
            long epoch = 1;
            int nativeView = 1;
            bool failForeignRestore = false;
            var guard = new ControlSaveIdentity(() => true, expected => expected == epoch,
                () => nativeView, view => {
                    if (view == 3 && failForeignRestore) throw new InvalidOperationException("restore unavailable");
                    nativeView = view;
                });
            Assert.IsTrue(guard.TryBind(1, 1, out long token, out _)); nativeView = 3;
            Assert.IsTrue(guard.TryRunWriter(0, value => {
                Assert.AreEqual(1, nativeView);
                Assert.IsTrue(guard.TryRelease(token, out _));
                Assert.IsFalse(guard.TryBind(1, 1, out _, out _));
                return value;
            }, out _, out _));
            Assert.AreEqual(1, nativeView); Assert.AreEqual(0, guard.ProtectedHumanId);

            Assert.IsTrue(guard.TryBind(1, 1, out token, out _)); nativeView = 3;
            Assert.IsTrue(guard.TryRunWriter(0, value => {
                epoch = 2; nativeView = 6; // Native load/mission replacement in an append callback.
                Assert.IsTrue(guard.RetireEndedWorld(1));
                return value;
            }, out _, out _));
            Assert.AreEqual(6, nativeView); Assert.AreEqual(0, guard.ProtectedHumanId);

            nativeView = 1;
            Assert.IsTrue(guard.TryBind(2, 1, out token, out _)); nativeView = 3;
            failForeignRestore = true;
            Assert.IsFalse(guard.TryRunWriter(7, value => value + 1, out int result, out string failure));
            Assert.AreEqual(8, result); Assert.IsFalse(string.IsNullOrWhiteSpace(failure));
            Assert.AreEqual(1, nativeView); Assert.AreEqual(1, guard.ProtectedHumanId);
            Assert.IsNotNull(guard.Failure);
            Assert.IsTrue(guard.TryRelease(token, out _));
            Assert.IsNull(guard.Failure); Assert.AreEqual(0, guard.ProtectedHumanId);
        }

        [TestMethod]
        public void SaveIdentityRechecksWorldAcrossNativeAdaptersAndSerializesLeaseTransitions()
        {
            // The adapter can cross managed observers. A load during a failing
            // setter must not roll an old view back into the replacement world.
            foreach (bool throwAfterReplacement in new[] { false, true })
            {
                long world = 1;
                int view = 1, writes = 0, writers = 0;
                bool replaceOnWrite = false;
                ControlSaveIdentity guard = null;
                guard = new ControlSaveIdentity(() => true, epoch => epoch == world, () => view, next => {
                    writes++;
                    if (replaceOnWrite)
                    {
                        world = 2; view = 6;
                        Assert.IsTrue(guard.RetireEndedWorld(1));
                        Assert.IsFalse(guard.TryBind(2, 6, out _, out _), "A save transition is still in progress.");
                        if (throwAfterReplacement) throw new InvalidOperationException("load during setter");
                    }
                    else view = next;
                });
                Assert.IsTrue(guard.TryBind(1, 1, out _, out _)); view = 3;
                replaceOnWrite = true;
                Assert.IsFalse(guard.TryRunWriter(0, value => { writers++; return value; }, out _, out _));
                Assert.AreEqual(0, writers); Assert.AreEqual(1, writes);
                Assert.AreEqual(6, view); Assert.AreEqual(0, guard.ProtectedHumanId);
                replaceOnWrite = false;
                Assert.IsTrue(guard.TryBind(2, 6, out _, out _), "The failed save must release its transition scope.");
            }

            long epochNow = 1;
            int currentView = 1, readWrites = 0;
            bool reenter = true, replaceOnRead = false;
            ControlSaveIdentity readerGuard = null;
            readerGuard = new ControlSaveIdentity(() => true, epoch => epoch == epochNow, () => {
                if (reenter)
                {
                    reenter = false;
                    Assert.IsFalse(readerGuard.TryBind(1, 1, out _, out _), "A recursive bind cannot replace its caller.");
                }
                int result = currentView;
                if (replaceOnRead) { epochNow = 2; currentView = 6; }
                return result;
            }, next => { readWrites++; currentView = next; });
            Assert.IsTrue(readerGuard.TryBind(1, 1, out long token, out _));
            replaceOnRead = true;
            Assert.IsFalse(readerGuard.TryRelease(token, out _), "A stale read cannot release an identity in a new world.");
            Assert.IsTrue(readerGuard.RetireEndedWorld(1));
            Assert.AreEqual(0, readWrites); Assert.AreEqual(6, currentView);
            epochNow = 1; currentView = 1;
            Assert.IsFalse(readerGuard.TryBind(1, 1, out _, out _), "A stale read cannot bind a replaced world.");
            Assert.AreEqual(0, readerGuard.ProtectedHumanId);
        }

        private sealed class Backend : IControlBackend
        {
            internal int Executions, AvailabilityReads;
            internal bool FailExecution, FailAvailability;
            public bool IsAvailable
            {
                get { AvailabilityReads++; if (FailAvailability) throw new InvalidOperationException("optional availability failure"); return true; }
            }
            public string UnavailableReason => null;
            public bool TryExecute(ControlExecutionPlan plan, out string reason)
            {
                Executions++; reason = null;
                if (FailExecution) throw new InvalidOperationException("optional execution failure");
                return true;
            }
        }

        [TestMethod]
        public void SingleplayerSingleOwnerControlSurvivesEveryOptionalBackendFailureWithoutNetworkFallback()
        {
            var router = new ControlBackendRouter();
            var single = new Backend();
            Assert.IsTrue(router.Initialize(ControlBackendKind.SingleplayerExclusive, () => single, out _));
            Assert.IsFalse(router.Initialize(ControlBackendKind.SingleplayerMixed,
                () => throw new InvalidOperationException("mixed selection initialization"), out _));
            var network = new Backend();
            Assert.IsTrue(router.Initialize(ControlBackendKind.MultiplayerExclusive, () => network, out _));
            var networkMixed = new Backend { FailAvailability = true };
            Assert.IsFalse(router.Initialize(ControlBackendKind.MultiplayerMixed, () => networkMixed, out _));
            var batch = new ControlCommandBatch(Array.Empty<PlayerControlObject>(), new[] { new ControlNativeCommand(2, 1, Array.Empty<byte>()) });
            var plan = new ControlExecutionPlan(1, 0, batch, new List<ControlOwnerCommands>());
            Assert.IsTrue(router.TryExecute(false, PlayerControlSelectionMode.ExclusivePlayer, plan, out _));
            int optionalReads = network.AvailabilityReads + networkMixed.AvailabilityReads;
            for (int i = 0; i < 20; i++)
                Assert.IsTrue(router.TryExecute(false, PlayerControlSelectionMode.ExclusivePlayer, plan, out _));
            Assert.AreEqual(optionalReads, network.AvailabilityReads + networkMixed.AvailabilityReads,
                "The local single-owner path queried an optional provider.");
            Assert.IsFalse(router.TryExecute(false, PlayerControlSelectionMode.AdditionalOwners, plan, out _));
            Assert.IsFalse(router.TryExecute(true, PlayerControlSelectionMode.AdditionalOwners, plan, out _));
            network.FailExecution = true;
            Assert.IsFalse(router.TryExecute(true, PlayerControlSelectionMode.ExclusivePlayer, plan, out _));
            Assert.IsFalse(router.TryExecute(true, PlayerControlSelectionMode.ExclusivePlayer, plan, out _));
            Assert.AreEqual(1, network.Executions, "Failed network execution was retried.");
            Assert.AreEqual(21, single.Executions, "A failed optional operation fell back to a local world edit.");
            Assert.IsTrue(router.TryExecute(false, PlayerControlSelectionMode.ExclusivePlayer, plan, out _));
            bool reinitialized = false;
            Assert.IsFalse(router.Initialize(ControlBackendKind.MultiplayerExclusive, () => { reinitialized = true; return new Backend(); }, out _));
            Assert.IsFalse(reinitialized, "Published failed hooks must not be reinstalled.");
            Assert.AreEqual(22, single.Executions);
        }
    }
}
