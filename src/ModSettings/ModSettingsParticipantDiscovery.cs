using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MessagePack;

namespace APIShared.ModSettings
{
    // No second registry: production supplies snapshots of the existing lobby and
    // configuration registrations. Tests substitute only that discovery boundary.
    internal static class ModSettingsParticipantDiscovery
    {
        internal sealed class Registration
        {
            internal string Guid, Name;
            internal object Model;
            internal Type PluginType;
            internal Func<bool> IsDynamic;
            internal Func<PropertyInfo[]> DynamicProperties;
        }

        internal static List<ModSettingsParticipantInfo> Inspect(IEnumerable<Registration> lobby,
            IEnumerable<Registration> configurations, ModSettingsParticipantPurpose purpose = ModSettingsParticipantPurpose.AutomaticSavegame)
        {
            var result = new List<ModSettingsParticipantInfo>();
            var authoringOnly = new List<Registration>();
            foreach (var group in lobby.GroupBy(item => item.Guid ?? string.Empty, StringComparer.Ordinal))
            {
                var first = group.First();
                if (string.IsNullOrEmpty(group.Key))
                {
                    foreach (var registration in group)
                        result.Add(Reject(registration, ModSettingsParticipantStatus.MissingPluginGuid, "missing owning plugin GUID"));
                    continue;
                }
                try
                {
                    // Keep the prior duplicate/exclusion precedence and never create a
                    // mission snapshot for a rejected registration group.
                    if (group.Skip(1).Any())
                        result.Add(Reject(first, ModSettingsParticipantStatus.DuplicateRegistration, "multiple mod-settings panels use the same plugin GUID"));
                    else
                    {
                        bool savegameExcluded = SavegameSettingsParticipation.IsExcluded(first.PluginType);
                        if (savegameExcluded)
                        {
                            result.Add(Reject(first, ModSettingsParticipantStatus.Excluded, "plugin excluded from automatic settings"));
                            if (purpose == ModSettingsParticipantPurpose.MissionAuthoring) authoringOnly.Add(first);
                        }
                        else result.Add(InspectLobby(first, checkCurrentValues: false));
                    }
                }
                catch (Exception error) { result.Add(Failed(first, error)); }
            }
            foreach (var registration in configurations)
            {
                try
                {
                    if (!registration.IsDynamic()) continue;
                    if (result.Any(item => item.ModGuid == registration.Guid &&
                        (item.IsCompatible || item.Status == ModSettingsParticipantStatus.ConflictingProvider)))
                    {
                        result.RemoveAll(item => item.ModGuid == registration.Guid);
                        result.Add(Reject(registration, ModSettingsParticipantStatus.ConflictingProvider,
                            "Duplicate settings provider: " + registration.Guid, configuration: true));
                        continue;
                    }
                    result.Add(new ModSettingsParticipantInfo(registration.Guid, registration.Name,
                        ModSettingsParticipantStatus.Compatible, null, true,
                        (IModSettingsPresetEndpoint)registration.Model, registration.DynamicProperties()));
                }
                catch (Exception error) { result.Add(Failed(registration, error, true)); }
            }
            // An explicit dynamic endpoint used to win before authoring considered a
            // savegame-excluded panel. Preserve that precedence and avoid probing its
            // shadowed panel; only otherwise unrepresented authoring panels need checks.
            foreach (var registration in authoringOnly)
            {
                if (result.Any(item => item.ModGuid == registration.Guid &&
                    (item.IsCompatible || item.Status == ModSettingsParticipantStatus.ConflictingProvider))) continue;
                result.RemoveAll(item => item.ModGuid == registration.Guid && item.Status == ModSettingsParticipantStatus.Excluded);
                try { result.Add(InspectLobby(registration, checkCurrentValues: true)); }
                catch (Exception error) { result.Add(Failed(registration, error)); }
            }
            return result;
        }

        internal static IEnumerable<ModSettingsParticipantInfo> Accepted(IEnumerable<ModSettingsParticipantInfo> catalog)
        {
            foreach (var item in catalog)
            {
                if (item.Status == ModSettingsParticipantStatus.ConflictingProvider)
                    throw new InvalidOperationException(item.Diagnostic);
                if (item.IsCompatible) yield return item;
            }
        }

        private static ModSettingsParticipantInfo InspectLobby(Registration registration, bool checkCurrentValues)
        {
            if (!(registration.Model is IModSettingsPresetEndpoint endpoint))
                return Reject(registration, ModSettingsParticipantStatus.MissingEndpoint, "missing typed APIShared preset endpoint");
            var properties = registration.Model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var host = properties.Where(property => property.CanRead && property.CanWrite &&
                Has(property, "SyncHostOnlyAttribute") && !Has(property, "DoNotPersistAttribute"))
                .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            var personal = properties.Where(property => property.CanRead && property.CanWrite &&
                !Has(property, "DoNotPersistAttribute") &&
                (Has(property, "SyncPerPlayerAttribute") || Has(property, "PresetLocalAttribute"))).ToArray();
            if (host.Length == 0)
                return Reject(registration, ModSettingsParticipantStatus.NoHostSettings, "no persistent SyncHostOnly settings");
            if (host.Any(property => property.Name == "EnableMod" && property.PropertyType != typeof(bool)))
                return Reject(registration, ModSettingsParticipantStatus.InvalidHostSettings, "EnableMod must be Boolean");
            var duplicate = host.GroupBy(property => property.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Skip(1).Any());
            if (duplicate != null)
                return Reject(registration, ModSettingsParticipantStatus.InvalidHostSettings, "duplicate SyncHostOnly property " + duplicate.Key);
            // Historically authoring separately validated savegame-excluded endpoints.
            // Preserve those current-value probes without broadening the savegame opt-out
            // or adding probes to ordinary automatic participants.
            if (checkCurrentValues)
                foreach (var property in host)
                {
                    object value = property.GetValue(registration.Model);
                    if (value == null)
                        return Reject(registration, ModSettingsParticipantStatus.InvalidHostSettings, property.Name + " is null");
                    MessagePackSerializer.Serialize(property.PropertyType, value);
                }
            var disabled = endpoint.System_CreateDisabledMissionPresetSnapshot();
            if (disabled == null)
                return Reject(registration, ModSettingsParticipantStatus.InvalidMissionSnapshot, "mission snapshot is null");
            foreach (var property in host)
            {
                if (!disabled.TryGetValue(property.Name, out var bytes) || bytes == null)
                    return Reject(registration, ModSettingsParticipantStatus.InvalidMissionSnapshot, "mission snapshot is missing " + property.Name);
                object value = MessagePackSerializer.Deserialize(property.PropertyType, bytes);
                if (property.Name == "EnableMod" && value is bool enabled && enabled)
                    return Reject(registration, ModSettingsParticipantStatus.InvalidMissionSnapshot, "mission snapshot does not disable EnableMod");
            }
            return new ModSettingsParticipantInfo(registration.Guid, registration.Name,
                ModSettingsParticipantStatus.Compatible, null, endpoint: endpoint, host: host, personal: personal);
        }

        private static bool Has(PropertyInfo property, string attribute) =>
            property.GetCustomAttributes(false).Any(item => item.GetType().Name == attribute);

        private static ModSettingsParticipantInfo Reject(Registration registration, ModSettingsParticipantStatus status,
            string diagnostic, bool configuration = false) =>
            new ModSettingsParticipantInfo(registration.Guid, registration.Name, status, diagnostic, configuration);

        private static ModSettingsParticipantInfo Failed(Registration registration, Exception error, bool configuration = false) =>
            Reject(registration, ModSettingsParticipantStatus.InspectionFailed, error.GetBaseException().Message, configuration);
    }
}
