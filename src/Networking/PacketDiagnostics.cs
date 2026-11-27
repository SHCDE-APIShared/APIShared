using System;

namespace APIShared.Networking
{
    internal static class PacketDiagnostics
    {
        // API_SHARED_DIAGNOSTICS: explicit caller-supplied failure callback; no routine
        // logging. Failure delivery stays available independently of the logging config.
        internal static Action<string, Exception> Create(string owner, string id, Action<NetworkPacketDiagnostic> callback)
        {
            return (reason, error) =>
            {
                if (callback == null) return;
                var snapshot = new NetworkPacketDiagnostic(owner, id, reason, error);
                // No logging or presentation runs inside a raw network/native callback.
                Internal.UnityMainThreadDispatch.TryEnqueue(() =>
                {
                    try { callback(snapshot); }
                    catch { }
                });
            };
        }
    }
}
