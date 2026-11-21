using System;
using UnityEngine;

namespace APIShared.Input
{
    /// <summary>A main key or mouse button and its exact modifier combination.</summary>
    public readonly struct KeybindChord : IEquatable<KeybindChord>
    {
        /// <summary>Creates a chord. KeyCode.None represents an unassigned shortcut.</summary>
        public KeybindChord(KeyCode key, KeybindModifiers modifiers = KeybindModifiers.None)
        { Key = key; Modifiers = key == KeyCode.None ? KeybindModifiers.None : modifiers; }
        /// <summary>The main input, or None.</summary>
        public KeyCode Key { get; }
        /// <summary>Required modifier groups.</summary>
        public KeybindModifiers Modifiers { get; }
        /// <summary>Compares both the key and modifiers.</summary>
        public bool Equals(KeybindChord other) => Key == other.Key && Modifiers == other.Modifiers;
        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is KeybindChord chord && Equals(chord);
        /// <inheritdoc/>
        public override int GetHashCode() => unchecked((int)Key * 397 ^ (int)Modifiers);
    }

    /// <summary>A shortcut backed by the registering mod's existing local settings. Callbacks run on
    /// Unity. The registration is process-lived and the caller's execution predicate still owns game-mode
    /// and gameplay permission checks.</summary>
    public sealed class ModKeybindRegistration
    {
        private readonly Func<string> displayName;
        private readonly Func<KeybindChord> read;
        private readonly Action<KeybindChord> write;
        private readonly Func<KeybindChord, bool> accepts;
        private readonly Func<bool> canEdit;
        private readonly Func<bool> canExecute;
        private readonly Action execute;

        /// <summary>Defines a local mod action. Use stable owner and action IDs. The writer should
        /// use the mod's normal setter and reflect accepted writes through the reader.</summary>
        public ModKeybindRegistration(string ownerGuid, string id, string modName, Func<string> displayName,
            KeybindChord defaultBinding, Func<KeybindChord> read, Action<KeybindChord> write,
            Func<KeybindChord, bool> accepts, Func<bool> canEdit, Func<bool> canExecute, Action execute)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid)) throw new ArgumentException("Owner GUID is required.", nameof(ownerGuid));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Action ID is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(modName)) throw new ArgumentException("Mod name is required.", nameof(modName));
            OwnerGuid = ownerGuid; Id = id; ModName = modName;
            this.displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.read = read ?? throw new ArgumentNullException(nameof(read));
            this.write = write ?? throw new ArgumentNullException(nameof(write));
            this.accepts = accepts ?? throw new ArgumentNullException(nameof(accepts));
            this.canEdit = canEdit ?? throw new ArgumentNullException(nameof(canEdit));
            this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            Default = defaultBinding;
        }

        /// <summary>The mod's stable plugin GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>The stable action ID within this mod.</summary>
        public string Id { get; }
        /// <summary>The mod name shown in the game settings.</summary>
        public string ModName { get; }
        /// <summary>The action's localized label.</summary>
        public string DisplayName => displayName();
        /// <summary>The default shortcut.</summary>
        public KeybindChord Default { get; }
        /// <summary>The current shortcut, read directly from the registering mod.</summary>
        public KeybindChord Current => read();
        /// <summary>Whether the shortcut can currently be changed.</summary>
        public bool CanEdit => canEdit();

        /// <summary>Tries to change the shortcut with the normal validation and edit permissions.</summary>
        public bool TrySet(KeybindChord chord)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread || !CanEdit || !Accepts(chord)) return false;
            if (!Current.Equals(chord)) write(chord);
            if (!Current.Equals(chord)) return false;
            ModKeybinds.NotifyBindingChanged();
            return true;
        }

        internal bool Accepts(KeybindChord chord) => ModKeybinds.IsValidChord(chord) && accepts(chord);
        internal void Dispatch(KeybindChord chord)
        {
            if (Current.Equals(chord) && canExecute()) execute();
        }
    }
}
