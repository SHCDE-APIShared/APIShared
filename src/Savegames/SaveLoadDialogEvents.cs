using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using APIShared.Events;

namespace APIShared.Savegames
{
    /// <summary>Process-wide load/save requester coordination; APIShared is the sole managed hook owner.</summary>
    public static class SaveLoadDialogEvents
    {
        /// <summary>Whether requester bindings and permanent hooks initialized after LibraryLoaded.
        /// Registrations may be queued earlier; consumers requiring this service must check readiness before publishing their runtime.</summary>
        public static bool IsAvailable => initialized;

        private delegate void OpenDialog(Enums.RequesterTypes type, Action<string, FileHeader> confirm, Action cancel, int count, bool skirmish, bool trails);
        private delegate void ClickDialog(HUD_LoadSaveRequester view, int function, bool doubleClick);
        private delegate void ExecuteDialog(HUD_LoadSaveRequester view);
        private static readonly ManagedInterceptionHook<OpenDialog> OpenHook = new ManagedInterceptionHook<OpenDialog>();
        private static readonly ManagedInterceptionHook<ClickDialog> ClickHook = new ManagedInterceptionHook<ClickDialog>();
        private static readonly ManagedInterceptionHook<ExecuteDialog> ExecuteHook = new ManagedInterceptionHook<ExecuteDialog>();
        private static readonly object Sync = new object();
        private static SaveLoadDialogRequest.Entry[] registrations = Array.Empty<SaveLoadDialogRequest.Entry>();
        private static Session current;
        private static long generation;
        private static ManualLogSource log;
        private static SavegameLoadOptions options;
        private static bool initialized;
        private static readonly SaveLoadDialogDispatch Dispatch = new SaveLoadDialogDispatch(Report);
        private sealed class Session
        {
            internal SaveLoadDialogRequest Request;
            internal Enums.RequesterTypes Type;
            internal bool Skirmish, Trails, IsSaveLoad;
            internal HUD_LoadSaveRequester View;
            internal ListView Files;
            internal CheckBox Check;
            internal string DecisionPath;
            internal bool? Decision;
            internal string SelectedPath;
        }

        /// <summary>Registers a contribution for all future requests. Owner/ID pairs are unique; order is
        /// ascending, then owner GUID and ID ordinal. Call on the Unity thread during startup.</summary>
        /// <remarks>Registration is process-lifetime, without replay. Delegates are copied. Callback state is
        /// isolated per owner/request. Required gates/preparation fail closed; observation/cleanup failures
        /// are logged independently. Do not hook the same requester separately.</remarks>
        /// <exception cref="ArgumentException">Owner, ID or callbacks are missing.</exception>
        /// <exception cref="InvalidOperationException">The owner already registered this ID.</exception>
        public static void Register(string ownerGuid, string registrationId, SaveLoadDialogCallbacks callbacks, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || callbacks == null)
                throw new ArgumentException("Owner GUID, registration ID and callbacks are required.");
            lock (Sync)
            {
                if (registrations.Any(e => e.Owner == ownerGuid && e.Id == registrationId))
                    throw new InvalidOperationException("Duplicate load/save requester registration.");
                registrations = registrations.Concat(new[] { new SaveLoadDialogRequest.Entry(callbacks) { Owner = ownerGuid, Id = registrationId, Order = order } })
                    .OrderBy(e => e.Order).ThenBy(e => e.Owner, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
            }
        }

        internal static void Initialize(ConfigFile config, ManualLogSource logger)
        {
            if (initialized) return;
            log = logger;
            // Old ExtendedData publishes its own requester hook. Do not install a competing hook;
            // compatible packages advertise the broker integration before LibraryLoaded.
            if (Chainloader.PluginInfos.TryGetValue("ExtendedData_Serp", out var plugin) &&
                plugin.Instance.GetType().GetField("UsesApiSharedSaveLoadDialog")?.GetRawConstantValue() as bool? != true)
                throw new NotSupportedException("Update ExtendedData together with APIShared: the installed ExtendedData still owns the load/save requester.");
            var preference = new SavegameLoadPreference(config, Report);
            options = new SavegameLoadOptions(() => preference.Value, preference.Set);
            GameXAMLManagerAPI.Instance.RegisterBinding("APISharedSavegameLoadOptionsHost", options);
            // Capture visibility before Vanilla ButtonClicked hides all requester views. The original
            // confirm callback runs later, including after editor overwrite confirmation.
            // RunOKAction also encloses delayed editor-overwrite callbacks. Closing just the
            // consumer callback would reopen a replacement before Vanilla remembers filenames.
            ExecuteHook.Install(typeof(HUD_LoadSaveRequester), "RunOKAction", typeof(void),
                Type.EmptyTypes, Execute, "SaveLoadDialogExecute");
            ClickHook.Install(typeof(HUD_LoadSaveRequester), nameof(HUD_LoadSaveRequester.ButtonClicked), typeof(void),
                new[] { typeof(int), typeof(bool) }, Click, "SaveLoadDialogClick");
            OpenHook.Install(typeof(HUD_LoadSaveRequester), nameof(HUD_LoadSaveRequester.OpenLoadSaveRequester), typeof(void),
                new[] { typeof(Enums.RequesterTypes), typeof(Action<string, FileHeader>), typeof(Action), typeof(int), typeof(bool), typeof(bool) }, Open, "SaveLoadDialogOpen");
            initialized = true;
            NativeApiLog.Debug(log, "Savegame requester and local choice preference initialized after startup cleanup.");
        }

        internal static bool IsSaveLoad(Enums.RequesterTypes type) =>
            type == Enums.RequesterTypes.LoadSinglePlayerGame || type == Enums.RequesterTypes.LoadSinglePlayerCoopGame ||
            type == Enums.RequesterTypes.LoadMultiplayerGame || type == Enums.RequesterTypes.LoadMultiplayerCoopGame;

        private static void Open(Enums.RequesterTypes type, Action<string, FileHeader> confirm, Action cancel, int count, bool skirmish, bool trails)
            => Dispatch.Open(() => OpenCore(type, confirm, cancel, count, skirmish, trails),
                () => current?.Request.Close(SaveLoadDialogOutcome.Replaced));

        private static void OpenCore(Enums.RequesterTypes type, Action<string, FileHeader> confirm, Action cancel, int count, bool skirmish, bool trails)
        {
            End(current, SaveLoadDialogOutcome.Replaced, true);
            var session = new Session { Type = type, IsSaveLoad = IsSaveLoad(type), Skirmish = skirmish, Trails = trails };
            SaveLoadDialogRequest.Entry[] snapshot;
            lock (Sync) snapshot = registrations;
            session.Request = new SaveLoadDialogRequest(++generation, type, snapshot, Report);
            current = session;
            if (session.IsSaveLoad) { SavegameModSettings.CancelLoadChoice(); options.Open(); }
            else options.Close();
            try
            {
                OpenHook.Original(type, (name, header) => Confirm(session, name, header, confirm), () =>
                {
                    Dispatch.Run(() =>
                    {
                        if (!ReferenceEquals(current, session)) return;
                        End(session, SaveLoadDialogOutcome.Cancelled, true);
                        cancel?.Invoke();
                    });
                }, count, skirmish, trails);
                if (!ReferenceEquals(current, session)) return;
                session.View = MainViewModel.viewModelLoaded ? MainViewModel.Instance.HUDLoadSaveRequester : null;
                session.Files = session.View?.FindName("FileList") as ListView;
                session.Check = session.View?.FindName("APISharedUseCurrentModSettings") as CheckBox;
                if (session.Files != null) session.Files.SelectionChanged += SelectionChanged;
                RefreshSelection(session);
                session.Request.Open();
            }
            catch { End(session, SaveLoadDialogOutcome.Failed, true); throw; }
        }

        private static void Click(HUD_LoadSaveRequester view, int function, bool doubleClick)
            => Dispatch.Run(() => ClickCore(view, function, doubleClick));

        private static void Execute(HUD_LoadSaveRequester view)
            => Dispatch.Run(() => ExecuteHook.Original(view));

        private static void ClickCore(HUD_LoadSaveRequester view, int function, bool doubleClick)
        {
            var session = current;
            if (function == 1 && session != null && ReferenceEquals(session.View, view) && session.IsSaveLoad)
            {
                RefreshSelection(session);
                session.DecisionPath = session.SelectedPath;
                bool visible = session.Check != null && session.Check.IsVisible && session.Check.IsEnabled &&
                    ReferenceEquals(session.Check.DataContext, options);
                session.Decision = visible ? (bool?)options.UseCurrentSavegameSettings : null;
            }
            ClickHook.Original(view, function, doubleClick);
        }

        private static void SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            var session = current;
            if (session != null && ReferenceEquals(sender, session.Files)) RefreshSelection(session);
        }
        private static void RefreshSelection(Session session)
        {
            string path = (session.Files?.SelectedItem as FileRow)?.fileHeader?.filePath;
            if (string.Equals(path, session.SelectedPath, StringComparison.OrdinalIgnoreCase)) return;
            session.SelectedPath = path; session.Decision = null; session.DecisionPath = null;
            if (session.IsSaveLoad) options.Select(SavegameModSettings.GetLoadChoiceState(path));
        }

        private static void Confirm(Session session, string name, FileHeader header, Action<string, FileHeader> original)
            => Dispatch.Run(() => ConfirmCore(session, name, header, original));

        private static void ConfirmCore(Session session, string name, FileHeader header, Action<string, FileHeader> original)
        {
            if (!ReferenceEquals(current, session) || session.Request.IsClosed) return;
            bool accepted;
            try
            {
                accepted = session.Request.Confirm(name, header, () =>
            {
                if (!session.IsSaveLoad) return true;
                bool? choice = string.Equals(session.DecisionPath, header?.filePath, StringComparison.OrdinalIgnoreCase) ? session.Decision : null;
                return SavegameModSettings.PrepareAutomaticLoadWithRestartCheck(header?.filePath, choice);
                }, () => original?.Invoke(name, header), () => { if (session.IsSaveLoad && ReferenceEquals(current, session)) SavegameModSettings.CancelLoadChoice(); });
            }
            catch { End(session, SaveLoadDialogOutcome.Failed, true); throw; }
            if (accepted || session.Request.IsClosed) End(session, SaveLoadDialogOutcome.Confirmed, false);
            else if (ReferenceEquals(current, session) && MainViewModel.viewModelLoaded)
            {
                // Vanilla already hid the dialog before invoking its callback. Preserve the request
                // and selection when a gate/restart preparation rejects the confirmation.
                var model = MainViewModel.Instance;
                model.Show_HUD_LoadSaveRequesterTrails = session.Trails;
                bool mp = !session.Trails && (session.Skirmish || session.Type == Enums.RequesterTypes.LoadMultiplayerGame || session.Type == Enums.RequesterTypes.LoadMultiplayerCoopGame);
                model.Show_HUD_LoadSaveRequesterMP = mp;
                model.Show_HUD_LoadSaveRequester = !session.Trails && !mp;
                HUD_ConfirmationPopup.ShowConfirmationOKMessage(APIShared.Localization.UiText.Get("Savegames.OperationNotReady"), null, APIShared.Localization.UiText.Get("Savegames.SettingsTitle"), false);
            }
        }

        private static void End(Session session, SaveLoadDialogOutcome outcome, bool cancelPreparation)
        {
            if (session == null) return;
            if (session.Files != null) session.Files.SelectionChanged -= SelectionChanged;
            if (ReferenceEquals(current, session))
            {
                current = null;
                options.Close();
                if (cancelPreparation && session.IsSaveLoad) SavegameModSettings.CancelLoadChoice();
            }
            session.Request.Close(outcome);
        }
        private static void Report(Exception error) => NativeApiLog.Error(log, "Load/save requester: " + error);
    }
}
