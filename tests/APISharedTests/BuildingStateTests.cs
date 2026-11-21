using System;
using System.Buffers.Binary;
using System.Linq;
using APIShared.Buildings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    // Contracts of the per-building value tables that native consumer code and saves depend on:
    // identity per ID and GlobalId, the native layout, the deterministic record and its validation.
    [TestClass]
    public sealed class BuildingStateTests
    {
        private static readonly byte[] TwoValues = { 3, 1 };

        [TestMethod]
        [TestCategory("BuildingState")]
        public void EntriesBelongToOneBuildingIdentity()
        {
            var memory = new BuildingStateMemory(TwoValues);
            Assert.AreEqual(0, memory.Get(7, 100, 0), "a new building starts with zeros");
            Assert.IsTrue(memory.Set(7, 100, 0, 2));
            Assert.IsTrue(memory.Set(7, 100, 1, 1));
            Assert.AreEqual(2, memory.Get(7, 100, 0));
            Assert.AreEqual(0, memory.Get(7, 101, 0), "another building on the same slot inherits nothing");

            Assert.IsTrue(memory.Set(7, 100, 0, 0));
            Assert.AreEqual(1, memory.Get(7, 100, 1), "a change for the same building keeps its other values");
            Assert.IsTrue(memory.Set(7, 101, 0, 3));
            Assert.AreEqual(0, memory.Get(7, 101, 1), "the first write for a new identity clears the earlier values");
            Assert.AreEqual(0, memory.Get(7, 100, 0), "the earlier building's entry is gone");
        }

        [TestMethod]
        [TestCategory("BuildingState")]
        public void InvalidWritesChangeNothing()
        {
            var memory = new BuildingStateMemory(TwoValues);
            memory.Set(7, 100, 0, 2);
            Assert.IsFalse(memory.Set(7, 100, 0, 4), "above the maximum");
            Assert.IsFalse(memory.Set(7, 100, 2, 1), "unknown value index");
            Assert.IsFalse(memory.Set(7, 0, 0, 1), "zero GlobalId");
            Assert.IsFalse(memory.Set(0, 100, 0, 1), "IDs are one-based");
            Assert.IsFalse(memory.Set(BuildingStateMemory.Capacity, 100, 0, 1), "beyond the table");
            Assert.AreEqual(2, memory.Get(7, 100, 0));
            Assert.AreEqual(0, memory.Get(BuildingStateMemory.Capacity, 100, 0));
            Assert.AreEqual(0, memory.Get(7, 100, -1));
        }

        [TestMethod]
        [TestCategory("BuildingState")]
        public unsafe void NativeLayoutIsTheDocumentedOne()
        {
            var two = new BuildingStateMemory(TwoValues);
            Assert.AreEqual(8, two.EntrySize);
            Assert.AreEqual(16, new BuildingStateMemory(new byte[5] { 1, 1, 1, 1, 1 }).EntrySize);
            two.Set(7, 0xA1B2C3D4, 0, 3);
            two.Set(7, 0xA1B2C3D4, 1, 1);
            byte* entry = (byte*)two.Address + 7 * two.EntrySize;
            Assert.AreEqual(0xA1B2C3D4u, *(uint*)(entry + BuildingStateMemory.GlobalIdOffset));
            Assert.AreEqual(3, entry[BuildingStateMemory.ValuesOffset]);
            Assert.AreEqual(1, entry[BuildingStateMemory.ValuesOffset + 1]);

            // Native code writes simulation values of a matching entry directly.
            entry[BuildingStateMemory.ValuesOffset + 1] = 0;
            Assert.AreEqual(0, two.Get(7, 0xA1B2C3D4, 1));
        }

        [TestMethod]
        [TestCategory("BuildingState")]
        public void RecordRoundTripsDeterministically()
        {
            var memory = new BuildingStateMemory(TwoValues);
            memory.Set(9, 50, 0, 1);
            memory.Set(3, 40, 1, 1);
            memory.Set(5, 60, 0, 0); // all zero: not written
            byte[] saved = memory.Encode();
            Assert.AreEqual(2, BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(1)));

            var restored = new BuildingStateMemory(TwoValues);
            restored.Set(11, 70, 0, 3);
            Assert.IsTrue(restored.TryRestore(saved));
            Assert.AreEqual(1, restored.Get(9, 50, 0));
            Assert.AreEqual(1, restored.Get(3, 40, 1));
            Assert.AreEqual(0, restored.Get(11, 70, 0), "a load replaces the table");
            CollectionAssert.AreEqual(saved, restored.Encode(), "equal state gives equal bytes on every peer");
        }

        [TestMethod]
        [TestCategory("BuildingState")]
        public void RecordsOfFewerValuesRestoreAndNewValuesStartAtZero()
        {
            var one = new BuildingStateMemory(new byte[] { 3 });
            one.Set(4, 44, 0, 2);
            var two = new BuildingStateMemory(TwoValues);
            Assert.IsTrue(two.TryRestore(one.Encode()));
            Assert.AreEqual(2, two.Get(4, 44, 0));
            Assert.AreEqual(0, two.Get(4, 44, 1));
            Assert.IsFalse(one.TryRestore(two.Encode()), "a record with more values than registered is refused");
        }

        [TestMethod]
        [TestCategory("BuildingState")]
        public void DamagedRecordsLeaveTheTableEmpty()
        {
            var memory = new BuildingStateMemory(TwoValues);
            memory.Set(3, 40, 0, 1);
            memory.Set(9, 50, 0, 2);
            byte[] saved = memory.Encode();
            const int First = 5, Entry = 8;

            byte[] aboveMaximum = (byte[])saved.Clone();
            aboveMaximum[First + Entry + 6] = 4;
            byte[] unsorted = (byte[])saved.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(unsorted.AsSpan(First + Entry), 3);
            byte[] zeroGlobal = (byte[])saved.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(zeroGlobal.AsSpan(First + 2), 0);
            foreach (byte[] damaged in new[] { aboveMaximum, unsorted, zeroGlobal, saved.AsSpan(0, saved.Length - 1).ToArray(), new byte[3], null })
            {
                var target = new BuildingStateMemory(TwoValues);
                target.Set(3, 40, 0, 3);
                Assert.IsFalse(target.TryRestore(damaged));
                Assert.AreEqual(0, target.Get(3, 40, 0), "a refused record leaves no partial state");
            }
        }

        [TestMethod]
        [TestCategory("BuildingState")]
        public void RegistrationsAreValidatedBeforeAnyResourceIsCreated()
        {
            BuildingStateValue owner = new BuildingStateValue("runs", 3, BuildingStateValueAccess.OwnerRequest);
            eStructs[] types = { eStructs.STRUCT_TANNERS_WORKSHOP };
            Assert.IsTrue(BuildingStates.TryValidate(new BuildingStateRegistration("owner", "id", "Owner.Save", types, new[] { owner }),
                out byte[] maxima, out _));
            CollectionAssert.AreEqual(new byte[] { 3 }, maxima);
            foreach (var invalid in new[]
            {
                null,
                new BuildingStateRegistration("", "id", "Owner.Save", types, new[] { owner }),
                new BuildingStateRegistration("owner", new string('x', 129), "Owner.Save", types, new[] { owner }),
                new BuildingStateRegistration("owner", "id", " ", types, new[] { owner }),
                new BuildingStateRegistration("owner", "id", "Owner.Save", new eStructs[0], new[] { owner }),
                new BuildingStateRegistration("owner", "id", "Owner.Save", types, new BuildingStateValue[0]),
                new BuildingStateRegistration("owner", "id", "Owner.Save", types, new BuildingStateValue[13].Populate(owner)),
                new BuildingStateRegistration("owner", "id", "Owner.Save", types, new[] { new BuildingStateValue("zero", 0, BuildingStateValueAccess.OwnerRequest) }),
                new BuildingStateRegistration("owner", "id", "Owner.Save", types, new[] { new BuildingStateValue("bad", 1, (BuildingStateValueAccess)9) })
            })
                Assert.IsFalse(BuildingStates.TryValidate(invalid, out _, out string reason) || reason.Length == 0);
        }

        [TestMethod]
        [TestCategory("BuildingState")]
        public void RequestPacketRoundTripsWithinItsChannelLimit()
        {
            var packet = new BuildingStatePacket(new string('o', 128), new string('i', 128), 4095, 4_000_000_000, 11, 255, int.MaxValue);
            byte[] body = MessagePackSerializer.Serialize(packet);
            Assert.IsTrue(body.Length + sizeof(short) <= 1024);
            var decoded = MessagePackSerializer.Deserialize<BuildingStatePacket>(body);
            Assert.AreEqual(packet.Owner, decoded.Owner);
            Assert.AreEqual(packet.Id, decoded.Id);
            Assert.AreEqual(packet.BuildingId, decoded.BuildingId);
            Assert.AreEqual(packet.GlobalId, decoded.GlobalId);
            Assert.AreEqual(packet.ValueIndex, decoded.ValueIndex);
            Assert.AreEqual(packet.Value, decoded.Value);
            Assert.AreEqual(packet.Serial, decoded.Serial);
            Assert.IsFalse(decoded.AllOfSameType);

            var batch = new BuildingStatePacket(packet.Owner, packet.Id, packet.BuildingId, packet.GlobalId,
                packet.ValueIndex, packet.Value, packet.Serial, allOfSameType: true);
            byte[] batchBody = MessagePackSerializer.Serialize(batch);
            Assert.IsTrue(MessagePackSerializer.Deserialize<BuildingStatePacket>(batchBody).AllOfSameType);
            Assert.AreEqual((byte)0x98, body[0], "the original single-building packet has eight fields");
            Assert.AreEqual((byte)1, body[1], "the original protocol remains on the wire");
            Assert.AreEqual((byte)2, batchBody[1]);
            CollectionAssert.AreEqual(body.Skip(2).ToArray(), batchBody.Skip(2).ToArray());

            foreach (var invalid in new[]
            {
                new BuildingStatePacket("o", "i", 0, 1, 0, 1, 1),
                new BuildingStatePacket("o", "i", 1, 0, 0, 1, 1),
                new BuildingStatePacket("o", "i", 1, 1, 12, 1, 1),
                new BuildingStatePacket("o", "", 1, 1, 0, 1, 1),
                new BuildingStatePacket("o", "i", 1, 1, 0, 1, 0)
            })
                Assert.ThrowsExactly<MessagePackSerializationException>(() => MessagePackSerializer.Serialize(invalid));
        }

        [TestMethod]
        [TestCategory("BuildingState")]
        public void SameTypeRequestSetsOnlyLivingOwnedBuildingsAndKeepsSimulationValues()
        {
            const eStructs type = eStructs.STRUCT_TANNERS_WORKSHOP;
            var registration = new BuildingStateRegistration("owner", "id", "Owner.Save", new[] { type },
                new[]
                {
                    new BuildingStateValue("runs", 3, BuildingStateValueAccess.OwnerRequest),
                    new BuildingStateValue("trip", 1, BuildingStateValueAccess.Simulation)
                }) { AllowSameTypeOwnerRequests = true };
            var table = new BuildingStateTable(registration, new BuildingStateMemory(TwoValues));
            var buildings = new[]
            {
                new GameBuilding { r_AliveState = AliveState.IsAlive, r_GlobalId = 11, r_BuildingType = type, r_PlayerIdOwner = 2 },
                new GameBuilding { r_AliveState = AliveState.IsAlive, r_GlobalId = 12, r_BuildingType = type, r_PlayerIdOwner = 2, r_IsSleeping = 1 },
                new GameBuilding { r_AliveState = AliveState.IsAlive, r_GlobalId = 13, r_BuildingType = type, r_PlayerIdOwner = 3 },
                new GameBuilding { r_AliveState = AliveState.NeedsInit, r_GlobalId = 14, r_BuildingType = type, r_PlayerIdOwner = 2 },
                new GameBuilding { r_AliveState = AliveState.IsAlive, r_GlobalId = 15, r_PlayerIdOwner = 2 }
            };
            Assert.IsTrue(table.TrySetFromSimulation(2, 12, 1, 1));
            BuildingStates.ApplySameType(table, buildings, type, 2,
                new BuildingStatePacket("owner", "id", 1, 11, 0, 2, 1, allOfSameType: true));
            Assert.AreEqual((byte)2, table.GetValue(1, 11, 0));
            Assert.AreEqual((byte)2, table.GetValue(2, 12, 0));
            Assert.AreEqual((byte)1, table.GetValue(2, 12, 1));
            Assert.AreEqual((byte)0, table.GetValue(3, 13, 0));
            Assert.AreEqual((byte)0, table.GetValue(4, 14, 0));
            Assert.AreEqual((byte)0, table.GetValue(5, 15, 0));
        }
    }

    internal static class BuildingStateTestArrays
    {
        internal static T[] Populate<T>(this T[] array, T value)
        {
            for (int i = 0; i < array.Length; i++) array[i] = value;
            return array;
        }
    }
}
