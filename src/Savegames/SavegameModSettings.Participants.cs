using APIShared.GameModes;
using APIShared.ModSettings;
using BepInEx.Logging;
using CrusaderDE;
using MessagePack;
using R3;
using SHCDESE.API;
using SHCDESE.API.Components.Archive;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.SaveData;
using SHCDESE.IO;
using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace APIShared
{
    public static partial class SavegameModSettings
    {
        private static Dictionary<string, Dictionary<string, byte[]>> CaptureCompatibleHostSettings()
        {
            Dictionary<string, CompatibleMod> participants;
            try { participants = FindParticipants(); }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Could not enumerate savegame settings participants: " + error);
                return new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
            }
            return CaptureCompatibleHostSettings(participants);
        }

        // Callers that already enumerated the participants reuse them: discovery calls every
        // lobby endpoint's disabled-snapshot factory.
        private static Dictionary<string, Dictionary<string, byte[]>> CaptureCompatibleHostSettings(
            Dictionary<string, CompatibleMod> participants)
        {
            var result = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, CompatibleMod> participant in participants
                .OrderBy(item => item.Key, StringComparer.Ordinal).Take(MaximumMods))
            {
                if (participant.Key.Length > 256) continue;
                result[participant.Key] = CaptureHostProperties(participant.Value.Endpoint,
                    participant.Value.Properties.Take(MaximumPropertiesPerMod), participant.Key);
            }
            return result;
        }

        internal static Dictionary<string, byte[]> CaptureHostProperties(
            object endpoint, IEnumerable<PropertyInfo> hostProperties, string modId)
        {
            var properties = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (PropertyInfo property in hostProperties)
            {
                if (property.Name.Length > 256) continue;
                try
                {
                    byte[] bytes = MessagePackSerializer.Serialize(
                        property.PropertyType, property.GetValue(endpoint));
                    if (bytes != null && bytes.Length <= MaximumPropertyBytes)
                        properties[property.Name] = bytes;
                    else
                        NativeApiLog.Error(log, "Savegame setting exceeds the property limit: " +
                            modId + "." + property.Name);
                }
                catch (Exception error)
                {
                    NativeApiLog.Error(log, "Could not capture savegame setting " +
                        modId + "." + property.Name + ": " + error.Message);
                }
            }
            return properties;
        }

        internal static void OverlaySavedHostProperties(Dictionary<string, byte[]> snapshot,
            IEnumerable<PropertyInfo> hostProperties, Dictionary<string, byte[]> stored, string modId)
        {
            if (snapshot == null || stored == null) return;
            foreach (PropertyInfo property in hostProperties)
            {
                if (!stored.TryGetValue(property.Name, out byte[] bytes) || bytes == null) continue;
                try
                {
                    object value = MessagePackSerializer.Deserialize(property.PropertyType, bytes);
                    if (value != null) snapshot[property.Name] = (byte[])bytes.Clone();
                }
                catch (Exception error)
                {
                    NativeApiLog.Error(log, "Invalid saved setting " + modId + "." +
                        property.Name + ": " + error.Message);
                }
            }
        }

        private sealed class CompatibleMod
        {
            internal IModSettingsPresetEndpoint Endpoint;
            internal PropertyInfo[] Properties;
            internal PropertyInfo[] PersonalProperties;
        }

        private static List<ModSettingsParticipantInfo> InspectParticipants(ModSettingsParticipantPurpose purpose = ModSettingsParticipantPurpose.AutomaticSavegame)
        {
            var lobby = GameXAMLManagerAPI.Instance.RegisteredModSettings.Select(entry =>
                new ModSettingsParticipantDiscovery.Registration {
                    Guid = entry?.Plugin?.Info?.Metadata?.GUID,
                    Name = entry?.Plugin?.Info?.Metadata?.Name ?? entry?.Name,
                    PluginType = entry?.Plugin?.GetType(), Model = entry?.ViewModel
                }).ToArray();
            var configurations = ModSettingsApplication.Endpoints.Select(pair =>
                new ModSettingsParticipantDiscovery.Registration {
                    Guid = pair.Key, Name = pair.Key, Model = pair.Value,
                    IsDynamic = () => pair.Value.System_HasDynamicSettings,
                    DynamicProperties = () => ModSettingsApplication.GetMissionHostProperties(pair.Value)
                }).ToArray();
            var result = ModSettingsParticipantDiscovery.Inspect(lobby, configurations, purpose);
            foreach (var item in result.Where(item => item.Status == ModSettingsParticipantStatus.InspectionFailed))
                NativeApiLog.Error(log, "Incompatible savegame settings for " + item.ModGuid + ": " + item.Diagnostic);
            return result;
        }

        private static Dictionary<string, CompatibleMod> FindParticipants()
        {
            var result = new Dictionary<string, CompatibleMod>(StringComparer.Ordinal);
            foreach (var item in ModSettingsParticipantDiscovery.Accepted(InspectParticipants()))
            {
                result.Add(item.ModGuid, new CompatibleMod {
                    Endpoint = item.Endpoint, Properties = item.HostProperties.ToArray(),
                    PersonalProperties = item.PersonalProperties
                });
            }
            return result;
        }
    }
}
