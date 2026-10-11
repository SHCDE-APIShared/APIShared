using APIShared.Internal;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace APIShared.Workshop
{
    internal static class WorkshopText
    {
        internal static bool German => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        internal static string Pick(string en, string de) => German ? de : en;
        internal static string External => Pick("Managed by external mod manager", "Durch externen Modmanager verwaltet");
    }
    // This is presentation state, not synchronized lobby settings or savegame data.
    internal sealed class WorkshopViewModel : INotifyPropertyChanged
    {
        internal static readonly WorkshopViewModel Instance = new WorkshopViewModel();
        private readonly WorkshopClient client = new WorkshopClient(APISharedPlugin.PluginGuid);
        private WorkshopInstallPlan install;
        private WorkshopUploadPlan upload;
        private WorkshopOperation observed;
        private WorkshopCatalogItem selected, owned;
        private string status = "", source = "", title = "", description = "", preview = "", itemId = "", changeNote = "", visibility = "Private";
        private bool busy;
        private int uploadRevision, installRevision;
        private string packageId = "", packageVersion = "", minimumApi = APISharedPlugin.PluginVersion;
        public string PackageId { get => packageId; set => Set(ref packageId, value, nameof(PackageId)); }
        public string PackageVersion { get => packageVersion; set => Set(ref packageVersion, value, nameof(PackageVersion)); }
        public string MinimumAPISharedVersion { get => minimumApi; set => Set(ref minimumApi, value, nameof(MinimumAPISharedVersion)); }
        public string DraftLabel => WorkshopText.Pick("New loose package: ID / version / APIShared minimum (leave ID empty to use existing manifest)", "Neues unverpacktes Paket: ID / Version / APIShared-Minimum (ID leer lassen für vorhandenes Manifest)");
        private bool metadataOnly;
        public bool MetadataOnly { get => metadataOnly; set { metadataOnly = value; upload = null; uploadRevision++; Changed(nameof(MetadataOnly)); } }
        public string MetadataLabel => WorkshopText.Pick("Update metadata only (retain published files)", "Nur Metadaten ändern (veröffentlichte Dateien behalten)");
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        private void Set(ref string field, string value, string property) { field = value ?? ""; upload = null; uploadRevision++; Changed(property); }
        public ObservableCollection<WorkshopCatalogItem> Packages { get; } = new ObservableCollection<WorkshopCatalogItem>();
        public ObservableCollection<WorkshopCatalogItem> OwnedItems { get; } = new ObservableCollection<WorkshopCatalogItem>();
        public string[] Visibilities { get; } = { "Private", "FriendsOnly", "Unlisted", "Public" };
        public WorkshopCatalogItem SelectedPackage { get => selected; set { selected = value; install = null; installRevision++; Changed(nameof(SelectedPackage)); Changed(nameof(SelectedInfo)); } }
        public WorkshopCatalogItem SelectedOwnedItem { get => owned; set { owned = value; ItemId = value?.WorkshopId.ToString() ?? ""; Changed(nameof(SelectedOwnedItem)); } }
        public string SelectedInfo => selected == null ? "" : selected.Status + "\nWorkshop: " + selected.WorkshopId + "\nInstalled: " + (selected.InstalledVersion ?? "—") + "\nRemote: " + (selected.Package?.Version ?? "—");
        public string SourceDirectory { get => source; set => Set(ref source, value, nameof(SourceDirectory)); }
        public string Title { get => title; set => Set(ref title, value, nameof(Title)); }
        public string Description { get => description; set => Set(ref description, value, nameof(Description)); }
        public string PreviewFile { get => preview; set => Set(ref preview, value, nameof(PreviewFile)); }
        public string ItemId { get => itemId; set => Set(ref itemId, value, nameof(ItemId)); }
        public string ChangeNote { get => changeNote; set => Set(ref changeNote, value, nameof(ChangeNote)); }
        public string Visibility { get => visibility; set => Set(ref visibility, value, nameof(Visibility)); }
        public string Status { get => status; private set { status = value; Changed(nameof(Status)); } }
        public string Ownership => client.IsExternallyManaged ? WorkshopText.External : WorkshopText.Pick("APIShared manages declared Payload files; Script Extender manages maps.", "APIShared verwaltet deklarierte Payload-Dateien; der Script Extender verwaltet Maps.");
        public string InstalledTitle => WorkshopText.Pick("Installed packages", "Installierte Pakete");
        public string PublishingTitle => WorkshopText.Pick("Publish", "Veröffentlichen");
        public string RefreshLabel => WorkshopText.Pick("Refresh", "Aktualisieren");
        public string PrepareLabel => WorkshopText.Pick("Prepare installation / update", "Installation / Update vorbereiten");
        public string ReinstallLabel => WorkshopText.Pick("Prepare explicit reinstall", "Ausdrückliche Neuinstallation vorbereiten");
        public string DowngradeLabel => WorkshopText.Pick("Prepare explicit downgrade", "Ausdrücklichen Downgrade vorbereiten");
        public string RemoveLabel => WorkshopText.Pick("Prepare removal", "Entfernung vorbereiten");
        public string ApplyLabel => WorkshopText.Pick("Confirm plan, apply and restart", "Plan bestätigen, anwenden und neu starten");
        public string PrepareUploadLabel => WorkshopText.Pick("Validate and prepare upload", "Upload prüfen und vorbereiten");
        public string PublishLabel => WorkshopText.Pick("Confirm upload to Steam", "Upload zu Steam bestätigen");
        public string OwnedLabel => WorkshopText.Pick("Load my Workshop items", "Meine Workshop-Items laden");
        public string ProgressLabel => WorkshopText.Pick("Refresh upload progress", "Uploadfortschritt aktualisieren");
        public string SourceLabel => WorkshopText.Pick("Prepared package folder", "Vorbereiteter Paketordner");
        public string ItemLabel => WorkshopText.Pick("Workshop ID (empty = new item / saved retry)", "Workshop-ID (leer = neues Item / gespeicherte Wiederholung)");
        public string TitleLabel => WorkshopText.Pick("Title", "Titel");
        public string DescriptionLabel => WorkshopText.Pick("Description", "Beschreibung");
        public string PreviewLabel => WorkshopText.Pick("Preview image file", "Vorschaubilddatei");
        public string ChangeLabel => WorkshopText.Pick("Change note", "Änderungsnotiz");
        public string VisibilityLabel => WorkshopText.Pick("Visibility", "Sichtbarkeit");
        public string Terms => WorkshopText.Pick("By uploading you agree to the Steam Workshop terms. Closing this view does not cancel a submitted upload.", "Mit dem Upload akzeptierst du die Steam-Workshop-Bedingungen. Schließen dieser Ansicht bricht einen eingereichten Upload nicht ab.");
        public string TermsLabel => WorkshopText.Pick("Steam Workshop terms", "Steam-Workshop-Bedingungen");
        public ICommand RefreshCommand { get; }
        public ICommand PrepareCommand { get; }
        public ICommand ReinstallCommand { get; }
        public ICommand DowngradeCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand PrepareUploadCommand { get; }
        public ICommand PublishCommand { get; }
        public ICommand OwnedCommand { get; }
        public ICommand ProgressCommand { get; }
        public ICommand TermsCommand { get; }
        private WorkshopViewModel()
        {
            RefreshCommand = new Command(Refresh);
            PrepareCommand = new Command(() => Prepare(selected?.InstalledVersion == null ? WorkshopAction.Install : WorkshopAction.Update));
            ReinstallCommand = new Command(() => Prepare(WorkshopAction.Reinstall));
            DowngradeCommand = new Command(() => Prepare(WorkshopAction.Downgrade));
            RemoveCommand = new Command(() => Prepare(WorkshopAction.Remove));
            ApplyCommand = new Command(() => Run(async () => { if (install == null) throw new InvalidOperationException("Prepare a plan first."); Observe(await client.ApplyAsync(install)); }));
            PrepareUploadCommand = new Command(() => Run(async () => {
                int expected = uploadRevision;
                var candidate = await client.PrepareUploadAsync(new WorkshopUploadOptions { SourceDirectory = SourceDirectory, Title = Title, Description = Description,
                    PreviewFile = PreviewFile, Visibility = Visibility, ChangeNote = ChangeNote, MetadataOnly = MetadataOnly, WorkshopId = string.IsNullOrWhiteSpace(ItemId) ? 0 : WorkshopPackageCodec.Id(ItemId),
                    Draft = string.IsNullOrWhiteSpace(PackageId) ? null : new WorkshopPackageDraft(PackageId, PackageVersion, MinimumAPISharedVersion, "2.14.0") });
                if (expected != uploadRevision) throw new InvalidOperationException("Upload form changed during preparation; prepare again.");
                upload = candidate;
                Status = "Ready: " + upload.PackageId + " " + upload.Version + "\n" + string.Join("\n", upload.Files) +
                    (upload.Package.Bootstrap == null ? "\n" + WorkshopText.Pick("A warning bootstrap is recommended for users without APIShared; the template is in Workshop/Bootstrap.", "Für Nutzer ohne APIShared wird ein Warn-Bootstrap empfohlen; die Vorlage liegt unter Workshop/Bootstrap.") : "") + "\n" + Terms;
            }));
            PublishCommand = new Command(() => Run(() => { if (upload == null) throw new InvalidOperationException("Prepare the upload first."); Observe(client.Publish(upload)); return Task.CompletedTask; }));
            OwnedCommand = new Command(() => Run(async () => { var items = await client.GetOwnedItemsAsync(); OwnedItems.Clear(); foreach (var item in items) OwnedItems.Add(item); }));
            ProgressCommand = new Command(() => Run(() => { WorkshopSteamUpload.RefreshProgress(); return Task.CompletedTask; }));
            TermsCommand = new Command(() => Steamworks.SteamFriends.ActivateGameOverlayToWebPage("https://steamcommunity.com/sharedfiles/workshoplegalagreement"));
        }
        internal void Refresh() => Run(async () => { var items = await client.GetCatalogAsync(); Packages.Clear(); foreach (var item in items) Packages.Add(item); Changed(nameof(Ownership)); Status = Ownership; });
        private void Prepare(WorkshopAction action) => Run(async () => {
            if (selected == null) throw new InvalidOperationException("Select a package first.");
            int expected = installRevision;
            var candidate = await client.PrepareInstallAsync(selected.WorkshopId, action);
            if (expected != installRevision) throw new InvalidOperationException("Selected package changed during preparation; prepare again.");
            install = candidate;
            Status = action + ": " + install.PackageId + " " + install.Version + "\n" + string.Join("\n", install.Files.Select(f => "+ " + f.Path).Concat(install.RemovedFiles.Select(f => "- " + f))) + "\n" + string.Join("\n", install.Diagnostics) + "\n" + ApplyLabel;
        });
        private void Observe(WorkshopOperation operation)
        {
            if (observed != null) observed.Changed -= OnOperation;
            observed = operation; observed.Changed += OnOperation; OnOperation(observed);
        }
        private void OnOperation(WorkshopOperation operation) => Status = operation.State + ": " + operation.Diagnostic + "\nWorkshop: " + operation.WorkshopId;
        private async void Run(Func<Task> action)
        {
            if (busy) return; busy = true;
            try { await action(); }
            catch (Exception ex) { Status = ex.Message; WorkshopRuntime.Log("Workshop view: " + ex.Message); }
            finally { busy = false; }
        }
        private sealed class Command : ICommand
        {
            private readonly Action action;
            internal Command(Action action) { this.action = action; }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => action();
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
