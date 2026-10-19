using System;
using System.ComponentModel;
using Noesis;
using SHCDESE.API;

namespace APIShared
{
    // Preference is process-owned; effective state never writes back to configuration.
    internal sealed class SavegameLoadOptions : INotifyPropertyChanged
    {
        private readonly Func<bool> read;
        private readonly Action<bool> write;
        private bool active, value, allowed;
        public event PropertyChangedEventHandler PropertyChanged;
        internal SavegameLoadOptions(Func<bool> read, Action<bool> write) { this.read = read; this.write = write; }
        public Visibility LoadOptionVisibility => active ? Visibility.Visible : Visibility.Collapsed;
        public bool CanUseCurrentSavegameSettings => allowed;
        public bool UseCurrentSavegameSettings
        {
            get => value;
            set
            {
                if (!active || !allowed || this.value == value) return;
                this.value = value;
                write(value);
                Changed(nameof(UseCurrentSavegameSettings));
            }
        }
        public string UseCurrentSavegameSettingsText
        {
            get
            {
                string language = string.Empty;
                try { language = GameAssetManagerAPI.Instance.CurrentLanguage ?? string.Empty; } catch { }
                return language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "Aktuelle Modsettings verwenden" : "Use current mod settings";
            }
        }
        public string UseCurrentSavegameSettingsHelp => "only by APIShared supported modsettings";
        internal void Open() { active = true; Select(SavegameLoadChoiceState.Invalid); Changed(nameof(LoadOptionVisibility)); }
        internal void Select(SavegameLoadChoiceState state)
        {
            allowed = active && state == SavegameLoadChoiceState.Selectable;
            value = state == SavegameLoadChoiceState.Legacy || state == SavegameLoadChoiceState.Invalid || allowed && read();
            Changed(nameof(CanUseCurrentSavegameSettings)); Changed(nameof(UseCurrentSavegameSettings));
        }
        internal void Close() { active = false; allowed = false; Changed(nameof(LoadOptionVisibility)); Changed(nameof(CanUseCurrentSavegameSettings)); }
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
