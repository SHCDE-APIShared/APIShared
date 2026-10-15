using System;
using System.IO;
using System.Threading;
using APIShared.Events;
using APIShared.Savegames;
using SHCDESE.Interop;

namespace APIShared.Units
{
    // Health fields of one unit slot. A default value means the ID did not resolve.
    internal struct UnitHealthState
    {
        internal bool Found;
        internal int GlobalId;
        internal eChimps UnitType;
        internal uint Current, Max;
    }

    // Must not throw; the production store reads and writes GameUnit through UnitAccess.
    internal interface IUnitHealthStore
    {
        UnitHealthState Read(int unitId);
        void Write(int unitId, uint current, uint max, ushort percent, uint healthBarBlocks);
    }

    // Dependency-free core of UnitStatModifiers: ordered registrations, composition, the applied
    // marker table and its save record. UnitSettled delivers units on one thread, the simulation thread,
    // which is the only writer of individual markers; the save record is captured on the Extender's save
    // thread under the recordVersion seqlock and restored on the load thread, when no unit settles.
    //
    // Applied marker: per unit slot, the GlobalId, type, native maximum and the maximum this service
    // wrote. A unit that settles again with the same identity and type whose maximum is still the
    // written one is composed from the recorded native maximum, so a reload (whose units carry the
    // written value) or a second report never compounds. Any other state (a new unit in the slot, a
    // type change, a maximum another writer changed) starts from the live maximum. The table is
    // replaced at every archive load: restored from the save record, or cleared.
    internal sealed class UnitStatComposer
    {
        internal const string SaveIdentifier = "APIShared.UnitStats";
        internal const string MaxHealthEntry = "maxhealth";
        internal const int RecordBytes = 18, InitialSlots = 4096, MaximumUnitId = 1 << 20;
        private static readonly InvalidOperationException WrongLayer =
            new InvalidOperationException("The contribution does not match the registration's layer.");
        private static readonly InvalidOperationException InvalidContribution =
            new InvalidOperationException("Invalid contribution: a ratio needs numerator >= 0 and denominator >= 1, a range minimum <= maximum.");

        internal sealed class Entry
        {
            internal long Failures;
            internal readonly UnitStatRegistration Handle;
            internal readonly UnitStatModifier Modifier;
            internal Entry(UnitStatRegistration handle, UnitStatModifier modifier) { Handle = handle; Modifier = modifier; }
        }

        internal struct Record
        {
            internal int GlobalId;
            internal eChimps UnitType;
            internal uint NativeMax, AppliedMax;
        }

        private readonly object registrationSync = new object();
        private readonly IUnitHealthStore store;
        private readonly Func<string> activate;
        private bool activated;
        private Entry[] entries = Array.Empty<Entry>();
        // Immutable snapshot of active MaxHealth registrations, sorted by layer, order, owner, ID.
        private Entry[] maxHealth = Array.Empty<Entry>();
        private Record[] records = Array.Empty<Record>();
        // Odd while the marker table or one of its records changes; readers on other threads retry.
        private int recordVersion;
        private long failures, applications;

        // activate runs once, under the registration lock, before the first registration is added; it
        // connects the composer to unit settlement and the save record (null = done, otherwise why not).
        internal UnitStatComposer(IUnitHealthStore store, Func<string> activate)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.activate = activate ?? throw new ArgumentNullException(nameof(activate));
        }

        internal long CallbackFailures => Interlocked.Read(ref failures);
        internal long Applications => Interlocked.Read(ref applications);
        internal int ActiveCount => Volatile.Read(ref maxHealth).Length;

        internal bool TryAdd(string ownerGuid, string registrationId, UnitStat stat, UnitStatLayer layer, int order,
            UnitStatModifier modifier, out UnitStatRegistration registration, out string reason)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || modifier == null)
            { reason = "Owner GUID, registration ID and a modifier are required."; return false; }
            if (stat != UnitStat.MaxHealth) { reason = "Unknown stat " + stat + "."; return false; }
            if (layer < UnitStatLayer.Base || layer > UnitStatLayer.Clamp) { reason = "Unknown layer " + layer + "."; return false; }
            lock (registrationSync)
            {
                foreach (Entry existing in entries)
                    if (existing.Handle.OwnerGuid == ownerGuid && existing.Handle.RegistrationId == registrationId && existing.Handle.Stat == stat)
                    { reason = "This owner already registered that ID for " + stat + "."; return false; }
                if (!activated)
                {
                    string refusal;
                    try { refusal = activate(); }
                    catch (Exception ex) { refusal = "Unit stat modifiers are unavailable: " + ex.Message; }
                    if (refusal != null) { reason = refusal; return false; }
                    activated = true;
                    if (Volatile.Read(ref records).Length == 0) Publish(new Record[InitialSlots]);
                }
                var handle = new UnitStatRegistration(ownerGuid, registrationId, stat, layer, order, Remove);
                var next = new Entry[entries.Length + 1];
                Array.Copy(entries, next, entries.Length);
                next[next.Length - 1] = new Entry(handle, modifier);
                Array.Sort(next, Compare);
                Publish(next);
                registration = handle;
            }
            reason = string.Empty;
            return true;
        }

        private void Remove(UnitStatRegistration handle)
        {
            lock (registrationSync)
            {
                int index = Array.FindIndex(entries, entry => ReferenceEquals(entry.Handle, handle));
                if (index < 0) return;
                var next = new Entry[entries.Length - 1];
                Array.Copy(entries, next, index);
                Array.Copy(entries, index + 1, next, index, next.Length - index);
                Publish(next);
            }
        }

        private void Publish(Entry[] next)
        {
            entries = next;
            Volatile.Write(ref maxHealth, next);
        }

        private static int Compare(Entry left, Entry right)
        {
            int comparison = left.Handle.Layer.CompareTo(right.Handle.Layer);
            if (comparison == 0) comparison = left.Handle.Order.CompareTo(right.Handle.Order);
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left.Handle.OwnerGuid, right.Handle.OwnerGuid);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Handle.RegistrationId, right.Handle.RegistrationId);
        }

        // UnitSettled callback (simulation thread). With no active modifier this is one volatile read.
        internal void OnSettled(UnitSettledNotification notification)
        {
            Entry[] modifiers = Volatile.Read(ref maxHealth);
            if (modifiers.Length == 0) return;
            Settle(notification, modifiers);
        }

        // One composed write per unit per settlement; modifiers run outside both locks. Without .locals
        // init: every local is assigned before use (see SkipLocalsInitAttribute).
        [System.Runtime.CompilerServices.SkipLocalsInit]
        private void Settle(UnitSettledNotification notification, Entry[] modifiers)
        {
            int unitId = notification.UnitId;
            if (unitId <= 0 || unitId >= MaximumUnitId) return;
            UnitHealthState unit = store.Read(unitId);
            // The callback runs inside the settling tick, so a mismatch means the slot is not that unit.
            if (!unit.Found || unit.GlobalId != notification.GlobalId || unit.Max == 0) return;
            uint native = unit.Max;
            Record[] table = Volatile.Read(ref records);
            if (unitId < table.Length)
            {
                Record known = table[unitId];
                if (known.GlobalId == unit.GlobalId && known.UnitType == unit.UnitType && known.AppliedMax == unit.Max)
                    native = known.NativeMax;
            }
            var subject = new UnitStatSubject(UnitStat.MaxHealth, unitId, unit.GlobalId, notification.PlayerId,
                unit.UnitType, notification.Cause, notification.SimulationTick, (int)Math.Min(native, (uint)int.MaxValue));
            uint target = (uint)Math.Max(1L, Compose(modifiers, subject));
            if (target != unit.Max)
            {
                uint current = RescaleCurrent(unit.Current, unit.Max, target);
                ushort percent = HealthPercent(current, target);
                store.Write(unitId, current, target, percent, (uint)(percent / 10));
                Volatile.Write(ref applications, applications + 1);
            }
            if (unitId >= table.Length) table = Grow(table, unitId);
            int writing = Interlocked.Increment(ref recordVersion);
            table[unitId] = new Record { GlobalId = unit.GlobalId, UnitType = unit.UnitType, NativeMax = native, AppliedMax = target };
            Volatile.Write(ref recordVersion, writing + 1);
        }

        // Simulation thread; slot IDs above the initial table are rare.
        private Record[] Grow(Record[] table, int unitId)
        {
            int size = Math.Max(InitialSlots, table.Length);
            while (size <= unitId) size *= 2;
            var grown = new Record[size];
            Array.Copy(table, grown, table.Length);
            Publish(grown);
            return grown;
        }

        private void Publish(Record[] table)
        {
            int writing = Interlocked.Increment(ref recordVersion);
            Volatile.Write(ref records, table);
            Volatile.Write(ref recordVersion, writing + 1);
        }

        // A consistent copy of the marker table for another thread.
        private Record[] Snapshot()
        {
            var spin = new SpinWait();
            while (true)
            {
                int before = Volatile.Read(ref recordVersion);
                if ((before & 1) == 0)
                {
                    var copy = (Record[])Volatile.Read(ref records).Clone();
                    Thread.MemoryBarrier();
                    if (Volatile.Read(ref recordVersion) == before) return copy;
                }
                spin.SpinOnce();
            }
        }

        // One pass in layer order (the snapshot is sorted by layer first). Integer arithmetic only, so every
        // peer computes the same value.
        [System.Runtime.CompilerServices.SkipLocalsInit]
        internal long Compose(Entry[] modifiers, UnitStatSubject subject)
        {
            long value = subject.NativeValue;
            for (int index = 0; index < modifiers.Length; index++)
            {
                Entry entry = modifiers[index];
                if (Volatile.Read(ref entry.Handle.ActiveValue) == 0) continue;
                UnitStatContribution contribution;
                bool applies;
                try { applies = entry.Modifier(subject, out contribution); }
                catch (Exception ex) { Fail(entry, ex); continue; }
                if (!applies) continue;
                UnitStatLayer layer = entry.Handle.Layer;
                if (contribution.Layer != layer) { Fail(entry, WrongLayer); continue; }
                switch (layer)
                {
                    case UnitStatLayer.Base:
                    case UnitStatLayer.Override:
                        value = contribution.Value;
                        break;
                    case UnitStatLayer.Additive:
                        value = Clamp32(value + contribution.Value);
                        break;
                    case UnitStatLayer.Multiplier:
                        if (contribution.Value < 0 || contribution.Second < 1) { Fail(entry, InvalidContribution); continue; }
                        value = Clamp32(DivideRounded(value * contribution.Value, contribution.Second));
                        break;
                    case UnitStatLayer.Clamp:
                        if (contribution.Value > contribution.Second) { Fail(entry, InvalidContribution); continue; }
                        value = Math.Min(Math.Max(value, contribution.Value), contribution.Second);
                        break;
                }
            }
            return value;
        }

        private static long Clamp32(long value) => Math.Min(int.MaxValue, Math.Max(int.MinValue, value));

        // Half away from zero; |numerator| stays below 2^62 (an int times an int).
        internal static long DivideRounded(long numerator, long denominator) =>
            numerator >= 0 ? (numerator + denominator / 2) / denominator : -((-numerator + denominator / 2) / denominator);

        // Keeps the unit's share of health. A living unit keeps at least 1; a unit at 0 stays at 0.
        internal static uint RescaleCurrent(uint current, uint oldMax, uint newMax)
        {
            if (current == 0) return 0;
            if (oldMax == 0) return newMax;
            ulong bounded = Math.Min(current, oldMax);
            ulong scaled = (bounded * newMax + oldMax / 2UL) / oldMax;
            return (uint)Math.Max(1UL, Math.Min(newMax, scaled));
        }

        internal static ushort HealthPercent(uint current, uint max)
        {
            if (max == 0) return 0;
            ulong bounded = Math.Min(current, max);
            return (ushort)Math.Min(100UL, (bounded * 100UL + max / 2UL) / max);
        }

        private void Fail(Entry entry, Exception error)
        {
            Interlocked.Increment(ref failures);
            long count = Interlocked.Increment(ref entry.Failures);
            if ((count & (count - 1)) != 0) return;
            try { InterceptionDiagnostics.Report(entry.Handle.OwnerGuid + "/" + entry.Handle.RegistrationId + " UnitStat " + entry.Handle.Layer, error); }
            catch { }
        }

        // Save thread. Slots in ascending order, so the same table always gives the same bytes:
        // count, then per record unit ID, GlobalId, type, native maximum, written maximum (little endian).
        internal bool Capture(VersionedSaveWriter writer)
        {
            Record[] table = Snapshot();
            int count = 0;
            for (int slot = 1; slot < table.Length; slot++) if (table[slot].GlobalId != 0) count++;
            byte[] data;
            using (var stream = new MemoryStream(4 + count * RecordBytes))
            using (var output = new BinaryWriter(stream))
            {
                output.Write(count);
                for (int slot = 1; slot < table.Length; slot++)
                {
                    Record record = table[slot];
                    if (record.GlobalId == 0) continue;
                    output.Write(slot); output.Write(record.GlobalId); output.Write((ushort)record.UnitType);
                    output.Write(record.NativeMax); output.Write(record.AppliedMax);
                }
                output.Flush();
                data = stream.ToArray();
            }
            writer.Write(MaxHealthEntry, data);
            return true;
        }

        // Load thread, once per archive load: the table becomes the save's record, or empty for every
        // other result (no record, a map, damaged or unreadable data), so no marker outlives its mission.
        internal void Loaded(VersionedSaveLoad load)
        {
            Record[] restored = new Record[Math.Max(InitialSlots, Volatile.Read(ref records).Length)];
            if (load != null && load.Status == VersionedSaveLoadStatus.Restored &&
                load.Entries.TryGetValue(MaxHealthEntry, out byte[] data) && !TryDecode(data, ref restored))
                restored = new Record[restored.Length];
            Publish(restored);
        }

        private static bool TryDecode(byte[] data, ref Record[] table)
        {
            if (data == null || data.Length < 4) return false;
            int count = BitConverter.ToInt32(data, 0);
            if (count < 0 || data.Length != 4 + (long)count * RecordBytes) return false;
            for (int index = 0, at = 4; index < count; index++, at += RecordBytes)
            {
                int slot = BitConverter.ToInt32(data, at);
                if (slot <= 0 || slot >= MaximumUnitId) return false;
                if (slot >= table.Length)
                {
                    int size = table.Length;
                    while (size <= slot) size *= 2;
                    Array.Resize(ref table, size);
                }
                table[slot] = new Record
                {
                    GlobalId = BitConverter.ToInt32(data, at + 4),
                    UnitType = (eChimps)BitConverter.ToUInt16(data, at + 8),
                    NativeMax = BitConverter.ToUInt32(data, at + 10),
                    AppliedMax = BitConverter.ToUInt32(data, at + 14),
                };
            }
            return true;
        }

        internal bool TryGetRecord(int unitId, out Record record)
        {
            Record[] table = Snapshot();
            record = unitId > 0 && unitId < table.Length ? table[unitId] : default;
            return record.GlobalId != 0;
        }
    }
}
