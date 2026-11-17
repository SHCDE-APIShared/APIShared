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
        private readonly RelayCommand withdraw;
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
            prepare = new RelayCommand(Prepare, () => !busy && preview == null && selected?.Item.CanInstall == true && !SelectedRequested);
            confirm = new RelayCommand(Confirm, () => !busy && preview != null);
            cancel = new RelayCommand(() => { preview = null; Changed(); }, () => !busy && preview != null);
            withdraw = new RelayCommand(Withdraw, () => !busy && SelectedRequested);
        }

        private bool SelectedRequested => selected != null && WorkshopRuntime.InstallRequested(selected.Item.WorkshopId);

        public string WorkshopTitle => UiText.Get("Workshop.Options.Title");
        public string PackageLabel => UiText.Get("Workshop.Options.Package");
        public string RefreshLabel => UiText.Get("Workshop.Options.Refresh");
        public string OffersLabel => UiText.Get("Workshop.Options.Offers");
        public string OffersHelp => UiText.Get("Workshop.Options.OffersHelp");
        public string PrepareLabel => UiText.Get("Workshop.Options.Prepare");
        public string PrepareHelp => UiText.Get("Workshop.Options.RestartRequired");
        public string ConfirmLabel => UiText.Get("Workshop.Options.Confirm");
        public string CancelLabel => UiText.Get("Workshop.Options.Cancel");
        public string WithdrawLabel => UiText.Get("Workshop.Options.Withdraw");
        public string WithdrawHelp => UiText.Get("Workshop.Options.WithdrawHelp");
        public ICommand RefreshCommand => refresh;
        public ICommand PrepareCommand => prepare;
        public ICommand ConfirmCommand => confirm;
        public ICommand CancelCommand => cancel;
        public ICommand WithdrawCommand => withdraw;
        public IReadOnlyList<WorkshopOption> Packages { get; private set; } = Array.Empty<WorkshopOption>();
        public string Message { get; private set; } = string.Empty;
        public string PendingMessage => SelectedRequested ? UiText.Get("Workshop.Options.RestartPending") : string.Empty;
        public Visibility WithdrawVisibility => SelectedRequested ? Visibility.Visible : Visibility.Collapsed;
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WithdrawVisibility)));
            refresh.RaiseCanExecuteChanged();
            prepare.RaiseCanExecuteChanged();
            confirm.RaiseCanExecuteChanged();
            cancel.RaiseCanExecuteChanged();
            withdraw.RaiseCanExecuteChanged();
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

        // Only records the request. The next start plans again and asks in its startup dialog;
        // no updater runs at game exit (an exit can be followed directly by a PC shutdown).
        private void Confirm()
        {
            if (!confirm.CanExecute(null)) return;
            var plan = preview;
            preview = null;
            try {
                WorkshopRuntime.SetInstallRequested(plan.WorkshopId, true);
                WorkshopRuntime.Log("Workshop installation requested for the next start; item=" + plan.WorkshopId +
                    "; previewAction=" + plan.Action + "; previewVersion=" + plan.Version + ".");
                Message = string.Empty;
            } catch (Exception ex) { ReportError("installation request", ex); }
            Changed();
        }

        private void Withdraw()
        {
            if (!withdraw.CanExecute(null)) return;
            try {
                WorkshopRuntime.SetInstallRequested(selected.Item.WorkshopId, false);
                WorkshopRuntime.Log("Workshop installation request withdrawn; item=" + selected.Item.WorkshopId + ".");
                Message = string.Empty;
            } catch (Exception ex) { ReportError("withdraw installation request", ex); }
            Changed();
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
