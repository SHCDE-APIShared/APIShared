using System;

namespace APIShared.Networking
{
    internal static class PacketDiagnostics
    {
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
