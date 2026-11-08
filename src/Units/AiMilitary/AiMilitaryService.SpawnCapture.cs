using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Tribes;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using APIShared.Units.AiMilitary.Native;
using APIShared.Units.AiMilitary.Orders;
using APIShared.Units.AiMilitary.Quota;
using APIShared.Units.AiMilitary.Engineering;

namespace APIShared.Units.AiMilitary
{
    internal sealed unsafe partial class AiMilitaryService
    {
        private bool Begin(string owner, int player, eChimps type, AiMilitaryAssignment assignment,
            bool editor, bool apply, Action<AiMilitaryAssignmentResult> completed, out IAiMilitarySpawnScope scope, out string reason)
        {
            scope = null; reason = string.Empty;
            if (!ready || worldChanging || !Monitor.IsEntered(engine)) { reason = "AI placement requires the validated engine monitor and an active world."; return false; }
            if (GameModes.GameModeHelper.IsRealMultiplayer()) { reason = "Local army placement is unavailable in multiplayer."; return false; }
            if (editor != GameModes.GameModeHelper.IsMapEditor()) { reason = "Editor mode changed."; return false; }
            if (player < 1 || player > GamePlayerManagerAPI.MAX_PLAYERS ||
                !Enum.IsDefined(typeof(AiRecruitmentMode), assignment.RecruitmentMode))
            { reason = "Invalid player or recruitment mode."; return false; }
            var option = AiMilitaryCatalog.Option(player, type, assignment.Role, editor);
            if (!option.IsAvailable) { reason = option.Reason + ": " + option.Detail; return false; }
            var created = new SpawnScope(this, owner, player, type, assignment, editor, apply, completed, capture);
            capture = created; scope = created;
            return true;
        }

        private void OnCreated(int unitId)
        {
            try
            {
                if (unitId <= 0 || unitId >= 10000 || !UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) || unit->r_GlobalId == 0) return;
                var identity = new AiUnitRef(unitId, unit->r_GlobalId);
                if (tracked.TryGetValue(identity.UnitId, out var old) && !old.Unit.Equals(identity))
                {
                    MarkAdditional(old.Unit, false); tracked.Remove(identity.UnitId);
                    if (old.Pending)
                    {
                        old.Pending = false;
                        old.Last = Result(old, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.StaleIdentity,
                            "The pending unit slot was reused by another native identity.");
                        retired.Add(old); Wake();
                    }
                }
                AiMilitaryEngineeringRuntime.OnUnitCreated(identity, unit->r_UnitChimp);
                SpawnScope current = capture;
                if (current == null || current.Generation != generation || current.Player != unit->r_ControllableForPlayerId ||
                    current.Type != unit->r_UnitChimp) return;
                current.Created.Add(identity);
            }
            catch (Exception error) { Report("create", error); }
        }

        private long FilterCapturedCreateReturn(long raw, long result)
        {
            SpawnScope scope = capture;
            if (scope == null) return result;
            if (scope.Generation != generation) return result; // A nested new-world load owns a newer ambient receipt.
            if (raw <= 0 || raw >= 10000 || !UnitAccess.TryGetById((int)raw, out GameUnit* unit, out _))
            {
                // A foreign Pre may supply an existing primary unit without invoking Original.
                // Non-primary helper calls remain outside the capture contract.
                if (result > 0 && result < 10000 && UnitAccess.TryGetById((int)result, out GameUnit* supplied, out _) &&
                    supplied->r_UnitChimp == scope.Type && supplied->r_ControllableForPlayerId == scope.Player) return 0;
                return result;
            }
            if (unit->r_UnitChimp != scope.Type || unit->r_ControllableForPlayerId != scope.Player) return result;
            if (raw != result) return 0;
            return scope.Created.Contains(new AiUnitRef((int)raw, unit->r_GlobalId)) ? result : 0;
        }

        private sealed class SpawnScope : IAiMilitarySpawnScope
        {
            private readonly AiMilitaryService service;
            private readonly string owner;
            internal readonly long Generation;
            internal readonly int Player;
            internal readonly eChimps Type;
            private readonly AiMilitaryAssignment assignment;
            private readonly bool editor, apply;
            private readonly Action<AiMilitaryAssignmentResult> completed;
            private readonly SpawnScope previous;
            private readonly int thread;
            private readonly IDisposable mutation;
            internal readonly List<AiUnitRef> Created = new List<AiUnitRef>();
            private bool complete, disposed;
            internal SpawnScope(AiMilitaryService service, string owner, int player, eChimps type, AiMilitaryAssignment assignment,
                bool editor, bool apply, Action<AiMilitaryAssignmentResult> completed, SpawnScope previous)
            { this.service = service; this.owner = owner; Player = player; Type = type; this.assignment = assignment;
                this.editor = editor; this.apply = apply; this.completed = completed; this.previous = previous;
                Generation = service.generation; thread = Thread.CurrentThread.ManagedThreadId;
                mutation = AiRecruitMutationReceipts.Enter(() => Generation == service.generation); }
            public IReadOnlyList<AiMilitaryAssignmentResult> Complete()
            {
                if (disposed || complete || thread != Thread.CurrentThread.ManagedThreadId || capture != this ||
                    !Monitor.IsEntered(service.engine)) throw new InvalidOperationException("Placement scope order/thread/monitor differs.");
                complete = true;
                var results = new List<AiMilitaryAssignmentResult>(Created.Count);
                foreach (var unit in Created)
                {
                    if (Generation == service.generation && service.Request(owner, unit, assignment, completed, true,
                        editor && !apply, out string reason, out AiMilitaryAssignmentResult initial))
                        results.Add(initial);
                    else
                    {
                        var rejected = new AiMilitaryAssignmentResult(unit, assignment, AiMilitaryAssignmentStatus.Rejected,
                            AiMilitaryReason.AssignmentFailed, "The captured unit could not be submitted.");
                        results.Add(rejected);
                        if (Generation == service.generation) DeleteOwnUnit(unit);
                        try { completed?.Invoke(rejected); } catch (Exception error) { service.Report("capture-completion", error); }
                    }
                }
                return results.AsReadOnly();
            }
            public void Dispose()
            {
                if (disposed) return;
                if (thread != Thread.CurrentThread.ManagedThreadId || capture != this)
                    throw new InvalidOperationException("Placement capture must close on its owning thread in stack order.");
                if (!Monitor.IsEntered(service.engine)) throw new InvalidOperationException("Placement capture must close under the engine monitor.");
                capture = previous; disposed = true;
                try
                {
                    if (!complete && Generation == service.generation)
                        foreach (var unit in Created) DeleteOwnUnit(unit);
                }
                finally { mutation.Dispose(); }
            }
        }
    }
}
