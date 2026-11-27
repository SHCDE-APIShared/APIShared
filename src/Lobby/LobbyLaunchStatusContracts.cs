using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using APIShared.Events;
using APIShared.Internal;
using APIShared.Localization;
using BepInEx.Logging;
using CrusaderDE;
using SHCDESE.API;
using Steamworks;

namespace APIShared.Lobby
{
    /// <summary>How a lobby launch status item is announced.</summary>
    public enum LobbyLaunchStatusKind
    {
        /// <summary>A condition that currently prevents Ready/Start; announced immediately.</summary>
        Blocking = 0,
        /// <summary>An expected transitional state, such as a joining player loading the selected mission.
        /// Announced as a neutral wait hint; reported as blocking only if it persists.</summary>
        Loading = 1,
        /// <summary>An obvious open-ended wait, such as a lobby without its second player. Never announced;
        /// while present it suppresses the "ready" announcement.</summary>
        Waiting = 2,
    }

    /// <summary>Who sees an entry reported while the local player is a lobby member rather than the host.</summary>
    /// <remarks>The host always announces its entries to the whole lobby.</remarks>
    public enum LobbyLaunchStatusScope
    {
        /// <summary>Shown only in the local lobby chat. Use it for states the host already reports for every player.</summary>
        Local = 0,
        /// <summary>Sent to the whole lobby. Use it for local conditions the host cannot detect, such as a missing file.</summary>
        Lobby = 1,
    }

    /// <summary>The lobby command a consumer rejected; APIShared supplies the localized label.</summary>
    public enum LobbyLaunchOperation
    {
        /// <summary>The Ready or ReadyLock command of a lobby member.</summary>
        Ready = 0,
        /// <summary>A start command, such as the host's Play or Coop start.</summary>
        Start = 1,
    }

    /// <summary>One immutable, player-facing launch status entry.</summary>
    public sealed class LobbyLaunchStatusItem
    {
        /// <summary>Creates a <see cref="LobbyLaunchStatusScope.Local"/> entry. <paramref name="text"/> is shown in lobby
        /// chat in the sender's language; <paramref name="detail"/> is written only to the local log.</summary>
        public LobbyLaunchStatusItem(string text, LobbyLaunchStatusKind kind = LobbyLaunchStatusKind.Blocking, string detail = null)
            : this(text, kind, detail, LobbyLaunchStatusScope.Local) { }

        /// <summary>Creates an entry with an explicit audience; <paramref name="scope"/> applies to lobby members.</summary>
        public LobbyLaunchStatusItem(string text, LobbyLaunchStatusKind kind, string detail, LobbyLaunchStatusScope scope)
        {
            Text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            Kind = kind;
            Detail = detail ?? string.Empty;
            Scope = scope;
        }
        /// <summary>Localized text; its ordinal value identifies the entry for deduplication.</summary>
        public string Text { get; }
        /// <summary>Announcement class of this entry.</summary>
        public LobbyLaunchStatusKind Kind { get; }
        /// <summary>Technical log detail; never sent to other players.</summary>
        public string Detail { get; }
        /// <summary>Audience of the entry while the local player is a lobby member; ignored on the host.</summary>
        public LobbyLaunchStatusScope Scope { get; }
    }

    /// <summary>Lobby passed to providers during one refresh.</summary>
    public sealed class LobbyLaunchStatusQuery
    {
        internal LobbyLaunchStatusQuery(ulong lobbyId, bool isHost, FRONT_Multiplayer lobby)
        { LobbyId = lobbyId; IsHost = isHost; Lobby = lobby; }
        /// <summary>Steam identifier of the active lobby.</summary>
        public ulong LobbyId { get; }
        /// <summary>Whether the local player hosts the lobby.</summary>
        public bool IsHost { get; }
        /// <summary>Vanilla lobby view; read it only during the provider call.</summary>
        public FRONT_Multiplayer Lobby { get; }
    }
}
