using System;
using System.Runtime.InteropServices;
using System.Text;

namespace APIShared.Workshop.Proton
{
    internal interface IWinePathApi
    {
        bool IsWine { get; }
        bool TryToUnix(string windowsPath, out string hostPath);
        bool TryToWindows(string hostPath, out string windowsPath);
    }

    // Wine owns both translation results on the process heap. Keep this ABI layer
    // independent of BepInEx so the publisher and manual patcher use the same code.
    internal sealed class WinePathInterop : IWinePathApi
    {
        internal static readonly WinePathInterop Instance = new WinePathInterop();
        private const uint UnixCodePage = 65010; // Wine's CP_UNIXCP, not Windows ACP.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr UnixName([MarshalAs(UnmanagedType.LPWStr)] string path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DosName(IntPtr path);
        private readonly UnixName toUnix;
        private readonly DosName toDos;
        public bool IsWine { get; }

        private WinePathInterop()
        {
            // Tests also compile this source on native Linux. Never import kernel32 there.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            IsWine = GetProcAddress(GetModuleHandleW("ntdll.dll"), "wine_get_version") != IntPtr.Zero;
            if (!IsWine) return;
            IntPtr kernel = GetModuleHandleW("kernel32.dll");
            IntPtr unix = GetProcAddress(kernel, "wine_get_unix_file_name");
            IntPtr dos = GetProcAddress(kernel, "wine_get_dos_file_name");
            if (unix != IntPtr.Zero) toUnix = (UnixName)Marshal.GetDelegateForFunctionPointer(unix, typeof(UnixName));
            if (dos != IntPtr.Zero) toDos = (DosName)Marshal.GetDelegateForFunctionPointer(dos, typeof(DosName));
        }

        public bool TryToUnix(string windowsPath, out string hostPath)
        {
            hostPath = null;
            if (toUnix == null || string.IsNullOrEmpty(windowsPath)) return false;
            IntPtr result = toUnix(windowsPath);
            if (result == IntPtr.Zero) return false;
            try {
                int count = MultiByteToWideChar(UnixCodePage, 0, result, -1, null, 0);
                if (count <= 0) return false;
                var buffer = new StringBuilder(count);
                if (MultiByteToWideChar(UnixCodePage, 0, result, -1, buffer, count) == 0) return false;
                hostPath = buffer.ToString();
                return !string.IsNullOrEmpty(hostPath);
            } finally { HeapFree(GetProcessHeap(), 0, result); }
        }

        public bool TryToWindows(string hostPath, out string windowsPath)
        {
            windowsPath = null;
            if (toDos == null || string.IsNullOrEmpty(hostPath)) return false;
            int count = WideCharToMultiByte(UnixCodePage, 0, hostPath, -1, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0) return false;
            IntPtr input = Marshal.AllocHGlobal(count);
            try {
                if (WideCharToMultiByte(UnixCodePage, 0, hostPath, -1, input, count, IntPtr.Zero, IntPtr.Zero) == 0) return false;
                IntPtr result = toDos(input);
                if (result == IntPtr.Zero) return false;
                try { windowsPath = Marshal.PtrToStringUni(result); return !string.IsNullOrEmpty(windowsPath); }
                finally { HeapFree(GetProcessHeap(), 0, result); }
            } finally { Marshal.FreeHGlobal(input); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] private static extern IntPtr GetProcessHeap();
        [DllImport("kernel32.dll")] private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MultiByteToWideChar(uint codePage, uint flags, IntPtr bytes, int byteCount, StringBuilder chars, int charCount);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int WideCharToMultiByte(uint codePage, uint flags, string chars, int charCount, IntPtr bytes, int byteCount, IntPtr defaultChar, IntPtr usedDefaultChar);
    }
}
