using APIShared.GameModes;
using APIShared.Internal;
using SHCDESE.API;
using SHCDESE.EventAPI.MapLoader;
using CrusaderDE;
using System;
using System.Collections.Generic;
using System.Linq;
#if !API_SHARED_PRESET_TESTS
using Steamworks;
#endif

namespace APIShared.GameModes
{
    /// <summary>Identified mission or editor category; Unknown means evidence is insufficient.</summary>
    public enum GameModeKind
    {
        /// <summary>Unknown.</summary>
        Unknown,
        /// <summary>MapEditor.</summary>
        MapEditor,
        /// <summary>Campaign.</summary>
        Campaign,
        /// <summary>StandaloneMission.</summary>
        StandaloneMission,
        /// <summary>CustomGame.</summary>
        CustomGame,
        /// <summary>VanillaTrail.</summary>
        VanillaTrail,
        /// <summary>CustomTrail.</summary>
        CustomTrail,
        /// <summary>CoopTrail.</summary>
        CoopTrail,
        /// <summary>SandsOfTime.</summary>
        SandsOfTime,
        /// <summary>Tutorial.</summary>
        Tutorial,
    }

    /// <summary>Whether the mission uses standard content, verified customization, or restored customization.</summary>
    public enum GameModeLaunchVariant
    {
        /// <summary>Standard.</summary>
        Standard,
        /// <summary>Customized.</summary>
        Customized,
        /// <summary>RestoredCustomizedSave.</summary>
        RestoredCustomizedSave,
    }

    /// <summary>Named Vanilla trail identifiers; these are trail identifiers, not player or mission indices.</summary>
    public enum GameTrailType
    {
        /// <summary>FirstEdition.</summary>
        FirstEdition = 0,
        /// <summary>Warchest.</summary>
        Warchest = 1,
        /// <summary>Extreme.</summary>
        Extreme = 2,
        /// <summary>SandsOne.</summary>
        SandsOne = 11,
        /// <summary>SandsTwo.</summary>
        SandsTwo = 12,
        /// <summary>SandsThree.</summary>
        SandsThree = 13,
        /// <summary>SandsFour.</summary>
        SandsFour = 14,
        /// <summary>SandsFive.</summary>
        SandsFive = 15,
        /// <summary>SandsSix.</summary>
        SandsSix = 16,
        /// <summary>SandsSeven.</summary>
        SandsSeven = 17,
        /// <summary>SandsEight.</summary>
        SandsEight = 18,
    }
}
