using System;
using System.Threading;

namespace APIShared.Savegames
{
    // The existing AI observer remains the sole owner of native 87D0. Participants
    // run after raw sections have been restored and before ANY first simulation fold.
    // A veto uses the audited native failure return; no exception crosses native code.
    internal static class NativeWorldRestoreParticipants
    {
        private static readonly object Sync = new object();
        private static Func<bool>[] participants = Array.Empty<Func<bool>>();
        internal static void Register(Func<bool> participant)
        {
            if (participant == null) throw new ArgumentNullException(nameof(participant));
            lock (Sync)
            {
                foreach (var old in participants) if (old == participant) return;
                var next = new Func<bool>[participants.Length + 1];
                Array.Copy(participants, next, participants.Length); next[next.Length - 1] = participant;
                Volatile.Write(ref participants, next);
            }
        }
        internal static bool Restore()
        {
            foreach (var participant in Volatile.Read(ref participants))
                try { if (!participant()) return false; }
                catch (Exception error)
                {
                    Economy.WorkerProductionWorldSafety.Fault("Native world restore participant failed: " + error.Message);
                    return false;
                }
            return true;
        }
    }
}
