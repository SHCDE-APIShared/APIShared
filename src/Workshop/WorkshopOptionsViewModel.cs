using APIShared.Localization;
using BepInEx;
using Noesis;
using SHCDESE.API;
using SHCDESE.NoesisUtil;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Path = System.IO.Path;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace APIShared.Workshop
{
    internal sealed class WorkshopOptionsViewModel : INotifyPropertyChanged
    {
        private readonly RelayCommand refresh;
        private readonly RelayCommand prepare;
        private readonly RelayCommand confirm;
        private readonly RelayCommand cancel;
        private WorkshopOption selected;
        private WorkshopInstallPlan preview;
        private bool busy, visible;
        private int viewGeneration;

        internal static void Register(BaseUnityPlugin plugin)
        {
            var viewModel = new WorkshopOptionsViewModel();
            var manager = GameXAMLManagerAPI.Instance;
            manager.RegisterLobbyModSettings(plugin, "APIShared", viewModel, "APISharedUI/Settings.xaml", useBuiltInPersistence: false);
            var entry = manager.RegisteredModSettings.SingleOrDefault(tab => ReferenceEquals(tab.ViewModel, viewModel));
            if (entry == null) throw new InvalidOperationException("APIShared Mod Options registration failed.");
            entry.View.Loaded += (_, __) => {
                viewModel.visible = true;
                viewModel.Changed();
                if (!viewModel.busy) viewModel.Refresh();
            };
            entry.View.Unloaded += (_, __) => {
                viewModel.visible = false;
                viewModel.viewGeneration++;
                viewModel.preview = null;
            };
        }

        private WorkshopOptionsViewModel()
        {
            refresh = new RelayCommand(Refresh, () => !busy);
            prepare = new RelayCommand(Prepare, () => !busy && preview == null && selected?.Item.CanInstall == true);
            confirm = new RelayCommand(Confirm, () => !busy && preview != null);
            cancel = new RelayCommand(() => { preview = null; Changed(); }, () => !busy && preview != null);
        }

        public string WorkshopTitle => UiText.Get("Workshop.Options.Title");
        public string PackageLabel => UiText.Get("Workshop.Options.Package");
        public string RefreshLabel => UiText.Get("Workshop.Options.Refresh");
        public string OffersLabel => UiText.Get("Workshop.Options.Offers");
        public string OffersHelp => UiText.Get("Workshop.Options.OffersHelp");
        public string PrepareLabel => UiText.Get("Workshop.Options.Prepare");
        public string PrepareHelp => UiText.Get("Workshop.Options.RestartRequired");
        public string ConfirmLabel => UiText.Get("Workshop.Options.Confirm");
        public string CancelLabel => UiText.Get("Workshop.Options.Cancel");
        public ICommand RefreshCommand => refresh;
        public ICommand PrepareCommand => prepare;
        public ICommand ConfirmCommand => confirm;
        public ICommand CancelCommand => cancel;
        public IReadOnlyList<WorkshopOption> Packages { get; private set; } = Array.Empty<WorkshopOption>();
        public string Message { get; private set; } = string.Empty;
        public string PendingMessage => WorkshopRuntime.SettingsInstallPending ? UiText.Get("Workshop.Options.RestartPending") : string.Empty;
        public Visibility PreviewVisibility => preview == null ? Visibility.Collapsed : Visibility.Visible;
        public bool CanChangeOffers => !busy && preview == null && selected != null;
        public bool CanSelectPackage => !busy;
        public string PreviewText => preview == null ? string.Empty : WorkshopStartupSummary.Describe(preview, GameLocale.Current()) +
            "\n" + UiText.Get("Workshop.Options.PreviewFiles", "Write", preview.Files.Count, "Remove", preview.RemovedFiles.Count) +
            "\n" + string.Join("\n", preview.Package.Roots.Concat(preview.Diagnostics)) +
            "\n\n" + UiText.Get("Workshop.Options.RestartRequired");

        public WorkshopOption Selected
        {
            get => selected;
            set
            {
                if (ReferenceEquals(selected, value)) return;
                selected = value;
                preview = null;
                Message = string.Empty;
                Changed();
            }
        }

        public bool StartupOffers
        {
            get => selected != null && WorkshopRuntime.OffersEnabled(selected.Item.WorkshopId);
            set
            {
                if (!CanChangeOffers || value == StartupOffers) return;
                try { WorkshopRuntime.SetOffersEnabled(selected.Item.WorkshopId, value); }
                catch (Exception ex) { ReportError("save offer preference", ex); }
                Changed();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingMessage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanChangeOffers)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSelectPackage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartupOffers)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewText)));
            refresh.RaiseCanExecuteChanged();
            prepare.RaiseCanExecuteChanged();
            confirm.RaiseCanExecuteChanged();
            cancel.RaiseCanExecuteChanged();
        }

        private async void Refresh()
        {
            if (busy) return;
            await RunAsync("refresh", async () => {
                preview = null;
                ulong? selectedId = selected?.Item.WorkshopId;
                var packages = new List<WorkshopOption>();
                var catalog = await WorkshopRuntime.GetCatalogAsync();
                foreach (var item in catalog.Where(item => item.IsSubscribed && (item.Package != null || item.Receipt != null))) {
                    try {
                        var evidence = await WorkshopRuntime.InspectLocalAsync(item);
                        packages.Add(new WorkshopOption(item, evidence.Status));
                    } catch (Exception ex) {
                        packages.Add(new WorkshopOption(item, null));
                        WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic,
                            WorkshopDiagnostics.InstallationContext(item, "unknown", "mod options local inspection"), ex);
                    }
                }
                Packages = packages.OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Packages)));
                selected = Packages.FirstOrDefault(package => package.Item.WorkshopId == selectedId) ?? Packages.FirstOrDefault();
                Message = Packages.Count == 0 ? UiText.Get("Workshop.Options.Empty") : string.Empty;
            });
        }

        private async void Prepare()
        {
            if (!prepare.CanExecute(null)) return;
            var item = selected.Item;
            int generation = viewGeneration;
            await RunAsync("preview", async () => {
                var plan = await WorkshopRuntime.PrepareSettingsAsync(item);
                if (visible && generation == viewGeneration && ReferenceEquals(selected?.Item, item)) preview = plan;
            });
        }

        private async void Confirm()
        {
            if (!confirm.CanExecute(null)) return;
            var plan = preview;
            preview = null;
            await RunAsync("confirmed installation", async () => {
                var operation = await WorkshopRuntime.ApplySettingsAsync(plan);
                Message = operation.State == WorkshopOperationState.AwaitingRestart
                    ? string.Empty
                    : operation.Failure != null ? FailureText(operation.Failure) : operation.Diagnostic;
            });
        }

        private async Task RunAsync(string phase, Func<Task> work)
        {
            busy = true;
            Message = UiText.Get("Workshop.Options.Working");
            Changed();
            try {
                await work();
                if (phase == "preview") Message = string.Empty;
            } catch (Exception ex) { ReportError(phase, ex); }
            finally { busy = false; Changed(); }
        }

        private void ReportError(string phase, Exception ex)
        {
            var item = selected?.Item;
            WorkshopDiagnostics.Failure(WorkshopRuntime.WriteDiagnostic, item == null ? "Workshop Mod Options " + phase :
                WorkshopDiagnostics.InstallationContext(item, "see startup log", "mod options " + phase), ex);
            Message = FailureText(ex);
        }

        private static string FailureText(Exception ex)
        {
            var conflicts = WorkshopDiagnostics.OwnershipConflicts(ex);
            if (conflicts == null) return ex.Message;
            return UiText.Get("Workshop.Options.Conflicts") + "\n" + string.Join("\n", conflicts.Select(conflict =>
                conflict.Kind == "plugin" ? Path.GetDirectoryName(conflict.Path) : conflict.Path).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        internal sealed class WorkshopOption
        {
            internal WorkshopCatalogItem Item { get; }
            private WorkshopLocalStatus? Status { get; }
            internal WorkshopOption(WorkshopCatalogItem item, WorkshopLocalStatus? status) { Item = item; Status = status; }
            public string Name => Item.Title;
            public string Local => UiText.Get("Workshop.Options.Local", "State", UiText.Get("Workshop.Options.State." + (Status?.ToString() ?? "Unknown"))) +
                (Status == WorkshopLocalStatus.Verified ? " (" + Item.InstalledVersion + ")" : string.Empty);
            public string Available => UiText.Get("Workshop.Options.Available", "Version", Item.Package?.Version ?? UiText.Get("Workshop.Options.State.Unknown"));
            public string UnavailableReason => Item.CanInstall ? string.Empty : Item.Status;
        }
    }
}
