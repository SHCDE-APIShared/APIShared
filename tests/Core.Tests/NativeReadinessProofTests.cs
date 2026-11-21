using APIShared.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Tests
{
    [TestClass]
    public sealed class NativeReadinessProofTests
    {
        [TestMethod]
        public void RequiresLocalAndEveryExpectedReceiptInTheCurrentAttempt()
        {
            var proof = new NativeReadinessProof();
            var peers = new Dictionary<ulong, string> { [11] = "local", [22] = "client", [33] = "spectator" };
            proof.Begin(11, "world", 1, peers);
            peers[22] = "new-client-world";

            Assert.IsFalse(proof.TryAccept(22, "new-client-world", "world", 1, 9, 77));
            Assert.IsFalse(proof.TryAccept(99, "client", "world", 1, 9, 77));
            Assert.IsFalse(proof.TryAccept(22, "client", "old-world", 1, 9, 77));
            Assert.IsTrue(proof.TryAccept(33, "spectator", "world", 1, 9, 77));
            Assert.IsTrue(proof.TryAccept(22, "client", "world", 1, 9, 77));
            Assert.AreEqual(NativeReadinessState.Pending, proof.Evaluate());
            Assert.IsTrue(proof.TryAccept(11, "local", "world", 1, 9, 77));
            Assert.AreEqual(NativeReadinessState.Ready, proof.Evaluate());

            proof.Begin(11, "world", 2, peers);
            Assert.IsFalse(proof.TryAccept(11, "local", "world", 1, 9, 77));
            Assert.AreEqual(NativeReadinessState.Pending, proof.Evaluate());
        }

        [TestMethod]
        public void RequiresSameCommandAndTickAndClearRetiresTheProof()
        {
            var proof = new NativeReadinessProof();
            var peers = new Dictionary<ulong, string> { [11] = "local", [22] = "remote" };
            proof.Begin(11, "world", 3, peers);
            proof.TryAccept(11, "local", "world", 3, 9, 77);
            proof.TryAccept(22, "remote", "world", 3, 10, 77);
            Assert.AreEqual(NativeReadinessState.Mismatch, proof.Evaluate());

            proof.Begin(11, "world", 4, peers);
            proof.TryAccept(11, "local", "world", 4, 9, 77);
            proof.TryAccept(22, "remote", "world", 4, 9, 76);
            Assert.AreEqual(NativeReadinessState.Mismatch, proof.Evaluate());

            proof.Clear();
            Assert.IsFalse(proof.TryAccept(11, "local", "world", 4, 9, 77));
            Assert.AreEqual(NativeReadinessState.Pending, proof.Evaluate());
        }

        [TestMethod]
        public void ContradictoryDuplicatesCannotBeOverwrittenBeforeOrAfterAgreement()
        {
            var proof = new NativeReadinessProof();
            var peers = new Dictionary<ulong, string> { [11] = "local", [22] = "remote", [33] = "spectator" };
            proof.Begin(11, "world", 1, peers);
            proof.TryAccept(11, "local", "world", 1, 9, 77);
            proof.TryAccept(22, "remote", "world", 1, 10, 77);
            proof.TryAccept(22, "remote", "world", 1, 9, 77);
            proof.TryAccept(33, "spectator", "world", 1, 9, 77);
            Assert.AreEqual(NativeReadinessState.Mismatch, proof.Evaluate());

            proof.Begin(11, "world", 2, peers);
            proof.TryAccept(11, "local", "world", 2, 9, 77);
            proof.TryAccept(22, "remote", "world", 2, 9, 77);
            proof.TryAccept(33, "spectator", "world", 2, 9, 77);
            proof.TryAccept(22, "remote", "world", 2, 9, 77);
            Assert.AreEqual(NativeReadinessState.Ready, proof.Evaluate());
            proof.TryAccept(22, "remote", "world", 2, 9, 78);
            Assert.AreEqual(NativeReadinessState.Mismatch, proof.Evaluate());
        }

        [TestMethod]
        public void InvalidRosterCannotLeaveAnEarlierAttemptActive()
        {
            var proof = new NativeReadinessProof();
            var local = new KeyValuePair<ulong, string>(11, "local");
            var duplicate = new KeyValuePair<ulong, string>(11, "different-world");
            proof.Begin(11, "world", 1, new[] { local });
            Assert.ThrowsExactly<ArgumentException>(() => proof.Begin(11, "world", 2, new[] { local, duplicate }));
            Assert.IsFalse(proof.TryAccept(11, "local", "world", 1, 9, 77));
            Assert.IsFalse(proof.TryAccept(11, "local", "world", 2, 9, 77));
        }
    }
}
