using APIShared.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class NetworkPacketTests
{
    private sealed class Packet { public int Value; public int[] Items = []; }

    [TestMethod]
    public void MissingRegistrationNeverSerializesOrSends()
    {
        var result = ChoreSendPolicy.Send(new Packet(), 7, false,
            _ => throw new AssertFailedException("serialized"), () => throw new AssertFailedException("manager"),
            (_, _) => Assert.Fail("sent"), 1200);
        Assert.AreEqual(NetworkSendStatus.Unavailable, result.Status);
    }

    [TestMethod]
    [DataRow(1197, true)]
    [DataRow(1198, true)]
    [DataRow(1199, false)]
    public void LimitIncludesExactlyTwoPacketIdBytes(int size, bool accepted)
    {
        int sent = 0;
        var packet = new Packet();
        var result = ChoreSendPolicy.Send(packet, 321, true, _ => new byte[size], () => 1,
            (received, id) => { Assert.AreSame(packet, received); Assert.AreEqual((short)321, id); sent++; }, 1200);
        Assert.AreEqual(accepted, result.Submitted);
        Assert.AreEqual(accepted ? 1 : 0, sent);
        Assert.AreEqual(size, result.BodyBytes);
    }

    [TestMethod]
    public void LowerConsumerLimitIsEnforcedAndUnsafeLimitsAreRejected()
    {
        Assert.AreEqual(NetworkSendStatus.TooLarge,
            ChoreSendPolicy.Send(new Packet(), 7, true, _ => new byte[9], () => 1, (_, _) => Assert.Fail(), 10).Status);
        foreach (int limit in new[] { 0, 1, 1201, int.MaxValue })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                ChoreSendPolicy.Send(new Packet(), 7, true, _ => [], () => 1, (_, _) => { }, limit));
    }

    [TestMethod]
    public void MissingManagerAndSerializerFailuresNeverReachTransport()
    {
        var packet = new Packet();
        Assert.AreEqual(NetworkSendStatus.Unavailable,
            ChoreSendPolicy.Send(packet, 7, true, _ => [1], () => 0, (_, _) => Assert.Fail(), 1200).Status);
        Assert.AreEqual(NetworkSendStatus.PreparationFailed,
            ChoreSendPolicy.Send(packet, 7, true, _ => throw new Exception("formatter"), () => 1, (_, _) => Assert.Fail(), 1200).Status);
        Assert.AreEqual(NetworkSendStatus.PreparationFailed,
            ChoreSendPolicy.Send(packet, 7, true, _ => null, () => 1, (_, _) => Assert.Fail(), 1200).Status);
    }

    [TestMethod]
    public void TransportExceptionIsReportedWithoutRetry()
    {
        int attempts = 0;
        var result = ChoreSendPolicy.Send(new Packet(), 7, true, _ => [1], () => 1,
            (_, _) => { attempts++; throw new Exception("queue"); }, 1200);
        Assert.AreEqual(NetworkSendStatus.TransportFailed, result.Status);
        Assert.AreEqual("queue", result.Reason);
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public void SerializationEvidenceCannotBeChangedByCaller()
    {
        byte[] body = [9];
        var result = ChoreSendPolicy.Send(new Packet(), 7, true, _ => body, () => 1, (_, _) => { }, 1200);
        body[0] = 3;
        byte[] copy = result.CopySerializedBody();
        Assert.AreEqual((byte)9, copy[0]);
        copy[0] = 4;
        Assert.AreEqual((byte)9, result.CopySerializedBody()[0]);
    }

    [TestMethod]
    public void ChoreExecutesInlineButSteamCannotReachGameplay()
    {
        int calls = 0, reports = 0;
        int thread = Environment.CurrentManagedThreadId;
        var receiver = new PacketReceivePolicy<Packet>(() => true, (_, _) => reports++);
        receiver.Chore(new Packet(), 123, _ => throw new AssertFailedException("validated Steam"), _ => calls++);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(1, reports);
        receiver.Chore(new Packet(), null, _ => true, _ => { Assert.AreEqual(thread, Environment.CurrentManagedThreadId); calls++; });
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void ChoreActivationValidationAndFailureIsolationAreIndependent()
    {
        bool enabled = false;
        int calls = 0;
        var receiver = new PacketReceivePolicy<Packet>(() => enabled, (_, _) => throw new Exception("logger"));
        receiver.Chore(new Packet(), null, _ => true, _ => calls++);
        enabled = true;
        receiver.Chore(new Packet(), null, _ => false, _ => calls++);
        receiver.Chore(new Packet(), null, _ => throw new Exception("validator"), _ => calls++);
        receiver.Chore(new Packet(), null, null, _ => throw new Exception("handler"));
        receiver.Chore(new Packet(), null, null, _ => calls++);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void MessagesCopyPayloadBeforeDispatchAndValidateOnlyAfterDispatch()
    {
        var queue = new Queue<Action>();
        int validation = 0, calls = 0;
        var packet = new Packet { Value = 8, Items = [10] };
        var receiver = new PacketReceivePolicy<Packet>(() => { Assert.AreEqual(1, validation); return true; }, (_, _) => Assert.Fail());
        receiver.Message(packet, 123, p => new Packet { Value = p.Value, Items = (int[])p.Items.Clone() },
            action => { queue.Enqueue(action); return true; },
            message => { Assert.AreEqual((ulong)123, message.SenderSteamId); return true; },
            message => { calls++; Assert.AreEqual(8, message.Packet.Value); Assert.AreEqual(10, message.Packet.Items[0]); });
        Assert.AreEqual(0, calls);
        packet.Value = 99; packet.Items[0] = 99;
        validation = 1;
        queue.Dequeue()();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void QueuedMessageRechecksCurrentActivationAndAuthority()
    {
        var queue = new Queue<Action>();
        bool enabled = true;
        ulong host = 100;
        int calls = 0;
        var receiver = new PacketReceivePolicy<Packet>(() => enabled, (_, _) => Assert.Fail());
        void Receive() => receiver.Message(new Packet(), 100, p => p,
            action => { queue.Enqueue(action); return true; }, m => m.SenderSteamId == host, _ => calls++);
        Receive(); enabled = false; queue.Dequeue()();
        enabled = true; Receive(); host = 200; queue.Dequeue()();
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void MissingIdentityOrDispatcherNeverExecutesInline()
    {
        int reports = 0;
        var receiver = new PacketReceivePolicy<Packet>(null, (_, _) => reports++);
        receiver.Message(new Packet(), null, _ => throw new AssertFailedException("copied"), _ => false, null, _ => Assert.Fail());
        receiver.Message(new Packet(), 100, p => p, _ => false, null, _ => Assert.Fail());
        Assert.AreEqual(1, reports);
    }

    [TestMethod]
    public void MessageHandlerFailureDoesNotPreventFollowingMessage()
    {
        var queue = new Queue<Action>();
        int calls = 0, reports = 0;
        var receiver = new PacketReceivePolicy<Packet>(null, (_, _) => reports++);
        foreach (int value in new[] { 1, 2 })
            receiver.Message(new Packet { Value = value }, 100, p => p,
                action => { queue.Enqueue(action); return true; }, null,
                p => { if (p.Packet.Value == 1) throw new Exception("first"); calls++; });
        while (queue.TryDequeue(out var action)) action();
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, reports);
    }
}
