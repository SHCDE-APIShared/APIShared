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
    internal readonly struct SavedModeEvidence
    {
        internal SavedModeEvidence(bool hasSaved, GameModeKind kind,
            GameModeLaunchVariant variant)
        {
            HasSaved = hasSaved;
            Kind = kind;
            Variant = variant;
        }

        internal bool HasSaved { get; }
        internal GameModeKind Kind { get; }
        internal GameModeLaunchVariant Variant { get; }
    }
}
