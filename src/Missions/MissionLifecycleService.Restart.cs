using CrusaderDE;
using System;
using System.Threading;

namespace APIShared
{
    internal sealed partial class MissionLifecycleService
    {
        private delegate void MenuButton(HUD_IngameMenu self, int function);
        private delegate void Confirmation(string title, Action yes, Action no, bool multiplayer, bool masters);
        private MenuButton menuButton;
        private Confirmation confirmation;
        private int restartConfirmationDepth;

        internal static bool RestartInProgress => published != null &&
            Thread.CurrentThread.ManagedThreadId == published.unityThread &&
            published.executing?.RestartedFrom != null;

        internal static void RunRestart(Action action)
        {
            var service = published ?? throw new InvalidOperationException("Mission lifecycle is unavailable.");
            service.Restart(action);
        }

        private void Restart(Action action)
        {
            if (Thread.CurrentThread.ManagedThreadId != unityThread)
                throw new InvalidOperationException("Restart requires the Unity thread.");
            if (executing != null)
            {
                if (!executing.RestartedFrom.HasValue)
                    throw new InvalidOperationException("Cannot restart during another mission initialization.");
                action();
                return;
            }
            var source = state.Current;
            if (source == null || source.IsEditor)
                throw new InvalidOperationException("Restart requires a ready gameplay mission.");
            // Begin before any caller preparation can unload the source or reload a sidecar.
            Run(new Operation
            {
                Kind = MissionStartKind.NewGame, Intent = source.Mode.Kind,
                File = source.FilePath, Name = source.MapName, Restart = true,
                RestartedFrom = source.SessionId, RestartMode = source.Mode, Multiplayer = source.Mode.IsRealMultiplayer,
                Campaign = source.Mode.CampaignMapId, Trail = source.Mode.EventTrailType,
                CoopTrail = source.Mode.CoopTrailId > 0 ? source.Mode.CoopTrailId : (int?)null,
                MissionIndex = source.MissionIndex, Size = source.MapSize, Difficulty = source.Difficulty
            }, action);
        }

        private void PrepareRestartMenu()
        {
            menuButton = Hook<MenuButton>(typeof(HUD_IngameMenu), "ButtonIngameMenuFunction", (self, function) =>
            {
                // Vanilla menu command 5 opens restart confirmation. Only its Yes callback
                // starts an operation; opening/dismissing the popup has no lifecycle effect.
                if (function != 5) { menuButton(self, function); return; }
                restartConfirmationDepth++;
                try { menuButton(self, function); }
                finally { restartConfirmationDepth--; }
            });
            confirmation = Hook<Confirmation>(typeof(HUD_ConfirmationPopup), "ShowConfirmation", (title, yes, no, multiplayer, masters) =>
            {
                Action accepted = yes;
                if (restartConfirmationDepth != 0 && yes != null)
                {
                    long? source = state.Current?.SessionId;
                    accepted = () =>
                    {
                        if (!source.HasValue || state.Current?.SessionId != source.Value)
                        {
                            Report(new InvalidOperationException("Restart confirmation no longer belongs to the active mission."));
                            return;
                        }
                        Restart(yes);
                    };
                }
                confirmation(title, accepted, no, multiplayer, masters);
            });
        }
    }
}
