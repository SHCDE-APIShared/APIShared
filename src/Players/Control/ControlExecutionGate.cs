using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace APIShared.PlayerControl
{
    // Only the native backend implements these rules. A packet never supplies its
    // own required rights, and a consumer callback cannot relabel destructive work.
    internal interface IControlCommandRules
    {
        bool TryValidate(ControlNativeCommand command, ReadOnlyCollection<PlayerControlObject> objects,
            out PlayerControlRights required);
    }

    internal delegate bool ResolveControlObject(PlayerControlObject captured, out PlayerControlObject live);

    internal sealed class ControlOwnerCommands
    {
        internal ControlOwnerCommands(int owner, List<ControlNativeCommand> commands)
        { Owner = owner; Commands = Array.AsReadOnly(commands.ToArray()); }
        internal int Owner { get; }
        internal ReadOnlyCollection<ControlNativeCommand> Commands { get; }
    }

    internal sealed class ControlExecutionPlan
    {
        internal ControlExecutionPlan(long session, int issuer, ControlCommandBatch batch, List<ControlOwnerCommands> groups)
        { Session = session; Issuer = issuer; Objects = batch.Objects; Groups = Array.AsReadOnly(groups.ToArray()); }
        internal long Session { get; }
        internal int Issuer { get; }
        internal ReadOnlyCollection<PlayerControlObject> Objects { get; }
        internal ReadOnlyCollection<ControlOwnerCommands> Groups { get; }
    }

    // WIP (see docs/PLAYER_CONTROL.md): no production IControlCommandRules/resolver exists yet.
    // A synchronized-context admission boundary. No native side effects until EVERY
    // command and identity has passed; then one backend call processes the whole batch.
    // The same gate is used for submission preflight and fresh execution admission.
    internal sealed class ControlExecutionGate
    {
        private readonly ControlGrantTable grants;
        private readonly IControlCommandRules rules;
        private readonly ResolveControlObject resolve;
        private readonly Func<long, bool> sessionReady;
        private bool executing;

        internal ControlExecutionGate(ControlGrantTable grants, IControlCommandRules rules,
            ResolveControlObject resolve, Func<long, bool> sessionReady)
        {
            this.grants = grants ?? throw new ArgumentNullException(nameof(grants));
            this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
            this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            this.sessionReady = sessionReady ?? throw new ArgumentNullException(nameof(sessionReady));
        }

        internal bool TryPrepare(long session, string registration, long revision, int authenticatedIssuer,
            ControlCommandBatch batch, out ControlExecutionPlan plan, out string reason)
        {
            plan = null; reason = null;
            if (batch == null || !sessionReady(session)) { reason = "Control session is not ready."; return false; }
            var slots = new HashSet<long>();
            foreach (var item in batch.Objects)
            {
                // Reject repeated slots even when a forged packet changes their owner
                // or global ID. Targets may be foreign but never stale/recycled.
                long slot = ((long)item.Kind << 32) | (uint)item.Slot;
                if (item.Slot < 1 || item.GlobalId == 0 || item.OwnerPlayerId < 1 || item.OwnerPlayerId > 8 ||
                    (item.Kind != PlayerControlObjectKind.Unit && item.Kind != PlayerControlObjectKind.Building) ||
                    !slots.Add(slot) || !resolve(item, out var current) || !current.Equals(item))
                { reason = "Object identity or ownership changed."; return false; }
            }
            var byOwner = new List<ControlNativeCommand>[9];
            var requirements = new PlayerControlRights[9];
            foreach (var command in batch.Commands)
            {
                if (!rules.TryValidate(command, batch.Objects, out var required) || required == PlayerControlRights.None ||
                    (required & ~PlayerControlRights.All) != 0 ||
                    !grants.Allows(session, registration, revision, authenticatedIssuer, command.Owner, required))
                { reason = "Command or delegated right rejected."; return false; }
                if (byOwner[command.Owner] == null) byOwner[command.Owner] = new List<ControlNativeCommand>();
                byOwner[command.Owner].Add(command); requirements[command.Owner] |= required;
            }
            // Resolver/rule evaluation must not let a reentrant session/grant change
            // turn an earlier permission check into a stale execution ticket.
            if (!sessionReady(session)) { reason = "Control session changed."; return false; }
            var groups = new List<ControlOwnerCommands>();
            for (int owner = 1; owner <= 8; owner++)
            {
                if (byOwner[owner] == null) continue;
                if (!grants.Allows(session, registration, revision, authenticatedIssuer, owner, requirements[owner]))
                { reason = "Control grant changed."; return false; }
                groups.Add(new ControlOwnerCommands(owner, byOwner[owner]));
            }
            plan = new ControlExecutionPlan(session, authenticatedIssuer, batch, groups);
            return true;
        }

        internal bool TryExecute(long session, string registration, long revision, int authenticatedIssuer,
            ControlCommandBatch batch, Action<ControlExecutionPlan> execute, out string reason)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            reason = null;
            if (executing) { reason = "Recursive delegated execution rejected."; return false; }
            executing = true;
            try
            {
                if (!TryPrepare(session, registration, revision, authenticatedIssuer, batch, out var plan, out reason)) return false;
                execute(plan);
                return true;
            }
            finally { executing = false; }
        }
    }
}
