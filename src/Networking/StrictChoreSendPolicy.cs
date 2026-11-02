using System;

namespace APIShared.Networking
{
    // The authenticated path serializes exactly once and queues the resulting blob
    // directly. In particular, a second serialization can never trigger Steam fallback.
    internal static class StrictChoreSendPolicy
    {
        internal static NetworkSendResult Send<T>(T packet, short packetId, bool registered,
            Func<T, byte[]> serialize, Func<byte[], bool> queue, int limit) where T : class
        {
            if (limit < 2 || limit > ChoreTransport.MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(limit));
            if (!registered) return ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Authenticated Chore transport unavailable.");
            if (packet == null || serialize == null || queue == null)
                return ChoreSendPolicy.Result(NetworkSendStatus.Invalid, "Chore prerequisites are incomplete.");
            byte[] body;
            try
            {
                body = serialize(packet);
                if (body == null) return ChoreSendPolicy.Result(NetworkSendStatus.PreparationFailed, "Serializer returned null.");
                if (body.Length > limit - 2)
                    return ChoreSendPolicy.Result(NetworkSendStatus.TooLarge, "Body plus packet ID exceeds " + limit + " bytes.", body);
            }
            catch (Exception error) { return ChoreSendPolicy.Result(NetworkSendStatus.PreparationFailed, error.Message); }
            var blob = new byte[body.Length + 2];
            blob[0] = unchecked((byte)packetId); blob[1] = unchecked((byte)(packetId >> 8));
            Buffer.BlockCopy(body, 0, blob, 2, body.Length);
            try
            {
                return queue(blob)
                    ? ChoreSendPolicy.Result(NetworkSendStatus.Submitted, string.Empty, body)
                    : ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Native queue rejected submission; no fallback was sent.", body);
            }
            catch (Exception error) { return ChoreSendPolicy.Result(NetworkSendStatus.TransportFailed, error.Message, body); }
        }
    }
}
