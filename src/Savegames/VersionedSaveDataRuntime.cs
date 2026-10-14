using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using System;
using System.Collections.Generic;

namespace APIShared.Savegames
{
    public static partial class VersionedSaveData
    {
        /// <summary>Registers a versioned record for the process lifetime and registers its Script Extender
        /// ModSaveDataAPI handler under <see cref="VersionedSaveDataRegistration.SaveDataIdentifier"/>.</summary>
        /// <remarks>
        /// <para>Thread and timing: thread-safe; register once during plugin startup (Awake or the
        /// Extender's LibraryLoaded), never from a save or load callback, and before the first load whose
        /// record you need. There is no unregistration, replay of earlier loads, or thread dispatch.</para>
        /// <para>Save: <see cref="VersionedSaveDataRegistration.Capture"/> runs synchronously on the
        /// Extender's save thread for each in-scope save. Records in TooNew, Unrecognized or
        /// MigrationFailed state are written back byte for byte without calling capture. Otherwise the
        /// written known entries plus the kept unknown entries form the record.</para>
        /// <para>Load: <see cref="VersionedSaveDataRegistration.Loaded"/> runs exactly once at the end of
        /// every Extender OnLoadSave or OnLoadMap (in its Post notification, after the native load
        /// returned), on that thread, with the result for this registration, including NoRecord and
        /// OutOfScope. Legacy readers and migration steps run on the same thread just before it. Store
        /// the entries and apply them when your mission state is ready.</para>
        /// <para>Ordering: load callbacks of different registrations run in ordinal owner GUID, then
        /// identifier order. For save loads entered through APIShared's mission lifecycle, the saved mod
        /// settings are applied before the native load and therefore before these callbacks; the mission
        /// start notification follows them. Save capture order across mods is the Extender's handler
        /// order and is unspecified; captures must not depend on each other.</para>
        /// <para>Multiplayer: every peer reads its own save copy. Records are not compared across peers;
        /// the multiplayer save readiness agreement covers settings only.</para>
        /// <para>Failures: every callback exception is isolated, logged and counted in
        /// <see cref="CallbackFailures"/>; nothing is thrown into the game or the Extender. A throwing
        /// load callback for a restored record makes later saves of that mission keep the loaded bytes.</para>
        /// <para>Lifetime: kept and unknown entries belong to one mission; they are dropped at the next
        /// load and at a map unload outside a load.</para>
        /// </remarks>
        /// <param name="registration">Validated and copied; see its members for the record contract.</param>
        /// <param name="handle">Diagnostics of the new registration, or null on failure.</param>
        /// <param name="reason">Empty on success; otherwise the first problem found.</param>
        /// <returns>False for an invalid registration, a duplicate identifier (ordinal, ignoring case, as
        /// the Extender's archive names are), an identifier the Extender rejects, or when the load event
        /// subscriptions could not be created.</returns>
        public static bool TryRegister(VersionedSaveDataRegistration registration,
            out VersionedSaveDataHandle handle, out string reason) =>
            VersionedSaveDataRuntime.TryRegister(registration, out handle, out reason);

        /// <summary>Isolated exceptions from capture, load, legacy reader and migration callbacks.</summary>
        public static long CallbackFailures => VersionedSaveDataRuntime.CallbackFailures;
    }

    // Binds the pure registry to Script Extender 2.14.1: ModSaveDataAPI handlers per identifier
    // (API/ModSaveDataAPI.cs:110-146) and the MapLoader events that bracket every archive load
    // (Detours/BulkMapLoaderDetours.cs:171-193 OnLoadSave, :347-372 OnLoadMap, :206-233 OnUnloadMap).
    // The Extender delivers stored entries only inside those events, so they are the window; the
    // save-settings entries (MissionLifecycleService save/StartSave hooks, SavegameLoadCoordinator)
    // wrap only save loads, from outside the native load, and are used here to identify multiplayer
    // saves. Subscriptions start with the first registration, so an unused service adds nothing.
    internal static class VersionedSaveDataRuntime
    {
        private static readonly object Sync = new object();
        private static ManualLogSource log;
        private static VersionedSaveDataRegistry registry;
        private static List<IDisposable> subscriptions;

        internal static long CallbackFailures
        {
            get { lock (Sync) return registry?.CallbackFailures ?? 0; }
        }

        internal static bool TryRegister(VersionedSaveDataRegistration registration,
            out VersionedSaveDataHandle handle, out string reason)
        {
            VersionedSaveDataRegistry current;
            lock (Sync)
            {
                if (registry == null)
                {
                    log = Logger.CreateLogSource("APIShared VersionedSaveData");
                    registry = new VersionedSaveDataRegistry(RegisterWithExtender,
                        message => NativeApiLog.Warning(log, message), message => NativeApiLog.Error(log, message));
                }
                if (subscriptions == null && !TrySubscribe(out reason))
                {
                    handle = null;
                    return false;
                }
                current = registry;
            }
            return current.TryRegister(registration, out handle, out reason);
        }

        private static bool RegisterWithExtender(string identifier, Func<VersionedSaveContext, byte[]> save, Action<byte[]> load) =>
            ModSaveDataAPI.Instance.RegisterModDataHandler(identifier,
                context => context == null ? null :
                    save(new VersionedSaveContext(context.IsSaveFile, context.IsMapEditorSave, context.FilePath)),
                (data, context) => load(data));

        private static bool TrySubscribe(out string reason)
        {
            var created = new List<IDisposable>();
            try
            {
                created.Add(MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(OnLoadSave));
                created.Add(MapLoaderR3EventHooks.OnLoadMap.Observable.Subscribe(OnLoadMap));
                created.Add(MapLoaderR3EventHooks.OnUnloadMap.Observable.Subscribe(OnUnloadMap));
                subscriptions = created;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                foreach (IDisposable subscription in created) { try { subscription.Dispose(); } catch { } }
                reason = "Could not observe Script Extender load events: " + ex.Message;
                NativeApiLog.Error(log, reason);
                return false;
            }
        }

        // These handlers must never throw: the Extender's load detours catch an exception and call
        // the native loader a second time (BulkMapLoaderDetours.cs:188-192, :367-371).
        internal static void OnLoadSave(LoadSaveGameEventArgs args)
        {
            try
            {
                if (args == null) return;
                if (args.Phase == EventHookPhase.Pre)
                    registry?.BeginLoad(args.LoadingEditorMap ? VersionedSaveLoadSource.EditorMap : VersionedSaveLoadSource.SaveGame,
                        args.FileName);
                else if (args.Phase == EventHookPhase.Post) registry?.EndLoad();
            }
            catch (Exception ex) { Report(ex); }
        }

        // Multiplayer saves load through OnLoadMap, whose LoadContext the Extender marks as a map
        // (SavegameModSettings.BeginLoad). The confirmed multiplayer save entry owned by
        // SavegameLoadCoordinator identifies them; the native flag is a second source.
        internal static void OnLoadMap(MapLoadEventArgs args)
        {
            try
            {
                if (args == null) return;
                if (args.Phase == EventHookPhase.Pre)
                    registry?.BeginLoad(args.bMultiplayerSave != 0 || SavegameLoadCoordinator.IsLoadingMultiplayerSave
                        ? VersionedSaveLoadSource.MultiplayerSave : VersionedSaveLoadSource.MapToPlay, args.FileName);
                else if (args.Phase == EventHookPhase.Post) registry?.EndLoad();
            }
            catch (Exception ex) { Report(ex); }
        }

        internal static void OnUnloadMap(MapUnloadEventArgs args)
        {
            try { registry?.Unload(); }
            catch (Exception ex) { Report(ex); }
        }

        private static void Report(Exception error)
        {
            try { NativeApiLog.Error(log, "Versioned save data load event failed: " + error); } catch { }
        }
    }
}
