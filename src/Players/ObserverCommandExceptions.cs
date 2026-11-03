using APIShared.Events;
using CrusaderDE;
using System.Threading;

namespace APIShared
{
    /// <summary>One local GameAction of a confirmed observer that an enforcing mod would otherwise veto.</summary>
    /// <remarks>An eliminated participant keeps its positive player ID while Vanilla's native viewpoint can
    /// show another player. Vanilla's local command queue takes the network source from that viewpoint, so an
    /// allowed command acts as <see cref="ViewedPlayerId"/>, not as <see cref="ObserverPlayerId"/>.</remarks>
    public readonly struct ObserverCommandRequest
    {
        /// <summary>Creates a request; enforcing mods pass the GameAction payloads unchanged.</summary>
        public ObserverCommandRequest(int observerPlayerId, int viewedPlayerId, Enums.GameActionCommand command,
            int structureId, int actionState, int value2)
        {
            ObserverPlayerId = observerPlayerId; ViewedPlayerId = viewedPlayerId; Command = command;
            StructureId = structureId; ActionState = actionState; Value2 = value2;
        }
        /// <summary>The observer's own positive one-based player ID.</summary>
        public int ObserverPlayerId { get; }
        /// <summary>Vanilla's current native viewpoint (one-based); the source of a queued command.</summary>
        public int ViewedPlayerId { get; }
        /// <summary>Requested GameAction.</summary>
        public Enums.GameActionCommand Command { get; }
        /// <summary>First GameAction payload.</summary>
        public int StructureId { get; }
        /// <summary>Second GameAction payload.</summary>
        public int ActionState { get; }
        /// <summary>Third GameAction payload.</summary>
        public int Value2 { get; }
    }

    /// <summary>Returns true to let the observer issue this one command; false gives no opinion.</summary>
    public delegate bool ObserverCommandException(ObserverCommandRequest request);

    /// <summary>
    /// Cross-mod exceptions for mods that block local commands of confirmed observers, such as eliminated
    /// participants viewing another player. APIShared enforces nothing itself; the enforcing mod asks
    /// <see cref="IsAllowed"/> before its veto.
    /// </summary>
    /// <remarks>
    /// Callbacks run synchronously on the enforcing mod's caller thread, which is Unity's main thread for
    /// local input, outside registration locks. Enforcing mods offer only local-input commands they would
    /// block; synchronized simulation calls must never be blocked. Ordering: order, ordinal owner GUID,
    /// ordinal ID; the first true allows the command and skips later callbacks. A throwing callback is
    /// isolated, counted and treated as no opinion. An allowed command follows Vanilla's normal path with the
    /// native viewpoint as its network source, i.e. it acts as the viewed player; that permission is the
    /// registering mod's responsibility. Registrations live for the process; there is no unregister.
    /// Without registrations <see cref="IsAllowed"/> returns false without allocation.
    /// </remarks>
    public static class ObserverCommandExceptions
    {
        internal static readonly ObserverCommandRegistry Registry = new ObserverCommandRegistry();

        /// <summary>Registers an exception once; false with a reason on invalid or duplicate identity.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, ObserverCommandException allow,
            out string reason, int order = 0) => Registry.TryRegister(ownerGuid, registrationId, allow, out reason, order);

        /// <summary>Whether any registered exception allows this request. Called by enforcing mods.</summary>
        public static bool IsAllowed(ObserverCommandRequest request) => Registry.IsAllowed(request);

        /// <summary>Total isolated callback exceptions.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;
    }

    internal sealed class ObserverCommandRegistry
    {
        private readonly OwnedCallbacks<ObserverCommandException> callbacks = new OwnedCallbacks<ObserverCommandException>();
        private long failures;

        internal long CallbackFailures => Interlocked.Read(ref failures);

        internal bool TryRegister(string owner, string id, ObserverCommandException allow, out string reason, int order) =>
            callbacks.TryAdd(owner, id, allow, out reason, order);

        internal bool IsAllowed(ObserverCommandRequest request)
        {
            var snapshot = callbacks.Snapshot;
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { if (snapshot[i].Callback(request)) return true; }
                catch { Interlocked.Increment(ref failures); }
            }
            return false;
        }
    }
}
