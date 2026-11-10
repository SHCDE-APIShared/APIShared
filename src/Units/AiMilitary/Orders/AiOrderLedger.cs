using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Units.AiMilitary.Native;

namespace APIShared.Units.AiMilitary.Orders
{
    // Process lifetime, world-generation scoped. Observers publish effective arguments
    // and the raw original result, never SE's reconstructed Post arguments/return value.
    internal sealed unsafe class AiOrderLedger
    {
        private readonly AiMilitaryNativeBackend backend;
        private readonly Dictionary<AiNativeGroup, AiCurrentOrder> orders = new Dictionary<AiNativeGroup, AiCurrentOrder>();
        private readonly Dictionary<AiNativeGroup, int> dispatches = new Dictionary<AiNativeGroup, int>();
        private long generation;
        internal AiOrderLedger(AiMilitaryNativeBackend backend) { this.backend = backend ?? throw new ArgumentNullException(nameof(backend)); }
        internal void Clear(long worldGeneration) { generation = worldGeneration; orders.Clear(); dispatches.Clear(); }
        internal void Invalidate(AiNativeGroup group) { orders.Remove(group); }
        internal void EnterGroupDispatch(AiNativeGroup group)
        { dispatches.TryGetValue(group, out int depth); dispatches[group] = depth + 1; }
        internal void ExitGroupDispatch(AiNativeGroup group)
        {
            if (!dispatches.TryGetValue(group, out int depth)) throw new InvalidOperationException("Unbalanced AI group dispatch.");
            if (depth == 1) dispatches.Remove(group); else dispatches[group] = depth - 1;
        }
        internal bool IsInGroupDispatch(AiNativeGroup group) => dispatches.ContainsKey(group);
        internal void ObserveMove(long worldGeneration, AiNativeGroup group, int x, int y,
            short patrol, bool newOrder, int moveType, long rawOriginalResult)
        {
            if (!CanObserve(worldGeneration, group)) return;
            if (rawOriginalResult == 0) return;
            orders[group] = new AiCurrentOrder(patrol != 0 ? AiOrderKind.Patrol : AiOrderKind.Move, x, y, moveType: moveType);
        }
        internal void ObserveTarget(long worldGeneration, AiNativeGroup group, int command,
            int arg1, int arg2, int arg3, long rawOriginalResult)
        {
            if (!CanObserve(worldGeneration, group) || rawOriginalResult == 0) return;
            // Root's target observer must resolve stable target identity from effective
            // arguments. Unknown commands supersede, rather than retaining an old move.
            if (command == 31) orders[group] = new AiCurrentOrder(AiOrderKind.Stop, command: command);
            else orders.Remove(group);
        }
        internal void ObserveAccepted(long worldGeneration, AiNativeGroup group, AiCurrentOrder order, long rawOriginalResult)
        {
            if (CanObserve(worldGeneration, group) && rawOriginalResult != 0)
            { if (order.Kind == AiOrderKind.Unknown) orders.Remove(group); else orders[group] = order; }
        }
        internal void ObserveStrategic(long worldGeneration, AiNativeGroup group, int action, long rawOriginalResult)
        {
            if (!CanObserve(worldGeneration, group) || rawOriginalResult == 0) return;
            if (backend.TryCaptureStrategicIntent(group, action, out var order)) orders[group] = order;
            else orders.Remove(group);
        }
        private bool CanObserve(long value, AiNativeGroup group) => value == generation && backend.IsCurrent(group);

        internal bool TryGetCurrent(long worldGeneration, AiNativeGroup group, out AiCurrentOrder order, out string reason)
        {
            order = default(AiCurrentOrder);
            if (!CanObserve(worldGeneration, group) || !orders.TryGetValue(group, out order))
            { reason = "CurrentOrderUnknown: no current accepted order exists for this group identity and world."; return false; }
            if (order.Kind == AiOrderKind.Patrol)
            {
                byte* raw = backend.Tribe(group);
                int count = *(short*)(raw + 0x5DE), index = *(short*)(raw + 0x5DC);
                if (*(short*)(raw + 0x582) == 0 || count < 1 || count > 10 || index < 0 || index >= count)
                    return Unknown(group, out order, out reason);
                int x = *(short*)(raw + 0x5B4 + index * 4), y = *(short*)(raw + 0x5B6 + index * 4);
                if (!backend.IsMapPosition(x, y)) return Unknown(group, out order, out reason);
                order = new AiCurrentOrder(AiOrderKind.Patrol, x, y, moveType: order.MoveType);
            }
            else if (order.Kind == AiOrderKind.Move && backend.IsGroupMovementCompleted(group))
            { order = new AiCurrentOrder(AiOrderKind.Idle); orders[group] = order; }
            else if (order.Kind == AiOrderKind.Strategic && !backend.IsStrategicIntentCurrent(group, order))
                return Unknown(group, out order, out reason);
            else if (order.Kind == AiOrderKind.DigMoat && !backend.IsDigMoatIntentCurrent(group, order))
                return Unknown(group, out order, out reason);
            reason = string.Empty; return true;
        }
        private bool Unknown(AiNativeGroup group, out AiCurrentOrder order, out string reason)
        { orders.Remove(group); order = default(AiCurrentOrder); reason = "CurrentOrderUnknown: the accepted order's execution lifetime cannot be confirmed."; return false; }

        internal byte[] Capture()
        {
            var keys = new List<AiNativeGroup>(orders.Keys);
            keys.Sort((a, b) => { int value = a.Owner.CompareTo(b.Owner); if (value != 0) return value;
                value = a.Id.CompareTo(b.Id); return value != 0 ? value : a.GlobalId.CompareTo(b.GlobalId); });
            var current = new List<KeyValuePair<AiNativeGroup, AiCurrentOrder>>();
            foreach (var key in keys)
                if (TryGetCurrent(generation, key, out var order, out _)) current.Add(new KeyValuePair<AiNativeGroup, AiCurrentOrder>(key, order));
            if (current.Count > 4500) throw new InvalidDataException("Too many AI order records.");
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write(0x314F4941); writer.Write(current.Count); // AIO1, world counter intentionally absent.
                foreach (var pair in current)
                {
                    var group = pair.Key; var order = pair.Value;
                    writer.Write(group.Id); writer.Write(group.GlobalId); writer.Write(group.Owner);
                    writer.Write((int)order.Kind); writer.Write(order.X); writer.Write(order.Y); writer.Write(order.Command);
                    writer.Write(order.TargetId); writer.Write(order.TargetGlobalId); writer.Write(order.Action); writer.Write(order.Extra); writer.Write(order.MoveType);
                }
                return memory.ToArray();
            }
        }

        internal void Restore(byte[] payload, long worldGeneration)
        {
            if (payload == null || payload.Length < 8 || payload.Length > 8 + 4500 * 48)
                throw new InvalidDataException("AI order payload length is outside its bounded format.");
            var parsed = new Dictionary<AiNativeGroup, AiCurrentOrder>();
            using (var memory = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(memory))
            {
                if (reader.ReadInt32() != 0x314F4941) throw new InvalidDataException("Unsupported AI order payload format.");
                int count = reader.ReadInt32();
                if (count < 0 || count > 4500 || payload.Length != 8 + count * 48)
                    throw new InvalidDataException("AI order record count/length differs.");
                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32(); uint global = reader.ReadUInt32(); int owner = reader.ReadInt32();
                    int kind = reader.ReadInt32(), x = reader.ReadInt32(), y = reader.ReadInt32(), command = reader.ReadInt32();
                    int target = reader.ReadInt32(); uint targetGlobal = reader.ReadUInt32();
                    int action = reader.ReadInt32(), extra = reader.ReadInt32(), moveType = reader.ReadInt32();
                    if (id <= 0 || id >= 4500 || global == 0 || owner < 1 || owner > 8 ||
                        kind <= (int)AiOrderKind.Unknown || kind > (int)AiOrderKind.AttackTile || (uint)x >= 800 || (uint)y >= 800 ||
                        command < 0 || command > ushort.MaxValue || target < 0 || target > ushort.MaxValue ||
                        action < 0 || action > ushort.MaxValue)
                        throw new InvalidDataException("Invalid AI order identity or domain value.");
                    // moveType is the caller's raw informational value and has no closed domain:
                    // ObserveMove stores it unvalidated, and the Extender's TribeMoveType.Fast is -255.
                    var group = new AiNativeGroup(id, global, owner);
                    if (parsed.ContainsKey(group)) throw new InvalidDataException("Duplicate AI order identity.");
                    parsed.Add(group, new AiCurrentOrder((AiOrderKind)kind, x, y, command, target, targetGlobal, action, extra, moveType));
                }
            }
            // Parse the entire document before touching the current world registry.
            // Native identities and execution lifetimes are checked afresh at restore.
            Clear(worldGeneration);
            foreach (var pair in parsed) orders.Add(pair.Key, pair.Value);
            foreach (var group in new List<AiNativeGroup>(orders.Keys))
                if (!TryGetCurrent(worldGeneration, group, out _, out _)) orders.Remove(group);
        }
    }
}
