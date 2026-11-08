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
        private void OnInitializing(MissionLifecycleNotification notification)
        {
            if (notification?.Context == null) return;
            if (notification.Phase == MissionInitializationPhase.BeforeLoad ||
                notification.Phase == MissionInitializationPhase.BeforeNativeStart)
                BeginWorld(notification.Context.SessionId);
        }
        private void OnStarted(MissionLifecycleNotification notification)
        {
            if (notification?.Context == null) return;
            lock (engine)
            {
                BeginWorld(notification.Context.SessionId);
                if (rawRestored) CompleteNativeLoad();
            }
        }
        private void OnEnded(MissionLifecycleNotification notification)
        {
            if (notification?.Context == null) return;
            lock (engine)
                if (worldSession == notification.Context.SessionId)
                { ClearWorld(); worldSession = 0; }
        }
        private void BeginWorld(long session)
        {
            lock (engine)
            {
                if (worldSession == session) return;
                ClearWorld(); worldSession = session;
            }
        }
        private void ClearWorld()
        {
            worldChanging = true;
            try
            {
                var callbacks = new List<Tracked>();
                foreach (var entry in tracked.Values) if (entry.Pending) callbacks.Add(entry);
                callbacks.AddRange(retired);
                generation++;
                tracked.Clear(); retired.Clear(); Array.Clear(additional, 0, additional.Length);
                orders?.Clear(generation); backend?.BeginWorld(generation); AiMilitaryQuotaRuntime.Clear(generation);
                AiMilitaryNativeObservers.BeginWorld();
                prepared = null; rawRestored = false; rawRestoreFailed = false;
                // Retire all world-owned data first. Reentrant completion callbacks cannot add a
                // request to the cleared old world or invalidate dictionary enumeration.
                foreach (var entry in callbacks)
                    Notify(entry, new AiMilitaryAssignmentResult(entry.Unit, entry.Assignment,
                        AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.WorldEnded, "The world ended."));
            }
            finally { worldChanging = false; }
        }
    }
}
