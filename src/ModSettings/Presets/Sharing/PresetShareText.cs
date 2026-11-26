using APIShared.Localization;

namespace APIShared.ModSettings
{
    internal static class PresetShareText
    {
        internal static string Failure(PresetShareFailure failure)
        {
            switch (failure)
            {
                case PresetShareFailure.NoCompatiblePeers: return UiText.Get("PresetShare.NoCompatiblePeers");
                case PresetShareFailure.Invalid: return UiText.Get("PresetShare.Invalid");
                case PresetShareFailure.TooLarge: return UiText.Get("PresetShare.TooLarge");
                case PresetShareFailure.SendFailed: return UiText.Get("PresetShare.SendFailed");
                case PresetShareFailure.AlreadyPending: return UiText.Get("PresetShare.Downloading");
                case PresetShareFailure.TimedOut: return UiText.Get("PresetShare.TimedOut");
                case PresetShareFailure.Busy: return UiText.Get("PresetShare.Busy");
                case PresetShareFailure.DeliveryTimedOut: return UiText.Get("PresetShare.DeliveryTimedOut");
                case PresetShareFailure.PartialDelivery: return UiText.Get("PresetShare.PartialDelivery");
                default: return UiText.Get("PresetShare.Unavailable");
            }
        }
    }
}
