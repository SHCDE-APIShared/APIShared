using System;
using System.Collections.Generic;
using APIShared.Internal;
using APIShared.Presentation;
using CrusaderDE;
using Noesis;
using R3;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Input;
using UnityEngine;

namespace APIShared.Input
{
    /// <summary>Local, process-lived shortcuts and a shared assignment session. Register and interact on
    /// the initialized Unity thread. Callbacks do not confer gameplay or multiplayer authority.</summary>
    public static class ModKeybinds
    {
        private const KeybindModifiers KnownModifiers = KeybindModifiers.Alt | KeybindModifiers.Control | KeybindModifiers.Shift;
        private static IReadOnlyList<ModKeybindRegistration> registrations = Array.Empty<ModKeybindRegistration>();
        private static IUiFrameWorkRegistration captureWork;
        private static IDisposable keyDown;
        private static Capture capture;
        private static Capture releasing;
        private static int lastCaptureFrame = -1;

        /// <summary>The registered shortcuts in registration order. Each new registration publishes a new snapshot.</summary>
        public static IReadOnlyList<ModKeybindRegistration> Registrations => registrations;
        /// <summary>Raised on Unity when the registered shortcuts, bindings or capture state change.</summary>
        public static event Action Changed;

        /// <summary>Registers a shortcut under a unique owner and action ID. Unassigned shortcuts remain editable.</summary>
        public static bool TryRegister(ModKeybindRegistration registration, out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Keybind registration requires the initialized Unity thread."; return false; }
            if (registration == null || !registration.Accepts(registration.Default))
            { reason = "A shortcut with a valid default is required."; return false; }
            foreach (var existing in registrations)
            {
                if (existing.OwnerGuid == registration.OwnerGuid && existing.Id == registration.Id)
                { reason = "A shortcut with this owner and ID is already registered."; return false; }
            }
            if (captureWork == null && !UiWorkRuntime.TryRegister(APISharedPlugin.PluginGuid, "mod-keybind-capture",
                UpdateCapture, 0, out captureWork, out reason))
                return false;
            if (keyDown == null) keyDown = InputR3EventHooks.OnKeyDown.Observable.Subscribe(OnKeyDown);
            var next = new List<ModKeybindRegistration>(registrations) { registration };
            registrations = next.AsReadOnly();
            reason = string.Empty;
            NotifyBindingChanged();
            return true;
        }

        /// <summary>Starts exclusive assignment while the provided surface remains active. Escape cancels.
        /// The optional completion callback runs on Unity after success or cancellation.</summary>
        public static bool BeginCapture(ModKeybindRegistration registration, Func<bool> isActive, Action finished = null)
        {
            if (!UnityMainThreadDispatch.IsMainThread || registration == null || !registration.CanEdit ||
                isActive == null || captureWork == null || capture != null || releasing != null || !Application.isFocused ||
                !IsRegistered(registration) || !isActive())
                return false;

            var manager = KeyManager.instance;
            if (manager == null || manager.HotKeySelectorMode)
                return false;

            int heldMouse = 0;
            for (int button = 0; button <= (int)KeyCode.Mouse6 - (int)KeyCode.Mouse0; button++)
                if (InputState.IsHeld(KeyCode.Mouse0 + button))
                    heldMouse |= 1 << button;

            // This existing game mode bypasses Vanilla's shortcut execution but still publishes SE key edges.
            manager.HotKeySelectorMode = true;
            capture = new Capture(registration, isActive, finished, manager, Time.frameCount, heldMouse);
            captureWork.SetContinuous(true);
            NotifyBindingChanged();
            return true;
        }

        /// <summary>Cancels this registration's current assignment session.</summary>
        public static void CancelCapture(ModKeybindRegistration registration)
        {
            if (UnityMainThreadDispatch.IsMainThread && IsCapturing(registration))
                FinishCapture();
        }

        /// <summary>Whether this shortcut owns assignment input.</summary>
        public static bool IsCapturing(ModKeybindRegistration registration) =>
            capture != null && ReferenceEquals(capture.Registration, registration);

        /// <summary>Routes a Noesis preview key or mouse event from the active assignment surface.</summary>
        public static bool TryCapture(RoutedEventArgs args)
        {
            if (!UnityMainThreadDispatch.IsMainThread || !CheckCapture() ||
                !NoesisKeybindInput.TryRead(args, out KeyCode key))
                return false;
            args.Handled = true;
            AcceptInput(key, args is MouseButtonEventArgs
                ? InputState.ReadGestureModifiers(args.Source as UIElement)
                : InputState.ReadModifiers(args));
            return true;
        }

        /// <summary>Signals changes made directly through a mod's existing settings or preset system.</summary>
        public static void NotifyBindingChanged()
        {
            if (!UnityMainThreadDispatch.IsMainThread || Changed == null) return;
            foreach (Action listener in Changed.GetInvocationList())
            {
                try { listener(); }
                catch (Exception error) { UiWorkRuntime.Report("mod-keybinds/changed", error); }
            }
        }

        /// <summary>Displays an assigned shortcut with the game's key names.</summary>
        public static string Format(KeybindChord chord)
        {
            if (chord.Key == KeyCode.None) return "—";
            if (!IsValidChord(chord)) return chord.Key.ToString();
            string key;
            try { key = HUD_Options.GetKeyCodeString(chord.Key); }
            catch (Exception) { key = chord.Key.ToString(); }
            return ((chord.Modifiers & KeybindModifiers.Control) != 0 ? "Ctrl+" : "") +
                ((chord.Modifiers & KeybindModifiers.Alt) != 0 ? "Alt+" : "") +
                ((chord.Modifiers & KeybindModifiers.Shift) != 0 ? "Shift+" : "") +
                (string.IsNullOrEmpty(key) ? chord.Key.ToString() : key);
        }

        /// <summary>Reports an exact duplicate with another mod shortcut or a configured Vanilla action.</summary>
        public static bool HasConflict(ModKeybindRegistration registration)
        {
            if (!UnityMainThreadDispatch.IsMainThread || registration == null) return false;
            KeybindChord chord = registration.Current;
            if (chord.Key == KeyCode.None) return false;
            foreach (var other in registrations)
            {
                if (ReferenceEquals(other, registration)) continue;
                try { if (other.Current.Equals(chord)) return true; }
                catch (Exception error) { UiWorkRuntime.Report(other.OwnerGuid + "/" + other.Id, error); }
            }
            var manager = KeyManager.instance;
            if (manager == null) return false;
            for (int action = 0; action < (int)Enums.KeyFunctions.NumActions; action++)
                for (int slot = 0; slot < 2; slot++)
                    if (FromGameBinding((int)manager.GetKeyCode((Enums.KeyFunctions)action, slot)).Equals(chord)) return true;
            return false;
        }

        // Vanilla encodes Shift, Control, Alt above its 16-bit key code (KeyManager.Update).
        internal static KeybindChord FromGameBinding(int value) => new KeybindChord((KeyCode)(value & 0xffff),
            ((value & 0x10000) != 0 ? KeybindModifiers.Shift : 0) |
            ((value & 0x20000) != 0 ? KeybindModifiers.Control : 0) |
            ((value & 0x40000) != 0 ? KeybindModifiers.Alt : 0));

        internal static bool IsValidChord(KeybindChord chord) =>
            (chord.Modifiers & ~KnownModifiers) == 0 && Enum.IsDefined(typeof(KeyCode), chord.Key);

        private static bool IsRegistered(ModKeybindRegistration registration)
        {
            foreach (var existing in registrations) if (ReferenceEquals(existing, registration)) return true;
            return false;
        }

        private static void OnKeyDown(UnityInputEventArgs args)
        {
            if (args == null || args.Phase != EventHookPhase.Pre || !UnityMainThreadDispatch.IsMainThread) return;
            if (capture != null)
            {
                args.Result = false;
                capture.ConsumedKeys.Add(args.Key);
                if (CheckCapture()) AcceptInput(args.Key, InputState.PhysicalModifiers);
                return;
            }
            if (releasing != null)
            {
                args.Result = false;
                releasing.ConsumedKeys.Add(args.Key);
                return;
            }
            if (!args.Result || args.Key == KeyCode.None || Time.frameCount == lastCaptureFrame ||
                !Application.isFocused || KeyManager.instance?.HotKeySelectorMode == true) return;
            var chord = new KeybindChord(args.Key, InputState.PhysicalModifiers);
            foreach (var registration in registrations)
            {
                try { registration.Dispatch(chord); }
                catch (Exception error) { UiWorkRuntime.Report(registration.OwnerGuid + "/" + registration.Id, error); }
            }
        }

        private static void UpdateCapture()
        {
            if (releasing != null)
            {
                ReleaseSelectorWhenKeysAreUp();
                return;
            }
            if (!CheckCapture() || Time.frameCount <= capture.StartFrame) return;
            if (InputState.IsPressed(KeyCode.Escape))
            {
                capture.ConsumedKeys.Add(KeyCode.Escape);
                FinishCapture();
                return;
            }
            for (int button = 0; button <= (int)KeyCode.Mouse6 - (int)KeyCode.Mouse0; button++)
            {
                KeyCode key = KeyCode.Mouse0 + button;
                int mask = 1 << button;
                if (!InputState.IsHeld(key)) capture.HeldMouse &= ~mask;
                else if ((capture.HeldMouse & mask) == 0)
                {
                    capture.HeldMouse |= mask;
                    AcceptInput(key, InputState.PhysicalModifiers, allowHeldMouse: true);
                    if (capture == null) return;
                }
            }
        }

        private static bool CheckCapture()
        {
            var active = capture;
            if (active == null) return false;
            try
            {
                if (Application.isFocused && ReferenceEquals(KeyManager.instance, active.Manager) &&
                    active.Manager.HotKeySelectorMode && active.Registration.CanEdit && active.IsActive())
                    return true;
            }
            catch (Exception error) { UiWorkRuntime.Report(active.Registration.Id + "/capture", error); }
            FinishCapture();
            return false;
        }

        private static void AcceptInput(KeyCode key, KeybindModifiers modifiers, bool allowHeldMouse = false)
        {
            var active = capture;
            if (active == null || Time.frameCount <= active.StartFrame || key == KeyCode.None) return;
            active.ConsumedKeys.Add(key);
            if (key == KeyCode.Escape) { FinishCapture(); return; }
            if (key == KeyCode.LeftShift || key == KeyCode.RightShift || key == KeyCode.LeftControl ||
                key == KeyCode.RightControl || key == KeyCode.LeftAlt || key == KeyCode.RightAlt)
                return;
            if (!allowHeldMouse && key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6 &&
                (active.HeldMouse & (1 << (key - KeyCode.Mouse0))) != 0) return;
            try
            {
                if (active.Registration.TrySet(new KeybindChord(key, modifiers)) && ReferenceEquals(capture, active))
                    FinishCapture();
            }
            catch (Exception error)
            {
                if (ReferenceEquals(capture, active)) FinishCapture();
                UiWorkRuntime.Report(active.Registration.Id + "/capture", error);
            }
        }

        private static void FinishCapture()
        {
            var active = capture;
            if (active == null) return;
            capture = null;
            lastCaptureFrame = Time.frameCount;
            releasing = active;
            ReleaseSelectorWhenKeysAreUp();
            try { active.Finished?.Invoke(); }
            catch (Exception error) { UiWorkRuntime.Report(active.Registration.Id + "/capture-finished", error); }
            NotifyBindingChanged();
        }

        private static void ReleaseSelectorWhenKeysAreUp()
        {
            var active = releasing;
            if (Application.isFocused && ReferenceEquals(KeyManager.instance, active.Manager) && active.Manager.HotKeySelectorMode)
            {
                // SE clears a consumed key's processed state. Leaving selector mode while it is held
                // would make Vanilla see a new press before the next SE callback.
                foreach (KeyCode key in active.ConsumedKeys)
                    if (InputState.IsHeld(key)) return;
            }
            releasing = null;
            lastCaptureFrame = Time.frameCount;
            if (active.Manager != null && active.Manager.HotKeySelectorMode)
                active.Manager.HotKeySelectorMode = false;
            captureWork.SetContinuous(false);
        }

        private sealed class Capture
        {
            internal readonly ModKeybindRegistration Registration;
            internal readonly Func<bool> IsActive;
            internal readonly Action Finished;
            internal readonly KeyManager Manager;
            internal readonly int StartFrame;
            internal readonly HashSet<KeyCode> ConsumedKeys = new HashSet<KeyCode>();
            internal int HeldMouse;

            internal Capture(ModKeybindRegistration registration, Func<bool> isActive, Action finished,
                KeyManager manager, int startFrame, int heldMouse)
            {
                Registration = registration; IsActive = isActive; Finished = finished;
                Manager = manager; StartFrame = startFrame; HeldMouse = heldMouse;
            }
        }
    }
}
