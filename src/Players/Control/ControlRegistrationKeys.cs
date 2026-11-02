using System;
using System.Globalization;
using System.Text;

namespace APIShared.PlayerControl
{
    internal static class ControlRegistrationKeys
    {
        internal const int MaximumRegistrations = 64;
        internal static readonly Encoding WireText = new UTF8Encoding(false, true);
        internal static bool TryCreate(string ownerGuid, string registrationId, out string key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) ||
                ownerGuid.Length > 128 || registrationId.Length > 128) return false;
            try { WireText.GetByteCount(ownerGuid); WireText.GetByteCount(registrationId); }
            catch (EncoderFallbackException) { return false; }
            // Length-prefixing avoids collisions such as ("a/b","c") vs ("a","b/c").
            key = ownerGuid.Length.ToString(CultureInfo.InvariantCulture) + ":" + ownerGuid + registrationId;
            return true;
        }
    }
}
