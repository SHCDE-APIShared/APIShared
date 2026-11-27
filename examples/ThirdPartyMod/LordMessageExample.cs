using APIShared.Presentation;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace ThirdPartyMod
{
    internal static class LordMessageExample
    {
        private static LordMessageRegistration registration;
        private static LordMessageAudio donorAudio;
        private static LordMessageVideo donorVideo;
        private static LordMessageText donorText;

        // Call on Unity after APIShared readiness. Keep the process-owned registration rooted.
        internal static bool Register(string ownerGuid, out string reason)
        {
            if (!VanillaLordMessages.TryGet(AILords.SK_WOLF, AILordMessageType.Taunt1, out var donor, out reason)) return false;
            donorAudio = LordMessageAudio.FromVanilla(donor);
            donorVideo = LordMessageVideo.FromVanilla(donor);
            donorText = LordMessageText.FromVanilla(donor);
            return LordMessageEvents.TryRegister(ownerGuid, "wolf-request-demo", BeforeMessage, null, out registration, out reason);
        }

        private static void BeforeMessage(LordMessagePreEventArgs args)
        {
            if (args.Context.VanillaLord != AILords.SK_WOLF || args.Context.MessageType != AILordMessageType.RequestGoods) return;
            args.TryReplaceAudio(donorAudio);
            args.TryReplaceVideo(donorVideo);
            args.TryReplaceText(donorText);
            // Omit any of the three calls to preserve that part.
            // Own indexed resources: prepare FromModAsset targets on Unity BEFORE registration,
            // retain them only when IsReady, and select those prepared targets here.
            // FromModAsset may extract package files and is prohibited inside Pre/Post.
            // args.TryReplaceText(LordMessageText.FromText("Already localized message text."));
        }

        internal static void SetEnabled(bool enabled) => registration?.SetEnabled(enabled);
    }
}
