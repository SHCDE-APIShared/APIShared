using Noesis;
using UnityEngine;

namespace APIShared.Input
{
    internal static class NoesisKeybindInput
    {
        internal static bool TryRead(RoutedEventArgs args, out KeyCode key)
        {
            if (args is KeyEventArgs keyArgs) return TryMapNoesisKey(keyArgs.Key, out key);
            if (args is MouseButtonEventArgs mouseArgs) return TryMapNoesisMouseButton(mouseArgs.ChangedButton, out key);
            key = KeyCode.None;
            return false;
        }

        private static bool TryMapNoesisMouseButton(
            Noesis.MouseButton button,
            out KeyCode key)
        {
            switch (button)
            {
                case Noesis.MouseButton.Left:
                    key = KeyCode.Mouse0;
                    return true;
                case Noesis.MouseButton.Right:
                    key = KeyCode.Mouse1;
                    return true;
                case Noesis.MouseButton.Middle:
                    key = KeyCode.Mouse2;
                    return true;
                case Noesis.MouseButton.XButton1:
                    key = KeyCode.Mouse3;
                    return true;
                case Noesis.MouseButton.XButton2:
                    key = KeyCode.Mouse4;
                    return true;
                default:
                    key = KeyCode.None;
                    return false;
            }
        }

        private static bool TryMapNoesisKey(
            Noesis.Key source,
            out KeyCode key)
        {
            if (source >= Noesis.Key.A && source <= Noesis.Key.Z)
            {
                key = (KeyCode)((int)KeyCode.A +
                    ((int)source - (int)Noesis.Key.A));
                return true;
            }
            if (source >= Noesis.Key.D0 && source <= Noesis.Key.D9)
            {
                key = (KeyCode)((int)KeyCode.Alpha0 +
                    ((int)source - (int)Noesis.Key.D0));
                return true;
            }
            if (source >= Noesis.Key.NumPad0 &&
                source <= Noesis.Key.NumPad9)
            {
                key = (KeyCode)((int)KeyCode.Keypad0 +
                    ((int)source - (int)Noesis.Key.NumPad0));
                return true;
            }
            if (source >= Noesis.Key.F1 && source <= Noesis.Key.F15)
            {
                key = (KeyCode)((int)KeyCode.F1 +
                    ((int)source - (int)Noesis.Key.F1));
                return true;
            }

            switch (source)
            {
                case Noesis.Key.Back: key = KeyCode.Backspace; return true;
                case Noesis.Key.Tab: key = KeyCode.Tab; return true;
                case Noesis.Key.Clear: key = KeyCode.Clear; return true;
                case Noesis.Key.Return: key = KeyCode.Return; return true;
                case Noesis.Key.Pause: key = KeyCode.Pause; return true;
                case Noesis.Key.Escape: key = KeyCode.Escape; return true;
                case Noesis.Key.Space: key = KeyCode.Space; return true;
                case Noesis.Key.PageUp: key = KeyCode.PageUp; return true;
                case Noesis.Key.PageDown: key = KeyCode.PageDown; return true;
                case Noesis.Key.End: key = KeyCode.End; return true;
                case Noesis.Key.Home: key = KeyCode.Home; return true;
                case Noesis.Key.Left: key = KeyCode.LeftArrow; return true;
                case Noesis.Key.Up: key = KeyCode.UpArrow; return true;
                case Noesis.Key.Right: key = KeyCode.RightArrow; return true;
                case Noesis.Key.Down: key = KeyCode.DownArrow; return true;
                case Noesis.Key.Print:
                    key = KeyCode.Print;
                    return true;
                case Noesis.Key.Insert: key = KeyCode.Insert; return true;
                case Noesis.Key.Delete: key = KeyCode.Delete; return true;
                case Noesis.Key.Help: key = KeyCode.Help; return true;
                case Noesis.Key.Multiply: key = KeyCode.KeypadMultiply; return true;
                case Noesis.Key.Add: key = KeyCode.KeypadPlus; return true;
                case Noesis.Key.Subtract: key = KeyCode.KeypadMinus; return true;
                case Noesis.Key.Decimal: key = KeyCode.KeypadPeriod; return true;
                case Noesis.Key.Divide: key = KeyCode.KeypadDivide; return true;
                case Noesis.Key.NumLock: key = KeyCode.Numlock; return true;
                case Noesis.Key.Scroll: key = KeyCode.ScrollLock; return true;
                case Noesis.Key.CapsLock: key = KeyCode.CapsLock; return true;
                case Noesis.Key.LeftShift: key = KeyCode.LeftShift; return true;
                case Noesis.Key.RightShift: key = KeyCode.RightShift; return true;
                case Noesis.Key.LeftCtrl: key = KeyCode.LeftControl; return true;
                case Noesis.Key.RightCtrl: key = KeyCode.RightControl; return true;
                case Noesis.Key.LeftAlt: key = KeyCode.LeftAlt; return true;
                case Noesis.Key.RightAlt: key = KeyCode.RightAlt; return true;
                case Noesis.Key.LWin: key = KeyCode.LeftWindows; return true;
                case Noesis.Key.RWin: key = KeyCode.RightWindows; return true;
                case Noesis.Key.Apps: key = KeyCode.Menu; return true;
                case Noesis.Key.OemSemicolon: key = KeyCode.Semicolon; return true;
                case Noesis.Key.OemPlus: key = KeyCode.Equals; return true;
                case Noesis.Key.OemComma: key = KeyCode.Comma; return true;
                case Noesis.Key.OemMinus: key = KeyCode.Minus; return true;
                case Noesis.Key.OemPeriod: key = KeyCode.Period; return true;
                case Noesis.Key.OemQuestion: key = KeyCode.Slash; return true;
                case Noesis.Key.OemTilde: key = KeyCode.BackQuote; return true;
                case Noesis.Key.OemOpenBrackets:
                    key = KeyCode.LeftBracket;
                    return true;
                case Noesis.Key.OemPipe: key = KeyCode.Backslash; return true;
                case Noesis.Key.OemCloseBrackets:
                    key = KeyCode.RightBracket;
                    return true;
                case Noesis.Key.OemQuotes: key = KeyCode.Quote; return true;
                case Noesis.Key.GamepadAccept:
                    key = KeyCode.JoystickButton0;
                    return true;
                case Noesis.Key.GamepadCancel:
                    key = KeyCode.JoystickButton1;
                    return true;
                case Noesis.Key.GamepadContext1:
                    key = KeyCode.JoystickButton2;
                    return true;
                case Noesis.Key.GamepadContext2:
                    key = KeyCode.JoystickButton3;
                    return true;
                case Noesis.Key.GamepadPageLeft:
                    key = KeyCode.JoystickButton4;
                    return true;
                case Noesis.Key.GamepadPageRight:
                    key = KeyCode.JoystickButton5;
                    return true;
                case Noesis.Key.GamepadView:
                    key = KeyCode.JoystickButton6;
                    return true;
                case Noesis.Key.GamepadMenu:
                    key = KeyCode.JoystickButton7;
                    return true;
                default:
                    key = KeyCode.None;
                    return false;
            }
        }

    }
}
