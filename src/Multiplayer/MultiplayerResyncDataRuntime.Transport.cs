using APIShared.Networking;

namespace APIShared.Multiplayer
{
    internal static partial class MultiplayerResyncDataRuntime
    {
        private static void Receive(NetworkMessage<MultiplayerResyncDataPacket> message) =>
            session?.Receive(message.SenderSteamId, message.Packet);
        internal static int GetHostWindowEnd(bool hostPrepared, int length, int acknowledged) =>
            MultiplayerResyncDataSession.GetHostWindowEnd(hostPrepared, length, acknowledged);
        internal static bool IsExpectedProgress(int length, int sent, int acknowledged, int offset) =>
            MultiplayerResyncDataSession.IsExpectedProgress(length, sent, acknowledged, offset);
    }
}