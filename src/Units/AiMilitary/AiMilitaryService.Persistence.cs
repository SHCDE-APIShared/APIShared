using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using APIShared.Savegames;
using APIShared.Units.AiMilitary.Native;
using APIShared.Units.AiMilitary.Quota;
using APIShared.Units.AiMilitary.Engineering;

namespace APIShared.Units.AiMilitary
{
    internal sealed unsafe partial class AiMilitaryService
    {
        private long worldSession;
        private PreparedState prepared;
        private bool rawRestored;
        private bool rawRestoreFailed;
        private VersionedSaveDataHandle saveHandle;
        private int observing;

        private void InitializeNativeObservers(CrusaderLibraryLoadContext context, string hash) =>
            AiMilitaryNativeObservers.Initialize(context, hash, log, OnRawRestored, ObserveStrategic);

        private void RegisterPersistence()
        {
            var registration = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, "APIShared_AiMilitary", 1, 1,
                new[] { "units", "additional", "quota", "orders" }, CaptureState, OnLoaded)
            { Scope = VersionedSaveScope.SaveGamesAndMaps, SimulationRelevant = true, Prepared = OnPrepared };
            if (!VersionedSaveData.TryRegister(registration, out saveHandle, out string reason))
                throw new InvalidOperationException("AI military save registration failed: " + reason);
        }

        private bool CaptureState(VersionedSaveWriter writer)
        {
            lock (engine)
            {
                AiMilitaryEngineeringRuntime.Reconcile();
                var units = new List<SaveUnit>();
                foreach (var entry in tracked.Values)
                {
                    if (!TryUnit(entry.Unit, out GameUnit* unit) || !AiMilitarySaveLifetime.CanCapture(in *unit)) continue;
                    units.Add(new SaveUnit { Owner = entry.Owner, Identity = entry.Unit, Player = unit->r_ControllableForPlayerId,
                        Type = unit->r_UnitChimp, Assignment = entry.Assignment, Declaration = entry.Declaration,
                        Pending = entry.Pending, Protected = entry.Protected, Private = entry.Private,
                        Status = entry.Last?.Status ?? AiMilitaryAssignmentStatus.Declared,
                        Reason = entry.Last?.Reason ?? AiMilitaryReason.None });
                }
                units.Sort((a, b) => a.Identity.UnitId.CompareTo(b.Identity.UnitId));
                writer.Write("units", EncodeUnits(units));
                writer.Write("additional", CaptureAdditional());
                writer.Write("quota", AiMilitaryQuotaRuntime.Capture());
                writer.Write("orders", orders.Capture());
                return true; // Explicit empty records retire old data too.
            }
        }

        private void OnPrepared(VersionedSaveLoad load)
        {
            try { PrepareState(load); }
            catch
            {
                // The archive framework preserves unreadable original bytes. It cannot permit
                // this simulation-relevant record to run without the required early protection.
                prepared = null; rawRestoreFailed = true;
                throw;
            }
        }
        private void PrepareState(VersionedSaveLoad load)
        {
            // No game access: the archive describes a world which has not been restored yet.
            if (load.Status == VersionedSaveLoadStatus.NoRecord || load.Status == VersionedSaveLoadStatus.OutOfScope)
            { prepared = new PreparedState(load.Source, new List<SaveUnit>(), new List<SaveUnit>(), null, null); return; }
            if (load.Status != VersionedSaveLoadStatus.Restored)
                throw new InvalidDataException("The AI military record cannot be prepared: " + load.Status);
            if (!load.Entries.TryGetValue("units", out byte[] units) || !load.Entries.TryGetValue("additional", out byte[] flags))
                throw new InvalidDataException("The AI military record lacks its identity entries.");
            if (!load.Entries.TryGetValue("quota", out byte[] quota) || !load.Entries.TryGetValue("orders", out byte[] orderBytes))
                throw new InvalidDataException("The AI military record lacks its quota/order entries.");
            prepared = new PreparedState(load.Source, DecodeUnits(units), DecodeAdditional(flags), quota, orderBytes);
        }
        private void OnLoaded(VersionedSaveLoad load)
        {
            // Prepared has the immutable candidate set. Application is owned by the native-restore
            // participant, not this ordinary Post callback (which must retain its existing timing).
            if ((prepared == null || rawRestoreFailed) && load.Status == VersionedSaveLoadStatus.Restored)
                throw new InvalidOperationException("Early AI military save preparation did not execute; loaded bytes are preserved.");
        }

        internal bool DidRawRestoreFail => rawRestoreFailed;
        private bool OnRawRestored()
        {
            // Sticky for the entire load attempt, including SE's exceptional retry of Original.
            if (rawRestoreFailed) return false;
            if (!ready || prepared == null) return true;
            long restoreGeneration = generation;
            try
            {
                if (!Monitor.IsEntered(engine)) throw new InvalidOperationException("Raw restore lacks the engine monitor.");
                rawRestored = true;
                bool authoredMap = prepared.Source == VersionedSaveLoadSource.MapToPlay;
                bool editor = prepared.Source == VersionedSaveLoadSource.EditorMap;
                // Phase 1 performs no native detach. Every owned declaration is protected before
                // any foreign hook can veto one of the subsequent native membership operations.
                foreach (var saved in prepared.Units)
                {
                    if (!ResolveSaved(saved, out AiUnitRef identity, out GameUnit* unit) ||
                        !UnitAccess.IsReallyAlive(unit) && unit->r_AliveState != AliveState.NeedsInit) continue;
                    var entry = new Tracked(saved.Owner, identity, unit->r_ControllableForPlayerId, unit->r_UnitChimp,
                        saved.Assignment, false, saved.Declaration, null)
                    { Pending = !editor && (authoredMap || saved.Pending), Protected = saved.Protected, Private = saved.Private };
                    tracked[identity.UnitId] = entry;
                    if ((authoredMap || !editor && entry.Pending) && AiMilitaryCatalog.IsMilitary(entry.Type)) Quarantine(entry, unit);
                    AiGroupRef restoredGroup = default;
                    if (!editor && !entry.Pending && saved.Status == AiMilitaryAssignmentStatus.Applied &&
                        TryGroup(unit->r_TribeId, out var currentGroup) && backend.HasMember(currentGroup, identity.UnitId))
                        restoredGroup = new AiGroupRef(currentGroup.Id, currentGroup.GlobalId, currentGroup.Owner);
                    entry.Last = Result(entry, editor ? AiMilitaryAssignmentStatus.Declared : entry.Pending ?
                        AiMilitaryAssignmentStatus.Queued : saved.Status, saved.Reason, string.Empty, restoredGroup);
                }
                foreach (var saved in prepared.Additional)
                    if (ResolveSaved(saved, out AiUnitRef identity, out _)) MarkAdditional(identity, true);
                // Activate markers before any possible natural fold; a snapshot restore never
                // overwrites a newer natural fold and never rewrites the native count vector.
                if (prepared.Quota != null) AiMilitaryQuotaRuntime.Restore(prepared.Quota);
                if (!authoredMap && !editor && prepared.Orders != null) orders.Restore(prepared.Orders, generation);
                bool failed = false;
                foreach (var entry in new List<Tracked>(tracked.Values))
                {
                    if (!entry.Protected || !TryUnit(entry.Unit, out GameUnit* unit) || !UnitAccess.IsReallyAlive(unit)) continue;
                    if (authoredMap || entry.Pending)
                    {
                        int previous = permittedUnit; permittedUnit = entry.Unit.UnitId;
                        try
                        {
                            if (!backend.TryDetach(entry.Unit.UnitId, entry.Unit.GlobalId, out string reason))
                            {
                                if (generation != restoreGeneration) return false;
                                failed = true; entry.Pending = false;
                                entry.Last = Result(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.NativeVeto, reason);
                            }
                        }
                        finally { permittedUnit = previous; }
                    }
                    // A rejected map declaration stays protected without a private group
                    // (CompleteNativeLoad, Finish); only an actual group claim can be inconsistent.
                    else if (!editor && entry.Private.Id != 0 && GamePlayerManagerAPI.Instance.IsAIPlayer(entry.Player) &&
                        (!backend.IsCurrent(entry.Private) || !backend.HasMember(entry.Private, entry.Unit.UnitId) ||
                            unit->r_TribeId != entry.Private.Id || unit->r_AITribeRole != AiMilitaryNativeBackend.UnmanagedRole))
                    {
                        failed = true;
                        entry.Last = Result(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.AssignmentFailed,
                            "The saved private protection group is inconsistent.");
                    }
                }
                if (failed) throw new InvalidOperationException("The loaded army protection cannot be established; native initialization was refused.");
                if (tracked.Count != 0 || prepared.Additional.Count != 0) EnableObservation();
                return true;
            }
            catch (Exception error)
            {
                if (generation == restoreGeneration) { rawRestoreFailed = true; rawRestored = false; }
                Report("raw-restore-admission", error);
                return false;
            }
        }

        // Called from MissionLifecycleService inside an outer hold of the actual EngineInterface
        // monitor, immediately after the full native wrapper and before public NativeLoaded observers.
        internal void CompleteNativeLoad()
        {
            if (!ready || rawRestoreFailed || !rawRestored || prepared == null) return;
            if (!Monitor.IsEntered(engine)) throw new InvalidOperationException("AI load reconciliation lacks its outer engine monitor.");
            bool authoredMap = prepared.Source == VersionedSaveLoadSource.MapToPlay;
            if (authoredMap)
            {
                var entries = new List<Tracked>(tracked.Values);
                foreach (var old in entries)
                {
                    if (!TryUnit(old.Unit, out GameUnit* unit)) { tracked.Remove(old.Unit.UnitId); continue; }
                    var current = new Tracked(old.Owner, old.Unit, unit->r_ControllableForPlayerId, unit->r_UnitChimp,
                        old.Assignment, false, true, null) { Pending = true, Protected = old.Protected };
                    tracked[old.Unit.UnitId] = current;
                    // Queued until the work pump applies it; TryGetState must not report it as untracked.
                    current.Last = Result(current, AiMilitaryAssignmentStatus.Queued, AiMilitaryReason.None, string.Empty);
                    if (!GamePlayerManagerAPI.Instance.IsAIPlayer(current.Player))
                    {
                        current.Protected = false; current.Pending = false;
                        // Human ownership is authoritative; do not leave an AI-only quarantine role.
                        unit->r_AITribeRole = 0;
                        current.Last = Result(current, AiMilitaryAssignmentStatus.Declared, AiMilitaryReason.NoAiContext,
                            "The final owner is human; the map declaration is retained.");
                        continue;
                    }
                    var option = AiMilitaryCatalog.Option(current.Player, current.Type, current.Assignment.Role, false, unit);
                    if (!option.IsAvailable)
                    {
                        current.Pending = false;
                        current.Last = Result(current, AiMilitaryAssignmentStatus.Rejected, option.Reason, option.Detail);
                        NativeApiLog.Warning(log, "AI_MAP_ASSIGNMENT_NOT_APPLIED: unit=" + current.Unit + ",reason=" + option.Reason + "; protected declaration retained.");
                    }
                }
            }
            rawRestored = false;
            // Pending restored units need explicit requeue: UnitSettledEvents has no save-load replay.
            foreach (var entry in tracked.Values) if (entry.Pending) { Wake(); break; }
            prepared = null;
        }

        private void EnableObservation()
        { Volatile.Write(ref observing, 1); AiMilitaryNativeObservers.EnableObservation(); }
        private bool ResolveSaved(SaveUnit saved, out AiUnitRef identity, out GameUnit* unit)
        {
            int id = GameUnitManagerAPI.Instance.GetByGlobalId(unchecked((int)saved.Identity.GlobalId));
            identity = new AiUnitRef(id, saved.Identity.GlobalId);
            return TryUnit(identity, out unit) && unit->r_UnitChimp == saved.Type &&
                unit->r_ControllableForPlayerId == saved.Player;
        }

        private byte[] CaptureAdditional()
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                var units = new List<SaveUnit>();
                for (int id = 1; id < UnitIdLimit; id++)
                {
                    uint global = Volatile.Read(ref additional[id]);
                    if (global == 0 || !TryUnit(new AiUnitRef(id, global), out GameUnit* unit) ||
                        !AiMilitarySaveLifetime.CanCapture(in *unit)) continue;
                    units.Add(new SaveUnit { Identity = new AiUnitRef(id, global), Type = unit->r_UnitChimp, Player = unit->r_ControllableForPlayerId });
                }
                writer.Write(1); writer.Write(units.Count);
                foreach (var unit in units) WriteIdentity(writer, unit);
                return stream.ToArray();
            }
        }
        private static byte[] EncodeUnits(List<SaveUnit> units)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); writer.Write(units.Count);
                foreach (var unit in units)
                {
                    WriteIdentity(writer, unit); writer.Write(unit.Owner);
                    writer.Write((byte)unit.Assignment.Role); writer.Write((byte)unit.Assignment.RecruitmentMode);
                    writer.Write(unit.Declaration); writer.Write(unit.Pending); writer.Write(unit.Protected);
                    writer.Write((byte)unit.Status); writer.Write((byte)unit.Reason);
                    writer.Write(unit.Private.Id); writer.Write(unit.Private.GlobalId); writer.Write(unit.Private.Owner);
                }
                return stream.ToArray();
            }
        }
        private static void WriteIdentity(BinaryWriter writer, SaveUnit unit)
        { writer.Write(unit.Identity.UnitId); writer.Write(unit.Identity.GlobalId); writer.Write((ushort)unit.Type); writer.Write((byte)unit.Player); }
        private static SaveUnit ReadIdentity(BinaryReader reader)
        {
            int id = reader.ReadInt32(); uint global = reader.ReadUInt32(); var type = (eChimps)reader.ReadUInt16(); int player = reader.ReadByte();
            if (id < 1 || id >= UnitIdLimit || global == 0 || player < 1 || player > GamePlayerManagerAPI.MAX_PLAYERS ||
                !Enum.IsDefined(typeof(eChimps), type)) throw new InvalidDataException("Invalid saved military identity.");
            return new SaveUnit { Identity = new AiUnitRef(id, global), Type = type, Player = player };
        }
        private static List<SaveUnit> DecodeUnits(byte[] bytes) => Decode(bytes, true);
        private static List<SaveUnit> DecodeAdditional(byte[] bytes) => Decode(bytes, false);
        private static List<SaveUnit> Decode(byte[] bytes, bool assignments)
        {
            var result = new List<SaveUnit>(); var seen = new HashSet<uint>();
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported army identity schema.");
                int count = reader.ReadInt32(); if (count < 0 || count >= UnitIdLimit) throw new InvalidDataException("Invalid army identity count.");
                for (int i = 0; i < count; i++)
                {
                    var unit = ReadIdentity(reader); if (!seen.Add(unit.Identity.GlobalId)) throw new InvalidDataException("Duplicate saved military identity.");
                    if (assignments)
                    {
                        unit.Owner = reader.ReadString();
                        var role = (AiMilitaryRole)reader.ReadByte(); var mode = (AiRecruitmentMode)reader.ReadByte();
                        if (string.IsNullOrWhiteSpace(unit.Owner) || unit.Owner.Length > 256 ||
                            !Enum.IsDefined(typeof(AiMilitaryRole), role) || !Enum.IsDefined(typeof(AiRecruitmentMode), mode))
                            throw new InvalidDataException("Invalid army assignment.");
                        unit.Assignment = new AiMilitaryAssignment(role, mode);
                        unit.Declaration = reader.ReadBoolean(); unit.Pending = reader.ReadBoolean(); unit.Protected = reader.ReadBoolean();
                        unit.Status = (AiMilitaryAssignmentStatus)reader.ReadByte(); unit.Reason = (AiMilitaryReason)reader.ReadByte();
                        if (!Enum.IsDefined(typeof(AiMilitaryAssignmentStatus), unit.Status) || !Enum.IsDefined(typeof(AiMilitaryReason), unit.Reason))
                            throw new InvalidDataException("Invalid saved assignment status.");
                        int tribe = reader.ReadInt32(); uint gid = reader.ReadUInt32(); int owner = reader.ReadInt32();
                        if (tribe < 0 || tribe >= AiMilitaryNativeBackend.TribeCapacity || owner < 0 || owner > 8 ||
                            tribe == 0 && (gid != 0 || owner != 0) || tribe != 0 && (gid == 0 || owner == 0))
                            throw new InvalidDataException("Invalid private army group reference.");
                        unit.Private = new AiNativeGroup(tribe, gid, owner);
                    }
                    result.Add(unit);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing army identity data.");
            }
            return result;
        }
        private sealed class SaveUnit
        {
            internal string Owner;
            internal AiUnitRef Identity;
            internal int Player;
            internal eChimps Type;
            internal AiMilitaryAssignment Assignment;
            internal bool Declaration, Pending, Protected;
            internal AiMilitaryAssignmentStatus Status;
            internal AiMilitaryReason Reason;
            internal AiNativeGroup Private;
        }
        private sealed class PreparedState
        {
            internal PreparedState(VersionedSaveLoadSource source, List<SaveUnit> units, List<SaveUnit> additional, byte[] quota, byte[] orders)
            { Source = source; Units = units; Additional = additional; Quota = quota; Orders = orders; }
            internal readonly VersionedSaveLoadSource Source;
            internal readonly List<SaveUnit> Units, Additional;
            internal readonly byte[] Quota, Orders;
        }
    }
}
