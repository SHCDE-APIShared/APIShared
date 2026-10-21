using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared.ModSettings
{
    // Unity-thread owner. Dependencies are supplied at the process boundary, so the
    // same capture/format/deduplication path can be exercised without a running game.
    internal sealed class ModSettingsStartLog
    {
        private readonly Func<IReadOnlyDictionary<string, PresetLobbyModSettingsViewModel>> endpoints;
        private readonly Action<string> info;
        private readonly Action<string, Exception> warning;
        private long lastSession;

        internal ModSettingsStartLog(Func<IReadOnlyDictionary<string, PresetLobbyModSettingsViewModel>> endpoints,
            Action<string> info, Action<string, Exception> warning)
        { this.endpoints = endpoints; this.info = info; this.warning = warning; }

        internal void Handle(MissionLifecycleNotification notification)
        {
            if (notification.Kind != MissionLifecycleKind.Start || !notification.HasStarted ||
                notification.Context.SessionId <= lastSession) return;
            var context = notification.Context;
            lastSession = context.SessionId;
            var participants = endpoints().OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            info("MODSETTINGS_BEGIN " + DependencyFreeJson.SerializeCompact(new Dictionary<string, object>
            {
                ["schema"] = 1, ["session"] = context.SessionId, ["start"] = context.StartKind.ToString(),
                ["restart"] = context.IsRestart, ["mode"] = context.Mode.Kind.ToString(),
                ["variant"] = context.Mode.LaunchVariant.ToString(),
                ["role"] = context.Mode.IsRealMultiplayer ?
                    context.IsHost.HasValue ? context.IsHost.Value ? "host" : "client" : "unknown" :
                    context.Mode.Kind == GameModes.GameModeKind.Unknown ? "unknown" : "local",
                ["localPlayer"] = context.LocalPlayerId, ["mods"] = participants.Length
            }));
            foreach (var participant in participants)
            {
                var row = new Dictionary<string, object>
                { ["schema"] = 1, ["session"] = context.SessionId, ["mod"] = participant.Key };
                try
                {
                    foreach (var item in participant.Value.CaptureRuntimeLogSettings(
                        (key, error) => Warn(participant.Key + "/" + key, error))) row.Add(item.Key, item.Value);
                }
                catch (Exception exception)
                {
                    row["errors"] = new[] { new Dictionary<string, object> { ["scope"] = "mod", ["code"] = "read_failed" } };
                    Warn(participant.Key, exception);
                }
                info("MODSETTINGS " + DependencyFreeJson.SerializeCompact(row));
            }
        }

        private void Warn(string key, Exception exception)
        { try { warning?.Invoke(key, exception); } catch { } }
    }
}
