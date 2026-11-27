using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.GameModes;
using APIShared.Simulation;
using SHCDESE.Interop;

namespace APIShared.Units
{
    /// <summary>Selects one side of an attacker/target pair. Null fields match any value.</summary>
    public readonly struct AttackTargetSelector
    {
        /// <summary>Combines all specified fields. Player zero selects neutral units.</summary>
        public AttackTargetSelector(int? playerId = null, eChimps? unitType = null, uint? globalId = null)
        {
            if (playerId < 0 || playerId > SHCDESE.API.GamePlayerManagerAPI.MAX_PLAYERS)
                throw new ArgumentOutOfRangeException(nameof(playerId));
            if (unitType.HasValue && !Enum.IsDefined(typeof(eChimps), unitType.Value))
                throw new ArgumentOutOfRangeException(nameof(unitType));
            if (globalId == 0) throw new ArgumentOutOfRangeException(nameof(globalId));
            PlayerId = playerId;
            UnitType = unitType;
            GlobalId = globalId;
        }

        /// <summary>Native controlling player, including zero for neutral units.</summary>
        public int? PlayerId { get; }
        /// <summary>Native unit type.</summary>
        public eChimps? UnitType { get; }
        /// <summary>Native unit identity within the current world, never a reusable slot number.</summary>
        public uint? GlobalId { get; }

        internal bool Matches(in AttackTargetUnit unit) =>
            (!PlayerId.HasValue || PlayerId.Value == unit.Player) &&
            (!UnitType.HasValue || UnitType.Value == unit.Type) &&
            (!GlobalId.HasValue || GlobalId.Value == unit.GlobalId);
    }

    /// <summary>Optional native condition on a unit-pair exclusion.</summary>
    public enum AttackTargetCondition
    {
        /// <summary>The rule applies whenever its world and selectors match.</summary>
        Always,
        /// <summary>The rule applies only while the native, save-preserved Peace Time flag is active.</summary>
        WhilePeaceTimeActive
    }

    /// <summary>An exclusion at the audited native unit-pair check.</summary>
    public readonly struct AttackTargetRule
    {
        /// <summary>Creates a rule matching the specified direction, or both directions if requested.</summary>
        public AttackTargetRule(AttackTargetSelector attacker, AttackTargetSelector target,
            bool bidirectional = false, AttackTargetCondition condition = AttackTargetCondition.Always)
        {
            if (condition != AttackTargetCondition.Always && condition != AttackTargetCondition.WhilePeaceTimeActive)
                throw new ArgumentOutOfRangeException(nameof(condition));
            Attacker = attacker;
            Target = target;
            Bidirectional = bidirectional;
            Condition = condition;
        }
        /// <summary>Selector for the attacker.</summary>
        public AttackTargetSelector Attacker { get; }
        /// <summary>Selector for the prospective target.</summary>
        public AttackTargetSelector Target { get; }
        /// <summary>Also tests the selectors in reversed order.</summary>
        public bool Bidirectional { get; }
        /// <summary>Optional native condition.</summary>
        public AttackTargetCondition Condition { get; }

        internal bool Matches(in AttackTargetUnit attacker, in AttackTargetUnit target) =>
            (Attacker.Matches(attacker) && Target.Matches(target)) ||
            (Bidirectional && Attacker.Matches(target) && Target.Matches(attacker));
    }

    /// <summary>Owns one process-lived rule registration.</summary>
    public sealed class AttackTargetRuleRegistration
    {
        internal AttackTargetRule[] Configured = Array.Empty<AttackTargetRule>();
        internal AttackTargetRule[] MissionRules = Array.Empty<AttackTargetRule>();
        internal MissionLifecycleState.NativeWorldState World;
        internal long Revision;
        internal AttackTargetRuleRegistration() { }

        /// <summary>Replaces this registration's initial configuration before a networked world starts.</summary>
        /// <remarks>Consumers must use identical, agreed settings on all peers. Configured rules cannot
        /// select world-specific global IDs. During a multiplayer mission use authenticated Chores for changes.
        /// An empty list clears the configured rules.</remarks>
        public bool TrySetConfiguredRules(IReadOnlyList<AttackTargetRule> rules, out string reason) =>
            AttackTargetRules.TrySetConfiguredRules(this, rules, out reason);

        /// <summary>Replaces this registration's rules for the current native world.</summary>
        /// <remarks>Use positive, strictly increasing revisions within the world. In multiplayer, call only
        /// from an authenticated Chore receiver after checking the issuer and gameplay permissions.
        /// Rebuild these rules on cold load using the current context and persisted, agreed mod state.</remarks>
        public bool TryReplaceRules(MissionContext mission, long revision, IReadOnlyList<AttackTargetRule> rules,
            out string reason) => AttackTargetRules.TryReplaceRules(this, mission, revision, rules, out reason);
    }

    /// <summary>Shared exclusions for the native unit-pair check used by audited melee and ranged paths.</summary>
    /// <remarks>This site is not a universal attack veto. Rules are combined by exclusion, with no
    /// override of Vanilla or another owner. The original predicate runs once if no rule matches.</remarks>
    public static class AttackTargetRules
    {
        private const int MaximumRulesPerRegistration = 4096;
        private static readonly object Gate = new object();
        private static readonly Dictionary<(string Owner, string Id), AttackTargetRuleRegistration> Registrations =
            new Dictionary<(string Owner, string Id), AttackTargetRuleRegistration>();
        private static AttackTargetRuleSnapshot snapshot = AttackTargetRuleSnapshot.Empty;

        /// <summary>Whether the native hook has passed installation and publication checks.</summary>
        public static bool IsAvailable => AttackTargetRuleNative.IsReady;

        /// <summary>Reserves an owner-local ID during startup, for the process lifetime.</summary>
        /// <remarks>Registration can precede native publication. Check IsAvailable before relying on
        /// exclusions. A published entry is never removed during gameplay or teardown.</remarks>
        public static bool TryRegister(string ownerGuid, string registrationId,
            out AttackTargetRuleRegistration registration, out string reason)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId))
            {
                reason = "Owner GUID and registration ID are required.";
                return false;
            }
            lock (Gate)
            {
                var key = (ownerGuid, registrationId);
                if (Registrations.ContainsKey(key))
                {
                    reason = "The owner and registration ID are already registered.";
                    return false;
                }
                if (!AttackTargetRuleNative.PrepareRegistration(ownerGuid, out reason)) return false;
                registration = new AttackTargetRuleRegistration();
                Registrations.Add(key, registration);
                return true;
            }
        }

        internal static void FinishStartup()
        {
            lock (Gate)
            {
                AttackTargetRuleNative.FinishStartup(Registrations.Count != 0);
                AttackTargetRuleNative.SetDemand(snapshot.Demand);
            }
        }

        internal static bool TrySetConfiguredRules(AttackTargetRuleRegistration registration,
            IReadOnlyList<AttackTargetRule> rules, out string reason)
        {
            if (!TryCopy(rules, true, out var replacement, out reason)) return false;
            lock (Gate)
            {
                var active = MissionLifecycleService.NativeClock?.Context;
                if (active != null && (active.Mode.IsRealMultiplayer || active.Mode.Kind == GameModeKind.Unknown))
                {
                    reason = "Configured rules cannot change during a multiplayer or unresolved world.";
                    return false;
                }
                registration.Configured = replacement;
                Publish();
                return true;
            }
        }

        internal static bool TryReplaceRules(AttackTargetRuleRegistration registration, MissionContext mission,
            long revision, IReadOnlyList<AttackTargetRule> rules, out string reason)
        {
            if (!TryCopy(rules, false, out var replacement, out reason)) return false;
            lock (Gate)
            {
                var clock = MissionLifecycleService.NativeClock;
                if (!CanReplace(mission, clock, registration.World, registration.Revision, revision,
                        SimulationMutationScope.IsAuthenticatedChore))
                {
                    reason = "Current mission, an increasing revision and a valid simulation context are required.";
                    return false;
                }
                registration.World = clock.World;
                registration.Revision = revision;
                registration.MissionRules = replacement;
                Publish();
                return true;
            }
        }

        internal static bool CanReplace(MissionContext mission, MissionLifecycleState.NativeClockState clock,
            MissionLifecycleState.NativeWorldState previous, long previousRevision, long revision, bool authenticatedChore) =>
            mission != null && clock != null && ReferenceEquals(mission, clock.Context) &&
            revision > 0 && (!ReferenceEquals(previous, clock.World) || revision > previousRevision) &&
            (clock.Context.Mode.IsRealMultiplayer ? authenticatedChore : clock.Context.Mode.Kind != GameModeKind.Unknown);

        internal static void Retire(long session)
        {
            lock (Gate)
            {
                foreach (var registration in Registrations.Values)
                {
                    if (registration.World?.Mission != session) continue;
                    registration.World = null;
                    registration.Revision = 0;
                    registration.MissionRules = Array.Empty<AttackTargetRule>();
                }
                Publish();
            }
        }

        private static bool TryCopy(IReadOnlyList<AttackTargetRule> rules, bool configured,
            out AttackTargetRule[] copy, out string reason)
        {
            copy = null;
            if (rules == null || rules.Count > MaximumRulesPerRegistration)
            {
                reason = "A non-null list of at most 4096 rules is required.";
                return false;
            }
            var replacement = new AttackTargetRule[rules.Count];
            for (int index = 0; index < replacement.Length; index++)
            {
                var rule = rules[index];
                if (configured && (rule.Attacker.GlobalId.HasValue || rule.Target.GlobalId.HasValue))
                {
                    reason = "Unit global IDs must be scoped to a native world.";
                    return false;
                }
                replacement[index] = rule;
            }
            copy = replacement;
            reason = string.Empty;
            return true;
        }

        private static void Publish()
        {
            var next = new AttackTargetRuleSnapshot(Registrations.Values);
            Volatile.Write(ref snapshot, next);
            AttackTargetRuleNative.SetDemand(next.Demand);
        }

        internal static bool Excludes(in AttackTargetUnit attacker, in AttackTargetUnit target, bool peace) =>
            Volatile.Read(ref snapshot).Excludes(attacker, target, peace, MissionLifecycleService.NativeWorld);
    }

    internal readonly struct AttackTargetUnit
    {
        internal readonly int Player;
        internal readonly eChimps Type;
        internal readonly uint GlobalId;
        internal AttackTargetUnit(int player, eChimps type, uint globalId)
        { Player = player; Type = type; GlobalId = globalId; }
    }

    internal sealed class AttackTargetRuleSnapshot
    {
        private readonly (AttackTargetRule Rule, MissionLifecycleState.NativeWorldState World)[] rules;
        internal static readonly AttackTargetRuleSnapshot Empty =
            new AttackTargetRuleSnapshot(Array.Empty<AttackTargetRuleRegistration>());
        internal int Demand { get; }
        internal AttackTargetRuleSnapshot(IEnumerable<AttackTargetRuleRegistration> registrations)
        {
            var entries = new List<(AttackTargetRule, MissionLifecycleState.NativeWorldState)>();
            foreach (var registration in registrations)
            {
                foreach (var rule in registration.Configured) entries.Add((rule, null));
                foreach (var rule in registration.MissionRules) entries.Add((rule, registration.World));
            }
            rules = entries.ToArray();
            foreach (var entry in rules)
                Demand |= entry.Rule.Condition == AttackTargetCondition.Always ? 1 : 2;
        }
        internal bool Excludes(in AttackTargetUnit attacker, in AttackTargetUnit target, bool peace,
            MissionLifecycleState.NativeWorldState world)
        {
            foreach (var entry in rules)
            {
                if ((entry.World == null || ReferenceEquals(entry.World, world)) &&
                    (entry.Rule.Condition == AttackTargetCondition.Always || peace) &&
                    entry.Rule.Matches(attacker, target))
                    return true;
            }
            return false;
        }
    }
}
