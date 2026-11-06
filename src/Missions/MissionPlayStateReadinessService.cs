using System;
using System.Threading;
using APIShared.Presentation;
using BepInEx.Logging;

namespace APIShared
{
    // No new hook: lifecycle captures provenance before loading; the existing GUI publisher only
    // wakes one coalesced Unity callback. The process-owned frame registration stays dormant otherwise.
    internal sealed class MissionPlayStateReadinessService
    {
        private readonly int unityThread = Thread.CurrentThread.ManagedThreadId;
        private readonly ManualLogSource log;
        private readonly MissionPlayStateReadinessState state;
        private IUiFrameWorkRegistration frameWork;
        private bool active;

        private MissionPlayStateReadinessService(ManualLogSource log)
        {
            this.log = log;
            state = new MissionPlayStateReadinessState(Request, Report,
                () => Thread.CurrentThread.ManagedThreadId == unityThread,
                session => MissionLifecycleService.ActiveContext?.SessionId == session);
        }
        private static object ReadPlayState() => GameData.Instance?.lastGameState;
        private void Request() => frameWork?.Request();
        private void Pump() { if (active) state.Pump(ReadPlayState()); }
        private void ObserveGui(PresentationPostEventArgs args) { if (active && state.Pending) Request(); }
        private void ObserveMission(MissionLifecycleNotification notification)
        {
            // Plain managed reference capture is synchronous on the lifecycle publisher thread.
            // Deferring it could mistake a first new-mission buffer for the old world's state.
            if (active) state.Handle(notification, ReadPlayState());
        }
        private void Report(Exception error)
        { try { NativeApiLog.Error(log, "Mission PlayState readiness callback failed: " + error); } catch { } }

        internal static ManagedMemberContract MemberContract() => new ManagedMemberContract()
            .Method(typeof(GameData), "get_Instance", typeof(GameData))
            .Method(typeof(GameData), "get_lastGameState", typeof(EngineInterface.PlayState));

        internal static bool TryCreate(MissionLifecycleService lifecycle, ManualLogSource log,
            out MissionPlayStateReadinessService service, out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            if (!ManagedMemberContracts.TryValidate(NativeCapabilityIds.MissionPlayStateReadiness, log, out diagnostic))
                return false;
            var candidate = new MissionPlayStateReadinessService(log);
            try
            {
                if (lifecycle == null) throw new InvalidOperationException("Mission lifecycle unavailable.");
                var instance = typeof(GameData).GetProperty(nameof(GameData.Instance));
                var snapshot = typeof(GameData).GetProperty(nameof(GameData.lastGameState));
                if (instance?.GetMethod == null || !instance.GetMethod.IsPublic || !instance.GetMethod.IsStatic || instance.PropertyType != typeof(GameData) ||
                    snapshot?.GetMethod == null || !snapshot.GetMethod.IsPublic || snapshot.GetMethod.IsStatic || snapshot.PropertyType != typeof(EngineInterface.PlayState))
                    throw new MissingMemberException("Public GameData PlayState getters changed.");
                if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).Ui.TryRegisterBeforeRender("mission-playstate-readiness",
                    candidate.Pump, out candidate.frameWork, out string reason, 90))
                    throw new InvalidOperationException(reason);
                if (!PresentationEvents.TryRegister(PresentationOperation.GuiChecks, APISharedPlugin.PluginGuid,
                    "mission-playstate-readiness", null, candidate.ObserveGui, out reason))
                    throw new InvalidOperationException(reason);
                candidate.active = true;
                if (!lifecycle.Bind(APISharedPlugin.PluginGuid).TryRegisterObserver("mission-playstate-readiness",
                    candidate.ObserveMission, candidate.ObserveMission, candidate.ObserveMission, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason);
                service = candidate;
                diagnostic = Status(NativeCapabilityState.Available, "Current-mission PlayState observation available.");
                return true;
            }
            catch (Exception error)
            {
                // Any already-published registrations remain rooted and inert for the process lifetime.
                candidate.active = false;
                diagnostic = Status(NativeCapabilityState.Faulted, error.Message);
                candidate.Report(error);
                return false;
            }
        }
        private static NativeCapabilityDiagnostic Status(NativeCapabilityState status, string reason) =>
            new NativeCapabilityDiagnostic(NativeCapabilityIds.MissionPlayStateReadiness, status, string.Empty, reason);
        internal IMissionPlayStateReadinessCapability Bind(string owner) => new Binding(this, owner);
        private sealed class Binding : IMissionPlayStateReadinessCapability
        {
            private readonly MissionPlayStateReadinessService service;
            private readonly string owner;
            internal Binding(MissionPlayStateReadinessService service, string owner) { this.service = service; this.owner = owner; }
            public MissionContext Current => service.state.Current;
            public bool TryRegisterObserver(string registrationId, Action<MissionPlayStateReadyNotification> onReady,
                out NativeCapabilityDiagnostic diagnostic)
            {
                bool registered = service.state.Register(owner, registrationId, onReady);
                diagnostic = Status(registered ? NativeCapabilityState.Available : NativeCapabilityState.ValidationFailed,
                    registered ? "Mission PlayState observer registered." : "Registration requires Unity, a callback and a unique nonempty owner-local ID.");
                if (registered) service.Pump();
                return registered;
            }
        }
    }
}
