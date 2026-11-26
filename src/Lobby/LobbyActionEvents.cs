using System;
using APIShared.Events;
using APIShared.Internal;

namespace APIShared.Lobby
{
    /// <summary>Immutable observation of one local lobby button command. It is not an admission receipt.</summary>
    public sealed class LobbyActionNotification
    {
        internal LobbyActionNotification(ulong lobbyId, string command, bool readyBefore,
            bool readyAfter, bool originalInvoked)
        {
            LobbyId = lobbyId; Command = command; ReadyBefore = readyBefore;
            ReadyAfter = readyAfter; OriginalInvoked = originalInvoked;
        }
        /// <summary>Lobby identity captured before dispatch, or zero without a lobby.</summary>
        public ulong LobbyId { get; }
        /// <summary>Unmodified Vanilla command string.</summary>
        public string Command { get; }
        /// <summary>Local Vanilla Ready state immediately before dispatch.</summary>
        public bool ReadyBefore { get; }
        /// <summary>Local Vanilla Ready state after dispatch, including a partially throwing command.</summary>
        public bool ReadyAfter { get; }
        /// <summary>Whether dispatch reached the existing original hook chain. This is not proof
        /// that every downstream interceptor accepted the command or that a game started.</summary>
        public bool OriginalInvoked { get; }
    }

    /// <summary>Observes local lobby commands through APIShared's single ButtonClicked owner.</summary>
    /// <remarks>Register on Unity; callbacks run synchronously after the existing dispatch, outside
    /// registration locks, ordered by priority then ordinal owner/ID. Registrations last until
    /// process exit and have no replay. A blocked command is observed with OriginalInvoked=false.
    /// An original exception still propagates after observation. Observer errors are isolated;
    /// commands invoked recursively by an observer retain normal execution but are not observed again.
    /// This does not observe settings/map resets, identify physical input, or authorize commands.
    /// Consumers distinguish their own automatic invocations when preserving user intent.</remarks>
    public static class LobbyActionEvents
    {
        private static readonly LobbyActionObserverRegistry Registry = new LobbyActionObserverRegistry(
            LobbyLaunchGuards.EnsureActionObservation,
            error => NativeApiLog.Error(null, "Lobby action observer failed: " + error));
        internal static bool HasObservers => Registry.HasObservers;

        /// <summary>Registers a process-lifetime observation callback. Wrong-thread, invalid,
        /// duplicate identities or unavailable game members return false and a diagnostic.</summary>
        public static bool TryRegisterObserver(string ownerGuid, string registrationId,
            Action<LobbyActionNotification> observer, out string diagnostic, int order = 0)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            { diagnostic = "Lobby action observers must register on the initialized Unity thread."; return false; }
            return Registry.TryRegister(ownerGuid, registrationId, observer, out diagnostic, order);
        }
        internal static void Publish(LobbyActionNotification notification) => Registry.Publish(notification);
    }

    internal sealed class LobbyActionObserverRegistry
    {
        private readonly OwnedCallbacks<Action<LobbyActionNotification>> callbacks =
            new OwnedCallbacks<Action<LobbyActionNotification>>();
        private readonly Action install;
        private readonly Action<Exception> report;
        private bool publishing;
        internal LobbyActionObserverRegistry(Action install, Action<Exception> report)
        { this.install = install; this.report = report; }
        internal bool HasObservers => callbacks.Snapshot.Length != 0;
        internal bool TryRegister(string owner, string id, Action<LobbyActionNotification> callback,
            out string diagnostic, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null)
            { diagnostic = "Owner GUID, registration ID and callback are required."; return false; }
            try { install(); }
            catch (Exception error) { diagnostic = error.Message; return false; }
            return callbacks.TryAdd(owner, id, callback, out diagnostic, order);
        }
        internal void Publish(LobbyActionNotification notification)
        {
            if (notification == null || publishing) return;
            publishing = true;
            try
            {
                foreach (var entry in callbacks.Snapshot)
                    try { entry.Callback(notification); }
                    catch (Exception error) { try { report?.Invoke(error); } catch { } }
            }
            finally { publishing = false; }
        }
    }
}
