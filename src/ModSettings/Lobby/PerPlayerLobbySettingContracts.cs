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
    internal sealed class PerPlayerLobbySettingOptions { internal Func<object> ResetValueFactory; internal bool IsReportRequired; internal Func<object, bool> HasReport; }
    internal sealed class PerPlayerLobbySettingContract
    {
        private readonly Array data;
        private readonly PerPlayerLobbySettingOptions options;
        internal PerPlayerLobbySettingContract(PropertyInfo property, PropertyInfo dataProperty, Array data, PerPlayerLobbySettingOptions options) { Property = property; DataProperty = dataProperty; this.data = data; this.options = options; }
        internal PropertyInfo Property { get; }
        internal PropertyInfo DataProperty { get; }
        internal bool IsReportRequired => options.IsReportRequired;
        internal Array GetData() => data;
        internal object CreateResetValue() => options.ResetValueFactory != null ? options.ResetValueFactory() : (Property.PropertyType.IsValueType ? Activator.CreateInstance(Property.PropertyType) : null);
        internal bool HasReport(object value) => !IsReportRequired || (options.HasReport ?? (item => item != null))(value);
    }
    internal sealed class PerPlayerLobbySettingsContract
    {
        internal PerPlayerLobbySettingsContract(IReadOnlyList<PerPlayerLobbySettingContract> settings, Action beforePublish, Action<int> localPlayerResolved, Action<PerPlayerLobbySnapshot> lobbyChanged, Action<string> remoteDataChanged, Action published, Action observe) { Settings = settings; BeforePublish = beforePublish; LocalPlayerResolved = localPlayerResolved; LobbyChanged = lobbyChanged; RemoteDataChanged = remoteDataChanged; Published = published; Observe = observe; }
        internal IReadOnlyList<PerPlayerLobbySettingContract> Settings { get; }
        internal Action BeforePublish { get; }
        internal Action<int> LocalPlayerResolved { get; }
        internal Action<PerPlayerLobbySnapshot> LobbyChanged { get; }
        internal Action<string> RemoteDataChanged { get; }
        internal Action Published { get; }
        internal Action Observe { get; }
    }
}
