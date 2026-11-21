using APIShared.Localization;
using BepInEx.Logging;
using Noesis;
using SHCDESE.NoesisUtil;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace APIShared.ModSettings
{
    /// <summary>Connects a patched lobby chat host or Coop chat badge to incoming preset offers on the Unity thread.</summary>
    public static class LobbyPresetOffers
    {
        /// <summary>Opt-in for the APIShared chat-area offer list or passive badge.</summary>
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(LobbyPresetOffers), new PropertyMetadata(false, OnChanged));

        /// <summary>Reads the offer presentation opt-in.</summary>
        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        /// <summary>Changes the offer presentation opt-in on the Unity thread.</summary>
        public static void SetEnabled(DependencyObject element, bool enabled) => element.SetValue(EnabledProperty, enabled);

        private static ManualLogSource log;
        private static ManualLogSource Log => log ?? (log = Logger.CreateLogSource("APIShared.PresetOffers"));

        private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        {
            if (!(element is ContentControl) && !(element is TextBlock)) return;
            var host = (FrameworkElement)element;
            host.Loaded -= OnLoaded;
            host.Unloaded -= OnUnloaded;
            if (!(bool)args.NewValue)
            {
                host.DataContext = null;
                return;
            }

            host.Loaded += OnLoaded;
            host.Unloaded += OnUnloaded;
            if (host.IsLoaded) Attach(host);
        }

        private static void OnLoaded(object sender, RoutedEventArgs args) => Attach((FrameworkElement)sender);

        private static void OnUnloaded(object sender, RoutedEventArgs args)
        {
            ((FrameworkElement)sender).DataContext = null;
        }

        private static void Attach(FrameworkElement host)
        {
            try
            {
                if (host is ContentControl content && content.Content == null)
                {
                    using (var stream = typeof(LobbyPresetOffers).Assembly.GetManifestResourceStream("APIShared.LobbyPresetOffers.xaml"))
                    using (var reader = new StreamReader(stream))
                        content.Content = GUI.ParseXaml(reader.ReadToEnd());
                }

                host.DataContext = LobbyPresetOffersModel.Instance;
            }
            catch (Exception error)
            {
                NativeApiLog.Error(Log, "Could not attach the lobby preset offers view: " + error);
            }
        }

        internal static void Report(Exception error)
        {
            NativeApiLog.Error(Log, "Could not import a shared lobby preset: " + error);
        }
    }

    // One model carries the user's opt-in across all five lobby chat views. It is
    // rooted by the Unity-thread offer service, independently of the plugin object.
    internal sealed class LobbyPresetOffersModel : INotifyPropertyChanged
    {
        internal static readonly LobbyPresetOffersModel Instance = new LobbyPresetOffersModel();

        private PresetShareOffer selected;
        private string name = string.Empty;
        private string status = string.Empty;
        private bool pending;
        private bool selectedAvailable;
        private Guid lobbySession;

        private LobbyPresetOffersModel()
        {
            SaveCommand = new RelayCommand(Save);
            CloseCommand = new RelayCommand(Close);
            PresetShareService.Changed += Refresh;
            Refresh();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public OfferRow[] Offers { get; private set; } = Array.Empty<OfferRow>();
        public RelayCommand SaveCommand { get; }
        public RelayCommand CloseCommand { get; }

        public string Name
        {
            get => name;
            set
            {
                string next = value ?? string.Empty;
                if (name == next) return;
                name = next;
                Notify(nameof(Name));
                Notify(nameof(CanSave));
            }
        }

        public string NameLabel => UiText.Get("PresetShare.Name");
        public string NameHelp => UiText.Get("PresetShare.NameHelp");
        public string SaveLabel => UiText.Get("PresetShare.Save");
        public string SaveHelp => UiText.Get("PresetShare.SaveHelp");
        public string CloseLabel => UiText.Get("PresetShare.Cancel");
        public string CloseHelp => UiText.Get("PresetShare.CancelHelp");
        public string CloseStatusHelp => UiText.Get("PresetShare.CloseStatus");
        public string Status => status;
        public string Badge => UiText.Get("PresetShare.PendingOffers", "Count", Offers.Length);
        public bool CanInteract => !pending;
        public bool CanSave => !pending && selectedAvailable && !string.IsNullOrWhiteSpace(name);
        public Visibility ListVisibility => Offers.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DialogVisibility => selected == null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility StatusVisibility => status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Visibility StatusCloseVisibility => selected == null && status.Length != 0 ? Visibility.Visible : Visibility.Collapsed;

        private void Refresh()
        {
            Guid nextLobby = PresetShareService.LobbySession;
            bool sessionChanged = nextLobby != lobbySession;
            if (sessionChanged)
            {
                lobbySession = nextLobby;
                selected = null;
                selectedAvailable = false;
                pending = false;
                status = string.Empty;
            }

            var offers = PresetShareService.Offers;
            if (!Offers.Select(row => row.Offer).SequenceEqual(offers))
            {
                Offers = offers.Select(offer => new OfferRow(offer, () => Open(offer))).ToArray();
                Notify(nameof(Offers));
                Notify(nameof(Badge));
                Notify(nameof(ListVisibility));
            }

            bool available = selected != null && offers.Any(offer => ReferenceEquals(offer, selected));
            if (selectedAvailable == available && !sessionChanged) return;

            selectedAvailable = available;
            if (selected != null && !pending && !available && string.IsNullOrEmpty(status))
                status = UiText.Get("PresetShare.Unavailable");
            NotifyDialog();
        }

        private void Open(PresetShareOffer offer)
        {
            if (pending || !IsCurrent(offer)) return;
            selected = offer;
            selectedAvailable = true;
            Name = offer.PresetName;
            status = string.Empty;
            NotifyDialog();
        }

        private void Close()
        {
            if (pending) return;
            selected = null;
            selectedAvailable = false;
            status = string.Empty;
            NotifyDialog();
        }

        private void Save()
        {
            if (!CanSave || selected == null) return;

            PresetShareOffer offer = selected;
            Guid requestSession = lobbySession;
            string chosenName = name.Trim();
            pending = true;
            status = UiText.Get("PresetShare.Downloading");
            NotifyDialog();
            if (!PresetShareService.TryRequest(offer, result => Complete(offer, chosenName, requestSession, result), out var failure) &&
                requestSession == lobbySession)
            {
                pending = false;
                selectedAvailable = IsCurrent(offer);
                status = PresetShareText.Failure(failure);
                NotifyDialog();
            }
        }

        private void Complete(PresetShareOffer offer, string chosenName, Guid session, PresetShareResult result)
        {
            if (session != PresetShareService.LobbySession || !ReferenceEquals(offer, selected) || !pending) return;
            pending = false;
            if (result.Failure != PresetShareFailure.None)
            {
                status = PresetShareText.Failure(result.Failure);
                NotifyDialog();
                return;
            }

            try
            {
                var endpoint = FindEndpoint(offer.ModGuid);
                if (endpoint == null)
                {
                    status = UiText.Get("PresetShare.Unavailable");
                }
                else
                {
                    endpoint.System_ImportSharedPresetJson(result.Json, chosenName);
                    selected = null;
                    selectedAvailable = false;
                    status = UiText.Get("PresetShare.Saved", "Name", chosenName);
                    PresetShareService.Dismiss(offer);
                }
            }
            catch (Exception error)
            {
                LobbyPresetOffers.Report(error);
                status = UiText.Get("PresetShare.ImportFailed");
            }

            NotifyDialog();
        }

        private static bool IsCurrent(PresetShareOffer offer) =>
            PresetShareService.Offers.Any(candidate => ReferenceEquals(candidate, offer));

        private static PresetLobbyModSettingsViewModel FindEndpoint(string guid) =>
            ModSettingsApplication.Endpoints.FirstOrDefault(entry =>
                string.Equals(entry.Key, guid, StringComparison.OrdinalIgnoreCase)).Value;

        private void NotifyDialog()
        {
            Notify(nameof(CanInteract));
            Notify(nameof(CanSave));
            Notify(nameof(DialogVisibility));
            Notify(nameof(Status));
            Notify(nameof(StatusVisibility));
            Notify(nameof(StatusCloseVisibility));
        }

        private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

        internal sealed class OfferRow
        {
            internal OfferRow(PresetShareOffer offer, Action open)
            {
                Offer = offer;
                OpenCommand = new RelayCommand(open);
                Caption = UiText.Get("PresetShare.OfferLine", "Sender", Offer.SenderName,
                    "Preset", Offer.PresetName, "Mod", FindEndpoint(Offer.ModGuid)?.System_SharedPresetModName ?? Offer.ModGuid);
            }

            internal PresetShareOffer Offer { get; }
            public RelayCommand OpenCommand { get; }
            public string Caption { get; }
            public string Help => UiText.Get("PresetShare.OfferHelp");
        }
    }
}
