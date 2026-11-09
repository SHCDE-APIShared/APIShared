using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using APIShared.Units.AiMilitary;
using APIShared.Units.AiMilitary.Quota;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    // Calls the real production context callback against private native records.
    // The separately executed AiMilitary.NativeTests proves the emitted CPU bridge.
    [TestClass, DoNotParallelize]
    public unsafe class AiMilitaryQuotaRuntimeTests
    {
        [TestMethod]
        public void SameFoldReadersAndLivePoolCountsPreserveNativeCounters()
        {
            const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type runtime = typeof(AiMilitaryQuotaRuntime);
            FieldInfo moduleField = runtime.GetField("module", Flags), stateField = runtime.GetField("state", Flags),
                additionalField = runtime.GetField("isAdditional", Flags), naturalField = runtime.GetField("naturalFold", Flags);
            MethodInfo invoke = runtime.GetMethod("Invoke", Flags);
            var instructions = (Instruction[][])runtime.GetField("Instructions", Flags).GetValue(null);
            var oldInstructions = (Instruction[][])instructions.Clone();
            object oldModule = moduleField.GetValue(null), oldState = stateField.GetValue(null), oldPredicate = additionalField.GetValue(null), oldNatural = naturalField.GetValue(null);
            var snapshot = (AiQuotaSnapshot)runtime.GetField("Snapshot", Flags).GetValue(null);
            var oldSnapshot = new AiQuotaSnapshot(); oldSnapshot.Restore(snapshot);
            var marked = (HashSet<int>)runtime.GetField("MarkedSlots", Flags).GetValue(null);
            int[] oldMarked = new int[marked.Count]; marked.CopyTo(oldMarked);
            IntPtr image = Marshal.AllocHGlobal(0x9000000), state = Marshal.AllocHGlobal(40064), context = Marshal.AllocHGlobal(240);
            var extras = new HashSet<AiUnitRef>();
            ulong m = unchecked((ulong)image.ToInt64());
            void Call(int index, params (int Register, ulong Value)[] values)
            {
                new Span<byte>((void*)context, 240).Clear(); ulong* c = (ulong*)context; c[16] = 0x246;
                foreach (var pair in values) c[pair.Register] = pair.Value;
                invoke.Invoke(null, new object[] { index, context });
            }
            void Unit(int id, uint gid, int role, int type)
            {
                byte* raw = (byte*)(image + AiQuotaNativeContract.UnitManager + id * 0x490);
                *(uint*)(raw + 0x6F0) = gid; *(ushort*)(raw + 0xA82) = (ushort)role; *(ushort*)(raw + 0x6E6) = (ushort)type;
                var unit = new AiUnitRef(id, gid); extras.Add(unit); AiMilitaryQuotaRuntime.SetAdditional(unit, true);
            }
            try
            {
                moduleField.SetValue(null, image); stateField.SetValue(null, state); additionalField.SetValue(null, (Func<AiUnitRef, bool>)extras.Contains);
                new Span<byte>((void*)state, 40064).Clear(); marked.Clear(); snapshot.Clear();
                for (int i = 0; i < instructions.Length; i++) instructions[i] = AiQuotaNativeContract.Decode(AiQuotaNativeContract.Sites[i].Bytes, m + (uint)AiQuotaNativeContract.Sites[i].Rva);
                foreach (int field in AiQuotaNativeContract.Fields) Marshal.WriteInt32(image, field + AiQuotaNativeContract.PlayerStride, 10);
                Call(0);
                int slot = 1;
                foreach (int role in new[] { 1, 2, 6, 7, 21, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 })
                {
                    Unit(slot, (uint)(100 + slot), role, role == 10 ? 30 : 22);
                    Call(1, (5, 1), (11, m + AiQuotaNativeContract.UnitManager + (uint)(slot * 0x490))); slot++;
                }
                for (int index = 2; index < 22; index++)
                {
                    if (index == 7) continue;
                    var site = AiQuotaNativeContract.Sites[index]; var operand = instructions[index][index == 3 ? 5 : 0];
                    ulong* c = (ulong*)context; new Span<byte>((void*)context, 240).Clear(); c[16] = 0x246;
                    int baseRegister = AiMilitaryQuotaRuntime.RegisterIndex(operand.MemoryBase), indexRegister = AiMilitaryQuotaRuntime.RegisterIndex(operand.MemoryIndex);
                    c[indexRegister] = AiQuotaNativeContract.PlayerStride;
                    c[baseRegister] = m + (uint)AiQuotaNativeContract.Fields[site.Column] - operand.MemoryDisplacement64;
                    if (operand.Mnemonic == Mnemonic.Cmp) c[AiMilitaryQuotaRuntime.RegisterIndex(operand.Op1Register)] = 10;
                    invoke.Invoke(null, new object[] { index, context });
                    if (index == 4) Assert.AreEqual(9UL, c[2], "MOV count excludes exactly its samefold contribution.");
                    else Assert.AreEqual(AiQuotaNativeContract.CompareFlags(0x246, 9, 10), c[16], "Only comparison flags change at reader " + index);
                    Assert.AreEqual(10, Marshal.ReadInt32(image, AiQuotaNativeContract.Fields[site.Column] + AiQuotaNativeContract.PlayerStride), "Physical native strength is untouched.");
                }
                // Removing a current marker cannot rewrite a contribution already in
                // the native fold, including a unit that died or changed role later.
                extras.Clear(); AiMilitaryQuotaRuntime.SetAdditional(new AiUnitRef(1, 101), false);
                Assert.AreEqual(1, snapshot.GetExtra(1, AiQuotaSnapshot.Defender));
                // Group quotas use actual current membership instead of the old fold.
                Unit(2, 102, 22, 30); Unit(3, 103, 22, 1); // a transforming reservation is deliberately included
                int group = 2; byte* rawGroup = (byte*)(image + 0x7CC6720 + group * 0x688);
                new Span<byte>(rawGroup, 0x688).Clear(); *(uint*)(rawGroup + 0x34) = 500; *(short*)(rawGroup + 0x5C) = 4;
                foreach (int id in new[] { 2, 3 })
                { byte* raw = (byte*)(image + AiQuotaNativeContract.UnitManager + id * 0x490); *(ushort*)(raw + 0x930) = (ushort)group; *(uint*)(raw + 0x93C) = 500; *(ushort*)(rawGroup + 0x60) |= (ushort)(1 << id); }
                Call(22, (2, m + 0x7CC6720), (4, (uint)group), (14, (uint)(group * 0x688)));
                Assert.AreEqual(AiQuotaNativeContract.CompareFlags(0x246, 2, 2), ((ulong*)context)[16], "The two regular reservations remain in the recruitment quota.");
                Call(7, (2, (uint)(group * 0x688)), (3, m)); Assert.AreEqual(2UL, ((ulong*)context)[0]);
                *(uint*)(rawGroup + 0x34) = 501;
                Call(7, (2, (uint)(group * 0x688)), (3, m)); Assert.AreEqual(4UL, ((ulong*)context)[0], "Stale group identities cannot subtract members.");
                Assert.AreEqual((short)4, *(short*)(rawGroup + 0x5C), "Physical crew staffing remains unchanged.");

                // Rare admission preflight reserves missing regular staff AND the
                // strict native '<20' production slot; E79C itself is not rewritten.
                Marshal.WriteInt32(image, AiQuotaNativeContract.UnitManager, 31);
                Marshal.WriteInt16(image, 0x379F7BA + AiQuotaNativeContract.PlayerStride, 0);
                Marshal.WriteInt32(image, 0x379FB5C + AiQuotaNativeContract.PlayerStride, 0);
                for (int id = 1; id <= 30; id++)
                {
                    byte* raw = (byte*)(image + AiQuotaNativeContract.UnitManager + id * 0x490);
                    new Span<byte>(raw + 0x65C, 0x490).Clear();
                    Unit(id, (uint)(200 + id), 22, (int)eChimps.CHIMP_TYPE_ENGINEER);
                    *(ushort*)(raw + 0x6EE) = 1;
                    *(ushort*)(raw + 0x6E4) = id <= 17 ? (ushort)AliveState.IsAlive : (ushort)AliveState.None;
                }
                byte* candidate = (byte*)(image + AiQuotaNativeContract.UnitManager + 30 * 0x490);
                *(ushort*)(candidate + 0x6E4) = (ushort)AliveState.IsAlive; *(ushort*)(candidate + 0xA82) = 0;
                var recruit = new AiUnitRef(30, 230);
                Assert.IsFalse(AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(recruit, 1, true, out _), "17 physical + extra + missing two regular would strand production at 20.");
                byte* last = (byte*)(image + AiQuotaNativeContract.UnitManager + 17 * 0x490); *(ushort*)(last + 0x6E4) = (ushort)AliveState.None;
                Assert.IsTrue(AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(recruit, 1, true, out _), "16 physical + extra + two reserved regular remains below 20.");
                *(ushort*)(last + 0x6E4) = (ushort)AliveState.IsAlive; *(ushort*)(last + 0x6E6) = (ushort)eChimps.CHIMP_TYPE_PEASANT;
                Assert.IsFalse(AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(recruit, 1, true, out _), "Transforming regular peasants reserve future physical engineer capacity.");
                Marshal.WriteInt16(image, 0x379F7BA + AiQuotaNativeContract.PlayerStride, (short)group);
                Marshal.WriteInt32(image, 0x379FB5C + AiQuotaNativeContract.PlayerStride, 501);
                *(int*)(rawGroup + 0x2C) = 1; *(short*)(rawGroup + 0x5C) = 2; *(ushort*)(rawGroup + 0x60) = (ushort)(1 << 15); *(ushort*)(rawGroup + 0x62) = 1;
                foreach (int id in new[] { 15, 16 })
                {
                    byte* raw = (byte*)(image + AiQuotaNativeContract.UnitManager + id * 0x490); *(ushort*)(raw + 0x930) = (ushort)group; *(uint*)(raw + 0x93C) = 501;
                    extras.Remove(new AiUnitRef(id, (uint)(200 + id))); AiMilitaryQuotaRuntime.SetAdditional(new AiUnitRef(id, (uint)(200 + id)), false);
                }
                byte* eighteenth = (byte*)(image + AiQuotaNativeContract.UnitManager + 18 * 0x490), nineteenth = (byte*)(image + AiQuotaNativeContract.UnitManager + 19 * 0x490);
                *(ushort*)(eighteenth + 0x6E4) = (ushort)AliveState.IsAlive; *(ushort*)(nineteenth + 0x6E4) = (ushort)AliveState.IsAlive;
                Assert.IsFalse(AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(recruit, 1, true, out _), "A complete regular pool still needs the native build slot below 20.");
                *(ushort*)(nineteenth + 0x6E4) = (ushort)AliveState.None;
                Assert.IsTrue(AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(recruit, 1, true, out _));
                *(short*)(rawGroup + 0x5C) = 1; *(ushort*)(rawGroup + 0x62) = 0;
                Assert.IsFalse(AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(recruit, 1, true, out _), "Additional recruits do not satisfy the missing regular pool member.");
                Assert.IsTrue(AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(recruit, 1, false, out _), "A regular recruit satisfies its own reserved pool slot.");
                *(ushort*)(candidate + 0xA82) = 22;
                Assert.IsTrue(AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(recruit, 1, false, out _), "An already-counted free role22 engineer is not counted twice.");
            }
            finally
            {
                moduleField.SetValue(null, oldModule); stateField.SetValue(null, oldState); additionalField.SetValue(null, oldPredicate); naturalField.SetValue(null, oldNatural);
                snapshot.Restore(oldSnapshot); marked.Clear(); foreach (int id in oldMarked) marked.Add(id);
                Array.Copy(oldInstructions, instructions, instructions.Length);
                Marshal.FreeHGlobal(context); Marshal.FreeHGlobal(state); Marshal.FreeHGlobal(image);
            }
        }
    }
}
