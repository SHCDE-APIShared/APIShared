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
    /// <summary>Optional policies for SyncPerPlayer properties configured by the settings model before publication. Companion NameData arrays must be stable and contain slots 0..8. The coordinator owns transport and convergence.</summary>
    /// <remarks>Configured callbacks are retained by the coordinator and run synchronously in addition order, without replay or implicit dispatch. They are ordinary delegates: throwing can interrupt the current coordinator operation. Keep them short and use the documented caller context. Register policies before settings publication.</remarks>
    public sealed class PerPlayerLobbySettingsBuilder
    {
        private readonly PresetLobbyModSettingsViewModel owner;
        private readonly Dictionary<string, PerPlayerLobbySettingOptions> options = new Dictionary<string, PerPlayerLobbySettingOptions>(StringComparer.Ordinal);
        private Action beforePublish;
        private Action<int> localPlayerResolved;
        private Action<PerPlayerLobbySnapshot> lobbyChanged;
        private Action<string> remoteDataChanged;
        private Action published;
        private Action observe;

        internal PerPlayerLobbySettingsBuilder(PresetLobbyModSettingsViewModel owner) { this.owner = owner; }

        /// <summary>Supplies reset values for a named per-player property when slot ownership changes; a null factory is rejected.</summary>
        public PerPlayerLobbySettingsBuilder ResetSlotsWith(string propertyName, Func<object> resetValueFactory) { Get(propertyName).ResetValueFactory = resetValueFactory ?? throw new ArgumentNullException(nameof(resetValueFactory)); return this; }
        /// <summary>Requires a player report for launch readiness; the default predicate considers non-null values reported.</summary>
        public PerPlayerLobbySettingsBuilder RequireReport(string propertyName, Func<object, bool> hasReport = null) { PerPlayerLobbySettingOptions item = Get(propertyName); item.IsReportRequired = true; item.HasReport = hasReport ?? (value => value != null); return this; }
        /// <summary>Adds a callback before the coordinator publishes local values; callbacks use the observation/publish caller thread.</summary>
        public PerPlayerLobbySettingsBuilder BeforePublish(Action callback) { beforePublish += callback; return this; }
        /// <summary>Adds a callback when the local slot is resolved during roster finalization or publication; receives its game player slot.</summary>
        public PerPlayerLobbySettingsBuilder WhenLocalPlayerResolved(Action<int> callback) { localPlayerResolved += callback; return this; }
        /// <summary>Adds a callback for an observed lobby snapshot, not an initial-state replay subscription.</summary>
        public PerPlayerLobbySettingsBuilder WhenLobbyChanged(Action<PerPlayerLobbySnapshot> callback) { lobbyChanged += callback; return this; }
        /// <summary>Adds a callback after remote per-player data changes; receives the setting property name.</summary>
        public PerPlayerLobbySettingsBuilder WhenRemoteDataChanged(Action<string> callback) { remoteDataChanged += callback; return this; }
        /// <summary>Adds a callback after publishing local per-player values.</summary>
        public PerPlayerLobbySettingsBuilder AfterPublish(Action callback) { published += callback; return this; }
        /// <summary>Adds a callback for coordinator observations, even when no lobby-change notification is needed.</summary>
        public PerPlayerLobbySettingsBuilder OnObservation(Action callback) { observe += callback; return this; }

        internal PerPlayerLobbySettingsContract Build()
        {
            PropertyInfo[] properties = owner.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (PropertyInfo property in properties)
            {
                bool host = property.GetCustomAttribute<SyncHostOnlyAttribute>() != null;
                bool player = property.GetCustomAttribute<SyncPerPlayerAttribute>() != null;
                bool local = property.GetCustomAttribute<PresetLocalAttribute>() != null;
                int classifications = (host ? 1 : 0) + (player ? 1 : 0) + (local ? 1 : 0);
                if (classifications > 1)
                    throw new InvalidOperationException($"Setting [{owner.GetType().Name}.{property.Name}] has conflicting sync/preset classifications.");
            }

            var settings = new List<PerPlayerLobbySettingContract>();
            foreach (PropertyInfo property in properties.Where(item => item.GetCustomAttribute<SyncPerPlayerAttribute>() != null))
            {
                if (!property.CanRead)
                    throw new InvalidOperationException($"Per-player setting [{owner.GetType().Name}.{property.Name}] is not readable.");
                PropertyInfo dataProperty = owner.GetType().GetProperty(property.Name + "Data", BindingFlags.Instance | BindingFlags.Public);
                if (dataProperty == null || !dataProperty.CanRead || !dataProperty.PropertyType.IsArray)
                    throw new InvalidOperationException($"Per-player setting [{owner.GetType().Name}.{property.Name}] requires a readable [{property.Name}Data] array.");
                Type elementType = dataProperty.PropertyType.GetElementType();
                if (!elementType.IsAssignableFrom(property.PropertyType))
                    throw new InvalidOperationException($"Companion [{owner.GetType().Name}.{dataProperty.Name}] has element type [{elementType}], expected [{property.PropertyType}].");
                Array data = dataProperty.GetValue(owner) as Array;
                if (data == null || data.Rank != 1 || data.Length < PlayerSlots.LastPlayerId + 1)
                    throw new InvalidOperationException($"Companion [{owner.GetType().Name}.{dataProperty.Name}] must be a one-dimensional array containing slots 0 through 8.");
                if (!ReferenceEquals(data, dataProperty.GetValue(owner)))
                    throw new InvalidOperationException($"Companion [{owner.GetType().Name}.{dataProperty.Name}] must return one stable array instance.");

                options.TryGetValue(property.Name, out PerPlayerLobbySettingOptions configured);
                configured = configured ?? new PerPlayerLobbySettingOptions();
                settings.Add(new PerPlayerLobbySettingContract(property, dataProperty, data, configured));
            }
            foreach (string configuredName in options.Keys)
                if (!settings.Any(item => item.Property.Name == configuredName))
                    throw new InvalidOperationException($"Per-player policy references non-[SyncPerPlayer] property [{owner.GetType().Name}.{configuredName}].");
            return new PerPlayerLobbySettingsContract(settings, beforePublish, localPlayerResolved, lobbyChanged, remoteDataChanged, published, observe);
        }

        private PerPlayerLobbySettingOptions Get(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentException("A property name is required.", nameof(propertyName));
            if (!options.TryGetValue(propertyName, out PerPlayerLobbySettingOptions value)) options[propertyName] = value = new PerPlayerLobbySettingOptions();
            return value;
        }
    }
}
