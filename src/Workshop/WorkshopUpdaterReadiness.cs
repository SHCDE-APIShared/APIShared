using System.IO;

namespace APIShared.Workshop
{
    // The external updater can still own its write handle when readiness is
    // polled. Readers must permit that writer, and a transient lock must remain
    // an unconfirmed handoff rather than escaping the bounded readiness wait.
    internal static class WorkshopUpdaterReadiness
    {
        internal static bool TryConfirm(string path, string expectedHash, out IOException readFailure)
        {
            readFailure = null;
            try {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd() == expectedHash;
            } catch (IOException ex) {
                readFailure = ex;
                return false;
            }
        }
    }
}
