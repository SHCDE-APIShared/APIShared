using System;
using System.IO;
using APIShared.Internal;

namespace APIShared.ModSettings
{
    internal sealed class PresetSaveFileExistsException : IOException
    {
        internal PresetSaveFileExistsException(string path)
            : base("The personal preset already exists: " + path)
        {
        }
    }

    internal interface IPresetAtomicFileOperations
    {
        bool Exists(string path);
        void Replace(string sourcePath, string destinationPath);
        void Move(string sourcePath, string destinationPath);
        void Delay(int milliseconds);
    }

    internal readonly struct PresetAtomicPublishResult
    {
        public PresetAtomicPublishResult(bool succeeded, int attempts, IOException error)
        {
            Succeeded = succeeded;
            Attempts = attempts;
            Error = error;
        }

        public bool Succeeded { get; }
        public int Attempts { get; }
        public IOException Error { get; }
    }

    internal static class PresetAtomicFilePublisher
    {
        private static readonly int[] RetryDelaysMilliseconds = { 15, 35, 75 };

        public static PresetAtomicPublishResult Publish(
            string temporaryPath,
            string destinationPath,
            IPresetAtomicFileOperations operations)
        {
            if (string.IsNullOrEmpty(temporaryPath))
                throw new ArgumentException("A temporary preset path is required.", nameof(temporaryPath));
            if (string.IsNullOrEmpty(destinationPath))
                throw new ArgumentException("A destination preset path is required.", nameof(destinationPath));
            if (operations == null)
                throw new ArgumentNullException(nameof(operations));

            for (int attempt = 1; attempt <= RetryDelaysMilliseconds.Length + 1; attempt++)
            {
                try
                {
                    if (operations.Exists(destinationPath))
                        operations.Replace(temporaryPath, destinationPath);
                    else
                        operations.Move(temporaryPath, destinationPath);
                    return new PresetAtomicPublishResult(true, attempt, null);
                }
                catch (IOException exception)
                {
                    if (attempt > RetryDelaysMilliseconds.Length)
                        return new PresetAtomicPublishResult(false, attempt, exception);
                    operations.Delay(RetryDelaysMilliseconds[attempt - 1]);
                }
            }

            throw new InvalidOperationException("The bounded preset publish loop terminated unexpectedly.");
        }

        public static PresetAtomicPublishResult Publish(
            string temporaryPath,
            string destinationPath)
        {
            return Publish(temporaryPath, destinationPath, SystemPresetAtomicFileOperations.Instance);
        }

        private sealed class SystemPresetAtomicFileOperations : IPresetAtomicFileOperations
        {
            public static readonly SystemPresetAtomicFileOperations Instance =
                new SystemPresetAtomicFileOperations();

            public bool Exists(string path) => File.Exists(path);

            public void Replace(string sourcePath, string destinationPath) =>
                AtomicFileReplacement.Replace(sourcePath, destinationPath);

            public void Move(string sourcePath, string destinationPath) =>
                File.Move(sourcePath, destinationPath);

            public void Delay(int milliseconds) =>
                System.Threading.Thread.Sleep(milliseconds);
        }
    }

}
