using APIShared.GameModes;
using APIShared.ModSettings;
using APIShared.Internal;
using BepInEx;
using BepInEx.Logging;
using MessagePack;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.Network;
using SHCDESE.BepInEx.Bootstrap;
using SHCDESE.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
#if !API_SHARED_PRESET_TESTS
using MonoMod.RuntimeDetour;
using R3;
using SHCDESE.EventAPI;
using SHCDESE.NoesisUtil;
#endif
using ComboBoxItem = Noesis.ComboBoxItem;
using Visibility = Noesis.Visibility;
#if API_SHARED_LOBBY_OBSERVER && !API_SHARED_PRESET_TESTS
using APIShared;
#endif

namespace APIShared.ModSettings
{
    /// <summary>Immutable copied roster used by per-player convergence; player keys are game slots, not zero-based array indices.</summary>
    public sealed class PerPlayerLobbySnapshot
    {
        internal static readonly PerPlayerLobbySnapshot Empty = new PerPlayerLobbySnapshot(null, new Dictionary<int, ulong>(), false, 0);
        internal PerPlayerLobbySnapshot(ulong? lobbyId, IReadOnlyDictionary<int, ulong> players, bool unresolved, int localPlayerId)
        {
            LobbyId = lobbyId;
            Players = new ReadOnlyDictionary<int, ulong>(
                (players ?? new Dictionary<int, ulong>())
                    .ToDictionary(item => item.Key, item => item.Value));
            HasUnresolvedPlayers = unresolved;
            LocalPlayerId = localPlayerId;
        }
        /// <summary>Observed lobby identity, or null outside a known lobby.</summary>
        public ulong? LobbyId { get; }
        /// <summary>Copied mapping from occupied player slots to Steam identities.</summary>
        public IReadOnlyDictionary<int, ulong> Players { get; }
        /// <summary>Whether some observed lobby members lack an authoritative player-slot mapping.</summary>
        public bool HasUnresolvedPlayers { get; }
        /// <summary>Resolved local game slot; zero means unresolved.</summary>
        public int LocalPlayerId { get; }
    }
}
