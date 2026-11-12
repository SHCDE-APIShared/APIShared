using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;

namespace APIShared.Units
{
    internal sealed class UnitVariantDefinition
    {
        internal readonly string Owner, Id, Key;
        internal readonly int NativeType;
        internal UnitVariantDefinition(string owner, string id, int nativeType)
        { Owner = owner; Id = id; NativeType = nativeType; Key = KeyFor(owner, id); }
        internal static string KeyFor(string owner, string id) => owner.Length + ":" + owner + id;
    }

    internal struct UnitIdentityProbe
    {
        internal bool Alive;
        internal uint GlobalId;
        internal int NativeType, TransformType;
        internal bool Matches(uint globalId, int carrier, int peasant, out bool pending)
        {
            pending = Alive && GlobalId == globalId && globalId != 0 && NativeType == peasant && NativeType != carrier && TransformType == carrier;
            return Alive && GlobalId == globalId && globalId != 0 && (NativeType == carrier || pending);
        }
    }

    internal sealed class UnitIdentityRow
    {
        internal string Owner, Variant;
        internal int NativeType, UnitId;
        internal uint GlobalId;
        internal bool Pending;
        internal UnitIdentitySnapshot Snapshot => new UnitIdentitySnapshot(Owner, Variant, NativeType, UnitId, GlobalId, Pending);
    }

    // Engine-independent identity state. Runtime serializes every write through the real engine monitor;
    // published dictionaries and lists are never mutated, so presentation reads take no simulation lock.
    // Only marked slots are reconciled. Missing definitions remain dormant reservations and survive saves.
    internal sealed class UnitIdentityRegistry
    {
        internal const int MaximumMarks = 16384, MaximumBytes = 1048576;
        private readonly Func<int, UnitIdentityProbe> read;
        private readonly Func<object> world;
        private readonly Func<bool> canMutate;
        private readonly Func<long> worldSession;
        private readonly int peasant;
        private readonly Dictionary<string, UnitVariantDefinition> definitions = new Dictionary<string, UnitVariantDefinition>(StringComparer.Ordinal);
        private Dictionary<int, UnitIdentityRow> marks = new Dictionary<int, UnitIdentityRow>();
        private readonly List<int> removed = new List<int>();
        private object boundWorld;
        private IReadOnlyList<UnitIdentityRow> stagedRestore;
        private long stagedSession;
        private int encodedBytes = 8;
        private Published published = new Published(null, new Dictionary<int, UnitIdentitySnapshot>(), new Dictionary<string, IReadOnlyList<UnitIdentitySnapshot>>());
        private static readonly IReadOnlyList<UnitIdentitySnapshot> Empty = Array.AsReadOnly(Array.Empty<UnitIdentitySnapshot>());

        private sealed class Published
        {
            internal readonly object World;
            internal readonly Dictionary<int, UnitIdentitySnapshot> Marks;
            internal readonly Dictionary<string, IReadOnlyList<UnitIdentitySnapshot>> Members;
            internal Published(object world, Dictionary<int, UnitIdentitySnapshot> marks, Dictionary<string, IReadOnlyList<UnitIdentitySnapshot>> members)
            { World = world; Marks = marks; Members = members; }
        }

        internal UnitIdentityRegistry(Func<int, UnitIdentityProbe> reader, Func<object> currentWorld, Func<bool> mutationContext, int peasantType,
            Func<long> currentWorldSession = null)
        { read = reader; world = currentWorld; canMutate = mutationContext; peasant = peasantType; worldSession = currentWorldSession ?? (() => 0); }
        internal static bool ValidText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 128) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsSurrogate(text[i])) continue;
                if (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[++i])) return false;
            }
            return true;
        }
        internal bool Register(string owner, string id, int type, out UnitVariantDefinition definition, out string reason)
        {
            definition = null;
            if (!ValidText(owner) || !ValidText(id) || type < 0 || type > ushort.MaxValue)
            { reason = "Owner and variant IDs need 1–128 nonblank characters and a native carrier in 0–65535."; return false; }
            string key = UnitVariantDefinition.KeyFor(owner, id);
            if (definitions.ContainsKey(key)) { reason = "This owner already registered the variant ID."; return false; }
            definition = new UnitVariantDefinition(owner, id, type); definitions.Add(key, definition);
            reason = string.Empty; return true;
        }

        internal static bool AllowsMutation(bool holdsMonitor, bool knownWorld, bool multiplayer, bool nativeTick, bool authenticatedChore) =>
            holdsMonitor && knownWorld && (!multiplayer || nativeTick || authenticatedChore);

        private bool Admit(out string reason)
        {
            if (!canMutate()) { reason = "The current native world and an authorized engine-monitor context are required."; return false; }
            object current = world();
            if (current == null) { reason = "No confirmed native world is available."; return false; }
            if (!ApplyPrepared()) { reason = "Identity restore belongs to another native load; mutation was refused."; return false; }
            if (!ReferenceEquals(current, world())) { reason = "The native world changed."; return false; }
            if (!ReferenceEquals(current, boundWorld)) BindWorld(current, Array.Empty<UnitIdentityRow>());
            reason = string.Empty; return true;
        }

        internal bool Mark(UnitVariantDefinition definition, int slot, uint globalId, out string reason)
        {
            if (!Admit(out reason)) return false;
            if (slot <= 0 || !read(slot).Matches(globalId, definition.NativeType, peasant, out bool pending))
            { reason = "The exact live unit does not have this carrier or a matching pending peasant transformation."; return false; }
            if (marks.TryGetValue(slot, out var old))
            {
                if (old.GlobalId == globalId)
                {
                    if (old.Owner != definition.Owner || old.Variant != definition.Id || old.NativeType != definition.NativeType)
                    { reason = "Identity conflict: unit belongs to " + old.Owner + "/" + old.Variant + "."; return false; }
                    if (old.Pending != pending) { old.Pending = pending; Publish(); }
                    reason = string.Empty; return true;
                }
            }
            int nextCount = marks.Count + (old == null ? 1 : 0);
            int nextBytes = encodedBytes - (old == null ? 0 : RowBytes(old.Owner, old.Variant)) + RowBytes(definition.Owner, definition.Id);
            if (nextCount > MaximumMarks || nextBytes > MaximumBytes)
            { reason = "The identity row or serialized byte limit would be exceeded."; return false; }
            // Only now replace a stale slot: a failed budget check preserves the previous state.
            marks[slot] = new UnitIdentityRow { Owner = definition.Owner, Variant = definition.Id, NativeType = definition.NativeType,
                UnitId = slot, GlobalId = globalId, Pending = pending };
            encodedBytes = nextBytes; Publish(); reason = string.Empty; return true;
        }

        internal bool Unmark(UnitVariantDefinition definition, int slot, uint globalId, out string reason)
        {
            if (!Admit(out reason)) return false;
            if (slot <= 0 || globalId == 0) { reason = "A positive slot and nonzero GlobalId are required."; return false; }
            if (!marks.TryGetValue(slot, out var old)) return true;
            if (old.GlobalId != globalId || old.Owner != definition.Owner || old.Variant != definition.Id)
            { reason = "Only this variant's exact mark can be removed."; return false; }
            encodedBytes -= RowBytes(old.Owner, old.Variant); marks.Remove(slot); Publish(); return true;
        }

        internal bool TryGet(string owner, int slot, uint globalId, out UnitIdentitySnapshot identity)
        {
            identity = default;
            var snapshot = Volatile.Read(ref published);
            object current = world();
            if (current == null || !ReferenceEquals(snapshot.World, current) || globalId == 0 ||
                !snapshot.Marks.TryGetValue(slot, out var found) || found.GlobalId != globalId || found.OwnerGuid != owner ||
                !ReferenceEquals(current, world())) return false;
            identity = found; return true;
        }
        internal bool TryGetLive(string owner, int slot, uint globalId, out UnitIdentitySnapshot identity)
        {
            identity = default;
            if (!ApplyPrepared()) return false;
            object current = world();
            if (current == null || !ReferenceEquals(current, boundWorld) || !TryGet(owner, slot, globalId, out var found) ||
                !read(slot).Matches(globalId, found.NativeType, peasant, out bool pending) || !ReferenceEquals(current, world())) return false;
            identity = new UnitIdentitySnapshot(found.OwnerGuid, found.VariantId, found.NativeType, slot, globalId, pending); return true;
        }
        internal bool IsCurrentWorldReady
        {
            get
            {
                var snapshot = Volatile.Read(ref published);
                object current = world();
                return current != null && ReferenceEquals(snapshot.World, current) && ReferenceEquals(current, world());
            }
        }
        internal IReadOnlyList<UnitIdentitySnapshot> Members(UnitVariantDefinition definition)
        {
            var snapshot = Volatile.Read(ref published);
            object current = world();
            return current != null && ReferenceEquals(snapshot.World, current) && snapshot.Members.TryGetValue(definition.Key, out var result) &&
                ReferenceEquals(current, world()) ? result : Empty;
        }

        internal void Clear()
        { marks.Clear(); boundWorld = null; stagedRestore = null; stagedSession = 0; encodedBytes = 8; Publish(); }
        // Archive preparation only stages decoded values. The first admitted mutation after native-world
        // publication applies these too, so an earlier NativeLoaded observer cannot lose a successful mark
        // when the service's own lifecycle observer runs later.
        internal void StageRestore(IReadOnlyList<UnitIdentityRow> restored, long session = 0)
        { stagedRestore = restored; stagedSession = session; }
        internal bool ApplyPrepared()
        {
            object current = world();
            if (current == null) return false;
            if (stagedRestore != null)
            {
                if (stagedSession != worldSession()) return false;
                BindWorld(current, stagedRestore);
            }
            else if (!ReferenceEquals(current, boundWorld)) BindWorld(current, Array.Empty<UnitIdentityRow>());
            return true;
        }
        internal void BindWorld(object current, IReadOnlyList<UnitIdentityRow> restored)
        {
            var next = new Dictionary<int, UnitIdentityRow>();
            int nextBytes = 8;
            foreach (var item in restored)
                if (read(item.UnitId).Matches(item.GlobalId, item.NativeType, peasant, out bool pending))
                {
                    item.Pending = pending; next.Add(item.UnitId, item);
                    nextBytes += RowBytes(item.Owner, item.Variant);
                }
            if (next.Count > MaximumMarks || nextBytes > MaximumBytes) throw new InvalidDataException("Identity restore exceeds its byte or row limit.");
            marks = next; boundWorld = current; stagedRestore = null; stagedSession = 0; encodedBytes = nextBytes; Publish();
        }
        internal void Reconcile()
        {
            object current = world();
            if (current == null || !ReferenceEquals(current, boundWorld)) { if (boundWorld != null) Clear(); return; }
            bool changed = false; removed.Clear();
            foreach (var item in marks.Values)
            {
                if (!read(item.UnitId).Matches(item.GlobalId, item.NativeType, peasant, out bool pending)) removed.Add(item.UnitId);
                else if (item.Pending != pending) { item.Pending = pending; changed = true; }
            }
            foreach (int slot in removed) { var old = marks[slot]; encodedBytes -= RowBytes(old.Owner, old.Variant); marks.Remove(slot); }
            if (changed || removed.Count != 0) Publish();
        }
        private void Publish()
        {
            var visible = new Dictionary<int, UnitIdentitySnapshot>();
            var groups = new Dictionary<string, List<UnitIdentitySnapshot>>(StringComparer.Ordinal);
            foreach (var item in marks.Values)
            {
                string key = UnitVariantDefinition.KeyFor(item.Owner, item.Variant);
                if (!definitions.TryGetValue(key, out var definition) || definition.NativeType != item.NativeType) continue;
                var value = item.Snapshot; visible.Add(item.UnitId, value);
                if (!groups.TryGetValue(key, out var list)) groups.Add(key, list = new List<UnitIdentitySnapshot>());
                list.Add(value);
            }
            var members = new Dictionary<string, IReadOnlyList<UnitIdentitySnapshot>>(StringComparer.Ordinal);
            foreach (var group in groups)
            { group.Value.Sort((a, b) => a.UnitId.CompareTo(b.UnitId)); members.Add(group.Key, group.Value.AsReadOnly()); }
            Volatile.Write(ref published, new Published(boundWorld, visible, members));
        }

        // BinaryWriter encodes each UTF-8 string's byte length with a 7-bit prefix.
        // Reservation uses the exact wire cost, including dormant rows, before changing any mark.
        private static int RowBytes(string owner, string variant) => StringBytes(owner) + StringBytes(variant) + 12;
        private static int StringBytes(string value)
        {
            int length = Encoding.UTF8.GetByteCount(value), prefix = 1;
            for (int rest = length; rest >= 128; rest >>= 7) prefix++;
            return length + prefix;
        }

        // Stable text keys and unsigned native IDs, never registration indexes. The bounded complete
        // payload is decoded before a caller can replace live state; malformed loads cannot partly apply.
        internal byte[] Capture()
        {
            object expected = world();
            if (expected == null || !ApplyPrepared()) return null;
            Reconcile();
            if (!ReferenceEquals(expected, world())) return null;
            byte[] bytes = Encode();
            return ReferenceEquals(expected, world()) ? bytes : null;
        }
        internal byte[] Encode()
        {
            var ordered = new List<UnitIdentityRow>(marks.Values);
            ordered.Sort((a, b) => { int c = StringComparer.Ordinal.Compare(a.Owner, b.Owner); if (c != 0) return c;
                c = StringComparer.Ordinal.Compare(a.Variant, b.Variant); return c != 0 ? c : a.UnitId.CompareTo(b.UnitId); });
            using (var stream = new MemoryStream(encodedBytes))
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(0x31444955); writer.Write(ordered.Count);
                foreach (var item in ordered)
                { writer.Write(item.Owner); writer.Write(item.Variant); writer.Write(item.NativeType); writer.Write(item.UnitId); writer.Write(item.GlobalId); }
                writer.Flush();
                if (stream.Length > MaximumBytes) throw new InvalidDataException("Identity payload exceeds 1 MiB.");
                return stream.ToArray();
            }
        }
        internal static IReadOnlyList<UnitIdentityRow> Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8 || bytes.Length > MaximumBytes) throw new InvalidDataException("Invalid identity payload length.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadInt32() != 0x31444955) throw new InvalidDataException("Unsupported identity payload.");
                int count = reader.ReadInt32();
                if (count < 0 || count > MaximumMarks || count > (bytes.Length - 8) / 16) throw new InvalidDataException("Invalid identity count.");
                var rows = new List<UnitIdentityRow>(count); var slots = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    var item = new UnitIdentityRow { Owner = reader.ReadString(), Variant = reader.ReadString(),
                        NativeType = reader.ReadInt32(), UnitId = reader.ReadInt32(), GlobalId = reader.ReadUInt32() };
                    if (!ValidText(item.Owner) || !ValidText(item.Variant) || item.NativeType < 0 || item.NativeType > ushort.MaxValue ||
                        item.UnitId <= 0 || item.GlobalId == 0 || !slots.Add(item.UnitId)) throw new InvalidDataException("Invalid or duplicate identity row.");
                    rows.Add(item);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing identity data.");
                return rows.AsReadOnly();
            }
        }
    }
}
