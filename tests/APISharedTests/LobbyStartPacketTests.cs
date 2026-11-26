using System;
using System.Linq;
using System.Reflection;
using APIShared.Internal;
using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class LobbyStartPacketTests
    {
        private static readonly ulong[] Humans = { 11, 22 };
        private static byte[] Remap(int saved, int current) => BitConverter.GetBytes(saved).Concat(BitConverter.GetBytes(current)).ToArray();

        [TestMethod]
        public void OnlyAuthenticatedRoleSpecificHandshakeBodiesAreOwnedAndOrdered()
        {
            var client = new LobbyStartPacketQueue();
            var bytes = new byte[] { 99, 1, 2, 3, 4, 88 };
            Assert.IsTrue(client.Add(2, bytes, 1, 4, 11, 22, 11, Humans));
            bytes[1] = 77;
            Assert.IsTrue(client.Add(4, Array.Empty<byte>(), 0, 0, 11, 22, 11, Humans));
            Assert.IsTrue(client.Add(4, new byte[36], 0, 36, 11, 22, 11, Humans));
            var first = client.Take();
            Assert.AreEqual((short)2, first.Type); Assert.AreEqual(11UL, first.Sender);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, first.Bytes);
            Assert.AreEqual((short)4, client.Take().Type); Assert.AreEqual(36, client.Take().Bytes.Length);
            Assert.IsNull(client.Take());
            var host = new LobbyStartPacketQueue();
            Assert.IsTrue(host.Add(9, Remap(8, 2), 0, 8, 22, 11, 11, Humans));
            Assert.IsTrue(host.Add(3, Array.Empty<byte>(), 0, 0, 22, 11, 11, Humans));
            Assert.AreEqual((short)9, host.Take().Type); Assert.AreEqual((short)3, host.Take().Type);
            foreach (short type in new short[] { 1, 5, 7, 1000, short.MaxValue })
                Assert.IsFalse(host.Add(type, new byte[8], 0, 8, 22, 11, 11, Humans));
            Assert.IsFalse(client.Add(2, new byte[4], 0, 4, 22, 22, 11, Humans), "Self packets are not remote handshake authority.");
            Assert.IsFalse(client.Add(2, new byte[4], 0, 4, 33, 22, 11, Humans), "A foreign Steam ID cannot seed the client.");
            Assert.IsFalse(client.Add(3, Array.Empty<byte>(), 0, 0, 11, 22, 11, Humans), "Acknowledgements only belong to the host.");
            Assert.IsFalse(host.Add(4, Array.Empty<byte>(), 0, 0, 22, 11, 11, Humans), "A client cannot start the host.");
            Assert.IsFalse(host.Add(9, Remap(1, 2), 0, 8, 0, 11, 11, Humans));
            Assert.IsFalse(host.Add(3, Array.Empty<byte>(), 0, 0, 22, 11, 11, null));
        }

        [TestMethod]
        public void FramingAndRemapBoundsRejectBeforeMutationAndOverflowPoisonsOnlyItsEpoch()
        {
            var queue = new LobbyStartPacketQueue(1);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LobbyStartPacketQueue(0));
            foreach (var pair in new[] { new[] { -1, 4 }, new[] { 0, -1 }, new[] { 5, 0 }, new[] { 0, 5 }, new[] { int.MaxValue, 4 } })
                Assert.IsFalse(queue.Add(2, new byte[4], pair[0], pair[1], 11, 22, 11, Humans));
            Assert.IsFalse(queue.Add(2, null, 0, 4, 11, 22, 11, Humans));
            foreach (int size in new[] { 0, 3, 5 }) Assert.IsFalse(queue.Add(2, new byte[size], 0, size, 11, 22, 11, Humans));
            foreach (int size in new[] { 1, 35, 37 }) Assert.IsFalse(queue.Add(4, new byte[size], 0, size, 11, 22, 11, Humans));
            foreach (int value in new[] { -1, 9, int.MaxValue })
            {
                Assert.IsFalse(queue.Add(9, Remap(value, 2), 0, 8, 22, 11, 11, Humans));
                Assert.IsFalse(queue.Add(9, Remap(1, value), 0, 8, 22, 11, 11, Humans));
            }
            Assert.AreEqual(0, queue.Count); Assert.IsFalse(queue.Poisoned);
            Assert.IsTrue(queue.Add(2, new byte[4], 0, 4, 11, 22, 11, Humans));
            Assert.IsFalse(queue.Add(1, new byte[4], 0, 4, 11, 22, 11, Humans));
            Assert.AreEqual(1, queue.Count); Assert.IsFalse(queue.Poisoned, "An unrelated rejected packet cannot poison a valid prefix.");
            Assert.IsFalse(queue.Add(4, Array.Empty<byte>(), 0, 0, 11, 22, 11, Humans));
            Assert.IsTrue(queue.Poisoned); Assert.AreEqual(0, queue.Count);
            Assert.IsFalse(queue.Add(2, new byte[4], 0, 4, 11, 22, 11, Humans)); Assert.IsNull(queue.Take());
            long old = queue.Generation; queue.Reset();
            Assert.IsTrue(queue.Generation > old); Assert.IsFalse(queue.Poisoned);
            Assert.IsTrue(queue.Add(2, new byte[4], 0, 4, 11, 22, 11, Humans));
            queue.Discard(old); Assert.AreEqual(1, queue.Count, "A delayed old cleanup cannot erase the replacement epoch.");
            queue.Discard(queue.Generation); Assert.AreEqual(0, queue.Count);
        }

        private static MethodInfo DrainMethod() => typeof(SHCDESE.API.GameNetworkAPI).Assembly
            .GetType("SHCDESE.ManagedHooks.ManagedHookManager", true).GetMethod("Platform_Multiplayer_ReceiveLobbyMessages_Hook",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Platform_Multiplayer) }, null);
        private static Instruction Call(ILContext il, string name) => il.Body.Instructions.Single(instruction =>
            instruction.Operand is MethodReference method && method.Name == name);
        private static Instruction Target(object operand) => operand is ILLabel label ? label.Target : operand as Instruction;

        [TestMethod]
        public void InstalledSeDrainAndVanillaPrologsTransformAndGenerateWithoutExecutingGameCode()
        {
            using (var definition = new DynamicMethodDefinition(DrainMethod()))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(il => {
                    var decode = Call(il, "FromBytes"); var store = decode.Next; var originalNext = store.Next;
                    Assert.IsTrue(store.MatchStloc(out int data));
                    Assert.IsTrue(Call(il, "GetSteamID").Next.MatchStloc(out int sender));
                    var lowBranch = originalNext.Next.Next.Next;
                    Assert.IsInstanceOfType<ILLabel>(lowBranch.Operand, "The real MonoMod normalized branch contract must be exercised.");
                    var originalLowTarget = Target(lowBranch.Operand);
                    LobbyStartPacketIl.Stage(il);
                    Assert.AreEqual(OpCodes.Ldarg_1, store.Next.OpCode);
                    Assert.IsTrue(store.Next.Next.MatchLdloc(data)); Assert.IsTrue(store.Next.Next.Next.MatchLdloc(sender));
                    var stage = store.Next.Next.Next.Next;
                    Assert.IsTrue(stage.Operand is MethodReference callback && callback.Name == "Stage" &&
                        callback.DeclaringType.FullName == typeof(LobbyStartPacketRuntime).FullName);
                    Assert.AreSame(originalNext, stage.Next); Assert.AreSame(originalLowTarget, Target(lowBranch.Operand));
                    Assert.IsNotNull(Call(il, "ReceiveMessagesOnChannel")); Assert.IsNotNull(Call(il, "HandleRawPacket"));
                    Assert.IsNotNull(Call(il, "get_Trampoline"));
                });
                definition.Generate();
            }
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in new[] { "ReceiveGameMessages", "exitMP" })
                using (var definition = new DynamicMethodDefinition(typeof(Platform_Multiplayer).GetMethod(name, flags)))
                using (var context = new ILContext(definition.Definition))
                {
                    context.ReferenceBag = RuntimeILReferenceBag.Instance;
                    context.Invoke(il => {
                        var original = il.Body.Instructions.First();
                        if (name == "ReceiveGameMessages") LobbyStartPacketIl.Replay(il); else LobbyStartPacketIl.Exit(il);
                        Assert.AreEqual(OpCodes.Ldarg_0, il.Body.Instructions.First().OpCode);
                        var callback = il.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method &&
                            method.DeclaringType.FullName == typeof(LobbyStartPacketRuntime).FullName);
                        Assert.AreSame(original, callback.Next, "The full original receive/exit body remains after the prolog.");
                    });
                    definition.Generate();
                }
        }

        private static void RejectChangedDrain(Action<ILContext> mutate)
        {
            using (var definition = new DynamicMethodDefinition(DrainMethod()))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(il => {
                    mutate(il); var before = il.Body.Instructions.ToArray();
                    Assert.ThrowsExactly<InvalidOperationException>(() => LobbyStartPacketIl.Stage(il));
                    CollectionAssert.AreEqual(before, il.Body.Instructions.ToArray(), "Unknown drain shape was partially edited.");
                });
            }
        }

        [TestMethod]
        public void UnknownChannelDecodeOrderAndUpstreamNativeForwardingRejectWithoutPartialEdits()
        {
            RejectChangedDrain(il => {
                var channel = Call(il, "ReceiveMessagesOnChannel").Previous.Previous.Previous.Previous.Previous;
                channel.OpCode = OpCodes.Ldc_I4_3; channel.Operand = null;
            });
            RejectChangedDrain(il => {
                var identity = Call(il, "GetSteamID");
                var block = new[] { identity.Previous.Previous, identity.Previous, identity, identity.Next };
                var decodeStore = Call(il, "FromBytes").Next;
                foreach (var instruction in block) il.Body.Instructions.Remove(instruction);
                int index = il.Body.Instructions.IndexOf(decodeStore) + 1;
                foreach (var instruction in block) il.Body.Instructions.Insert(index++, instruction);
            });
            RejectChangedDrain(il => {
                var decode = Call(il, "FromBytes"); int index = il.Body.Instructions.IndexOf(decode.Next) + 1;
                foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Call, (MethodReference)decode.Operand), Instruction.Create(OpCodes.Pop) })
                    il.Body.Instructions.Insert(index++, instruction);
            });
            RejectChangedDrain(il => {
                var decode = Call(il, "FromBytes"); Assert.IsTrue(decode.Next.MatchStloc(out int data));
                var branch = decode.Next.Next.Next.Next.Next; var target = Target(branch.Operand);
                var process = typeof(Platform_Multiplayer).GetMethod("processMessage", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(Platform_Multiplayer.MPData), typeof(Platform_Multiplayer.MPGameMember), typeof(bool) }, null);
                var forwarding = new[] { Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldloc, il.Body.Variables[data]),
                    Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ldc_I4_0),
                    Instruction.Create(OpCodes.Call, il.Module.ImportReference(process)), Instruction.Create(OpCodes.Pop) };
                int index = il.Body.Instructions.IndexOf(target);
                foreach (var instruction in forwarding) il.Body.Instructions.Insert(index++, instruction);
                ((ILLabel)branch.Operand).Target = forwarding[0];
            });
        }
    }
}
