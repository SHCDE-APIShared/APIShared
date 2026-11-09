// APIShared-owned implementation; independent of workspace Shared helpers.
// Initial provenance: a7888900e, Shared/AtomicFileReplacement.cs. No automatic synchronization.
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace APIShared.Internal
{
    internal static class AtomicFileReplacement
    {
        private const uint MoveFileReplaceExisting = 0x1;
        private const uint MoveFileWriteThrough = 0x8;

        internal static void Replace(string sourcePath, string destinationPath)
        {
            if (TryMove(sourcePath, destinationPath, true))
                return;

            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Atomic file replacement failed with Win32 error {error}: " +
                $"source=[{sourcePath}], destination=[{destinationPath}].",
                new Win32Exception(error));
        }

        // The one MoveFileExW declaration. Returns false with the Win32 error left for
        // Marshal.GetLastWin32Error; the move is write-through.
        internal static bool TryMove(string sourcePath, string destinationPath, bool replaceExisting) =>
            MoveFileExW(sourcePath, destinationPath, MoveFileWriteThrough | (replaceExisting ? MoveFileReplaceExisting : 0));

        [DllImport(
            "kernel32.dll",
            EntryPoint = "MoveFileExW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(
            string existingFileName,
            string newFileName,
            uint flags);
    }
}
