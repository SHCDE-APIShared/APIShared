using System;
using System.Collections.Generic;
using System.Reflection;

namespace APIShared.ModSettings
{
    /// <summary>Result of one current Map/Trail/Savegame settings participation check.</summary>
    public enum ModSettingsParticipantStatus
    {
        /// <summary>The existing shared settings contract accepts this endpoint.</summary>
        Compatible = 0,
        /// <summary>The lobby registration has no owning plugin GUID.</summary>
        MissingPluginGuid = 1,
        /// <summary>Multiple lobby panels claim the same plugin GUID.</summary>
        DuplicateRegistration = 2,
        /// <summary>The plugin explicitly excludes its lobby registration from automatic settings.</summary>
        Excluded = 3,
        /// <summary>The registration supplies no typed preset endpoint.</summary>
        MissingEndpoint = 4,
        /// <summary>No persistent host-owned settings are exposed.</summary>
        NoHostSettings = 5,
        /// <summary>Host property names or the EnableMod type violate the shared contract.</summary>
        InvalidHostSettings = 6,
        /// <summary>The disabled mission snapshot is absent, incomplete or does not disable EnableMod.</summary>
        InvalidMissionSnapshot = 7,
        /// <summary>Metadata inspection, snapshot creation or decoding threw; other participants remain inspectable.</summary>
        InspectionFailed = 8,
        /// <summary>Two accepted providers claim the same GUID; automatic operations retain their fail-closed exception.</summary>
        ConflictingProvider = 9
    }

    /// <summary>Detached participation metadata. The endpoint and PropertyInfo targets remain live process-owned objects.</summary>
    /// <remarks>Created synchronously on the caller's Unity thread. Collections are read-only copies;
    /// this is neither a settings-value snapshot nor a provider registration. Re-query after registrations
    /// or endpoint schemas change. Rejected entries expose no usable endpoint or host properties.</remarks>
    public sealed class ModSettingsParticipantInfo
    {
        /// <summary>Owning plugin GUID, using ordinal identity; empty for an unidentified registration.</summary>
        public string ModGuid { get; }
        /// <summary>Plugin display name, falling back to its GUID or registration name.</summary>
        public string DisplayName { get; }
        /// <summary>Structured participation outcome.</summary>
        public ModSettingsParticipantStatus Status { get; }
        /// <summary>Whether this endpoint passed the existing shared contract.</summary>
        public bool IsCompatible => Status == ModSettingsParticipantStatus.Compatible;
        /// <summary>Failure explanation, or null for an accepted endpoint. Not a localized UI string.</summary>
        public string Diagnostic { get; }
        /// <summary>Accepted endpoint, or null on rejection; invoking it remains the caller's responsibility.</summary>
        public IModSettingsPresetEndpoint Endpoint { get; }
        /// <summary>Read-only host property metadata; empty on rejection. No setting values are read here.</summary>
        public IReadOnlyList<PropertyInfo> HostProperties { get; }
        /// <summary>Whether the result came from an explicitly registered dynamic configuration endpoint.</summary>
        public bool IsConfigurationEndpoint { get; }
        internal PropertyInfo[] PersonalProperties { get; }

        internal ModSettingsParticipantInfo(string guid, string name, ModSettingsParticipantStatus status,
            string diagnostic, bool configuration = false, IModSettingsPresetEndpoint endpoint = null,
            PropertyInfo[] host = null, PropertyInfo[] personal = null)
        {
            ModGuid = guid ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(name) ? ModGuid : name;
            Status = status; Diagnostic = diagnostic; IsConfigurationEndpoint = configuration;
            Endpoint = endpoint;
            HostProperties = Array.AsReadOnly((PropertyInfo[])(host ?? Array.Empty<PropertyInfo>()).Clone());
            PersonalProperties = (PropertyInfo[])(personal ?? Array.Empty<PropertyInfo>()).Clone();
        }
    }
}
