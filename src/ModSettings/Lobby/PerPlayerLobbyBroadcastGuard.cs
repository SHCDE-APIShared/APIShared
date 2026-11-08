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
#if !API_SHARED_PRESET_TESTS
    // Extender broadcasts before our PropertyChanged observer runs. Intercept only its
    // outbound boundary for prepared APIShared participants; persistence/UI events and
    // unrelated Extender consumers keep their original behavior. Retained until exit.
    internal static class PerPlayerLobbyBroadcastGuard
    {
        private delegate void Broadcast(GameXAMLManagerAPI self, string mod, object model, PropertyInfo property);
        private static readonly ConditionalWeakTable<object, PerPlayerLobbySettingsCoordinator> coordinators =
            new ConditionalWeakTable<object, PerPlayerLobbySettingsCoordinator>();
        private static Hook hook;
        private static Broadcast original;

        internal static void Register(object model, PerPlayerLobbySettingsCoordinator coordinator)
        {
            if (hook == null)
            {
                MethodInfo target = typeof(GameXAMLManagerAPI).GetMethod("BroadcastSettingChange",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(string), typeof(object), typeof(PropertyInfo) }, null);
                if (target == null || target.ReturnType != typeof(void) || target.IsStatic || target.GetMethodBody() == null)
                    throw new MissingMethodException("GameXAMLManagerAPI.BroadcastSettingChange contract changed.");
                Hook candidate = null;
                try
                {
                    candidate = new Hook(target, (Broadcast)OnBroadcast,
                        new HookConfig { ManualApply = true, ID = "APIShared.PerPlayerLobbyBroadcast" });
                    original = candidate.GenerateTrampoline<Broadcast>();
                    candidate.Apply();
                    hook = candidate;
                }
                catch
                {
                    // Only a candidate that failed before publication can be rolled back.
                    candidate?.Dispose();
                    throw;
                }
            }
            coordinators.Add(model, coordinator);
        }

        private static void OnBroadcast(GameXAMLManagerAPI self, string mod, object model, PropertyInfo property)
        {
            if (model != null && property != null && coordinators.TryGetValue(model, out var coordinator) &&
                coordinator.DeferBroadcast(property.Name)) return;
            original(self, mod, model, property);
        }
    }
#endif
}
