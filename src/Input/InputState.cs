using System;
using CrusaderDE;
using Noesis;
using UnityEngine;

namespace APIShared.Input
{
    /// <summary>Modifier groups for a shortcut. Either side of a physical modifier satisfies its group.</summary>
    [Flags]
    public enum KeybindModifiers
    {
        /// <summary>No modifier.</summary>
        None = 0,
        /// <summary>Either Alt key.</summary>
        Alt = 1,
        /// <summary>Either Control key.</summary>
        Control = 2,
        /// <summary>Either Shift key.</summary>
        Shift = 4
    }

    /// <summary>Local input queries for use on the initialized Unity thread.</summary>
    public static class InputState
    {
        /// <summary>Reads the current physical state, including the frame in which the key was pressed.</summary>
        public static bool IsHeld(KeyCode key) => key != KeyCode.None && UnityEngine.Input.GetKey(key);
        /// <summary>Reads the physical press edge in the current frame.</summary>
        public static bool IsPressed(KeyCode key) => key != KeyCode.None && UnityEngine.Input.GetKeyDown(key);
        /// <summary>Reads the physical release edge in the current frame.</summary>
        public static bool IsReleased(KeyCode key) => key != KeyCode.None && UnityEngine.Input.GetKeyUp(key);
        /// <summary>Reads the physical mouse button's held state by Unity button index.</summary>
        public static bool IsMouseHeld(int button) => UnityEngine.Input.GetMouseButton(button);
        /// <summary>Reads the physical mouse button's press edge by Unity button index.</summary>
        public static bool IsMousePressed(int button) => UnityEngine.Input.GetMouseButtonDown(button);
        /// <summary>Reads the physical mouse button's release edge by Unity button index.</summary>
        public static bool IsMouseReleased(int button) => UnityEngine.Input.GetMouseButtonUp(button);

        /// <summary>Reads both sides of Shift, Control and Alt directly from Unity.</summary>
        public static KeybindModifiers PhysicalModifiers =>
            (PhysicalAltHeld ? KeybindModifiers.Alt : 0) |
            (PhysicalControlHeld ? KeybindModifiers.Control : 0) |
            (PhysicalShiftHeld ? KeybindModifiers.Shift : 0);

        /// <summary>Whether either physical Alt key is held.</summary>
        public static bool PhysicalAltHeld => IsHeld(KeyCode.LeftAlt) || IsHeld(KeyCode.RightAlt);
        /// <summary>Whether either physical Control key is held.</summary>
        public static bool PhysicalControlHeld => IsHeld(KeyCode.LeftControl) || IsHeld(KeyCode.RightControl);
        /// <summary>Whether either physical Shift key is held.</summary>
        public static bool PhysicalShiftHeld => IsHeld(KeyCode.LeftShift) || IsHeld(KeyCode.RightShift);

        /// <summary>Reads Vanilla's processed modifiers, respecting its current routing.</summary>
        public static KeybindModifiers GameModifiers
        {
            get
            {
                var keys = KeyManager.instance;
                if (keys == null) return KeybindModifiers.None;
                return (keys.isAltDown() ? KeybindModifiers.Alt : 0) |
                    (keys.isCtrlDown() ? KeybindModifiers.Control : 0) |
                    (keys.isShiftDown() ? KeybindModifiers.Shift : 0);
            }
        }

        /// <summary>Reads Noesis modifiers from the event source, falling back to physical input without a keyboard source.</summary>
        public static KeybindModifiers ReadModifiers(RoutedEventArgs args) =>
            args?.Source is UIElement source && source.Keyboard != null
                ? FromNoesis(source.Keyboard.Modifiers) : PhysicalModifiers;

        /// <summary>Combines physical modifiers with the local UI element's modifiers while Noesis owns the keyboard.</summary>
        public static KeybindModifiers ReadGestureModifiers(UIElement element) =>
            PhysicalModifiers | (FatControler.instance?.NoesisHasKeyboard == true && element?.Keyboard != null
                ? FromNoesis(element.Keyboard.Modifiers) : KeybindModifiers.None);

        private static KeybindModifiers FromNoesis(ModifierKeys held) =>
            ((held & ModifierKeys.Alt) != 0 ? KeybindModifiers.Alt : 0) |
            ((held & ModifierKeys.Control) != 0 ? KeybindModifiers.Control : 0) |
            ((held & ModifierKeys.Shift) != 0 ? KeybindModifiers.Shift : 0);

        /// <summary>Queries the KeyManager's held state, with its normal modifier handling by default.</summary>
        public static bool IsGameKeyHeld(KeyCode key, bool ignoreModifiers = false) =>
            KeyManager.instance?.IsKeyHeldDown(key, ignoreModifiers) == true;
        /// <summary>Queries the KeyManager's press edge, with its normal modifier handling by default.</summary>
        public static bool IsGameKeyPressed(KeyCode key, bool ignoreModifiers = false) =>
            KeyManager.instance?.IsKeyPressed(key, ignoreModifiers) == true;
        /// <summary>Queries the KeyManager's release edge.</summary>
        public static bool IsGameKeyReleased(KeyCode key) => KeyManager.instance?.IsKeyReleased(key) == true;

        /// <summary>Queries a configured Vanilla action's press edge using the game's current mappings.</summary>
        public static bool IsGameActionPressed(Enums.KeyFunctions action) => KeyManager.instance?.IsActionPressed(action) == true;
        /// <summary>Queries a configured Vanilla action's held state using the game's current mappings.</summary>
        public static bool IsGameActionHeld(Enums.KeyFunctions action, bool ignoreModifiers = false) =>
            KeyManager.instance?.IsActionHeldDown(action, ignoreModifiers) == true;
        /// <summary>Queries a configured Vanilla action's release edge using the game's current mappings.</summary>
        public static bool IsGameActionReleased(Enums.KeyFunctions action) => KeyManager.instance?.IsActionRelease(action) == true;
    }
}
