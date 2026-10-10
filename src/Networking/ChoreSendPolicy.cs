using System;

namespace APIShared.Networking
{
    // Pure submission policy used by the real transport and game-free boundary tests.
    // No simulation mutation, retry, automatic Steam fallback or UI work belongs here.
    internal static class ChoreSendPolicy
    {
        internal static NetworkSendResult Send<T>(T packet, short packetId, bool registered,
            Func<T, byte[]> serialize, Func<ulong> manager, Action<T, short> send, int limit) where T : class
        {
            if (limit < sizeof(short) || limit > 1200)
                throw new ArgumentOutOfRangeException(nameof(limit));
            if (!registered)
                return Result(NetworkSendStatus.Unavailable, "Packet receiver is not registered.");
            if (packet == null || serialize == null || manager == null || send == null)
                return Result(NetworkSendStatus.Invalid, "Chore prerequisites are incomplete.");
            byte[] body;
            try
            {
                body = serialize(packet);
                if (body == null)
                    return Result(NetworkSendStatus.PreparationFailed, "Serializer returned null.");
                if (body.Length > limit - sizeof(short))
                    return Result(NetworkSendStatus.TooLarge, "Body plus packet ID exceeds " + limit + " bytes.", body);
                if (manager() == 0)
                    return Result(NetworkSendStatus.Unavailable, "Chore manager is unavailable.", body);
            }
            catch (Exception error)
            {
                return Result(NetworkSendStatus.PreparationFailed, error.Message);
            }
            try
            {
                send(packet, packetId);
                return Result(NetworkSendStatus.Submitted, string.Empty, body);
            }
            catch (Exception error)
            {
                return Result(NetworkSendStatus.TransportFailed, error.Message, body);
            }
        }

        internal static NetworkSendResult Result(NetworkSendStatus status, string reason, byte[] body = null) =>
            new NetworkSendResult(status, reason, body);
    }
}
