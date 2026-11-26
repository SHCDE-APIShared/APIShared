using System;
using System.Runtime.InteropServices;

namespace APIShared.Presentation
{
    // Native Windows message box for prompts that must appear before the game UI exists (Workshop
    // startup dialog, package bootstraps). System types only: the Workshop bootstrap template compiles
    // this file into each generated bootstrap (tools/New-WorkshopBootstrap.ps1).
    //
    // Called on the game's main thread, which hides the system cursor (the ShowCursor counter is per
    // thread) and confines it to the game window. An unowned box also opened behind the game, so
    // neither it nor other programs were reachable with the mouse. The box is owned by the game
    // window (stays above it, application-modal only: other programs stay usable), brought to the
    // front, and cursor visibility and confinement are released while it is open and then restored.
    internal static class NativeMessageBox
    {
        private const uint SetForeground = 0x10000; // MB_SETFOREGROUND

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW(IntPtr window, string text, string caption, uint flags);
        [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
        [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
        [DllImport("user32.dll")] private static extern bool ClipCursor(ref Rect rect);
        [DllImport("user32.dll")] private static extern bool GetClipCursor(out Rect rect);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindowsProc callback, IntPtr state);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

        // Returns the MessageBoxW result (IDYES = 6, IDNO = 7, IDOK = 1); 0 means the box could not
        // be shown (Marshal.GetLastWin32Error has the reason).
        internal static int Show(string text, string caption, uint flags)
        {
            // Plain C# 5 syntax: bootstraps compile this file with the .NET Framework CodeDom compiler.
            Rect clip;
            bool clipped = GetClipCursor(out clip);
            int shown = 0;
            try {
                ClipCursor(IntPtr.Zero);
                while (ShowCursor(true) < 0 && shown < 64) shown++;
                shown++;
                return MessageBoxW(GameWindow(), text, caption, flags | SetForeground);
            } finally {
                for (int i = 0; i < shown; i++) ShowCursor(false);
                // GetClipCursor also reports a rectangle when nothing was clipped; restoring that
                // could confine a multi-monitor player to one screen. Only a real clip returns.
                if (clipped && !(clip.Left == GetSystemMetrics(76) && clip.Top == GetSystemMetrics(77) && // SM_X/YVIRTUALSCREEN
                    clip.Right - clip.Left == GetSystemMetrics(78) && clip.Bottom - clip.Top == GetSystemMetrics(79))) // SM_CX/CYVIRTUALSCREEN
                    ClipCursor(ref clip);
            }
        }

        // Visible top-level window of the calling (main) thread, i.e. the game window; zero before
        // it exists, which keeps an unowned box.
        private static IntPtr GameWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumThreadWindows(GetCurrentThreadId(), (window, _) => {
                if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true; // GW_OWNER
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }
    }
}
