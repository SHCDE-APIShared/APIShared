using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Presentation
{
    // The 21-short output protocol, not persistent/native message-manager state.
    internal readonly struct LordMessageGroup
    {
        internal readonly int Header, Audio, Video, Section, TextIndex;
        internal readonly AILords? Lord;
        internal readonly int? Player;
        internal readonly AILordMessageType Type;
        internal LordMessageGroup(int header, int audio, int video, int section, int text, AILords? lord, int? player, AILordMessageType type)
        { Header = header; Audio = audio; Video = video; Section = section; TextIndex = text; Lord = lord; Player = player; Type = type; }
    }

    internal static class LordMessageGroups
    {
        internal const int Words = 21;
        // Native wrapper's text stride includes one nickname/reserved position after categories 1..33.
        internal const int TextStride = 34;
        internal static bool TryMatch(short[] records, int count, int header, out LordMessageGroup group)
        {
            group = default;
            if (records == null || count < 0 || count > records.Length / Words || header < 0 || header >= count) return false;
            int at = header * Words;
            // FEE50 passes audio-present as [13], video-present as [14]. Announcements do not have this complete group.
            if (records[at] != 20 || records[at + 13] != 1 || records[at + 14] != 1) return false;
            int section = records[at + 11], text = records[at + 12];
            AILords? lord = null; int? player = null; int category;
            if (section == (int)global::Enums.eTextSections.TEXT_SKIRMISH_SPEECH)
            {
                if (text <= 0) return false;
                int lordValue = (text - 1) / TextStride + (int)AILords.SK_RAT;
                category = (text - 1) % TextStride + 1;
                if (lordValue < (int)AILords.SK_RAT || lordValue > (int)AILords.SK_DLC4B) return false;
                lord = (AILords)lordValue;
            }
            else
            {
                if (section >= 0 || section < -SHCDESE.API.GamePlayerManagerAPI.MAX_PLAYERS) return false;
                player = -section; category = text;
            }
            if (category < (int)AILordMessageType.Taunt1 || category > (int)AILordMessageType.WillAttackEnemy) return false;

            int speech = -1; bool controls = false;
            for (int index = header + 1; index < count; index++)
            {
                int offset = index * Words, kind = records[offset];
                if (speech < 0)
                {
                    // Stop-channel calls for 3/4 before the speech are no-ops in this native contract.
                    if (kind != 18 || records[offset + 12] != 1 || records[offset + 11] != 100) return false;
                    speech = index; continue;
                }
                if (!controls)
                {
                    if (kind != 22) return false;
                    controls = true; continue;
                }
                if (kind == 21)
                {
                    if (records[offset + 3] < 0) continue; // Optional UI transition can stop an old Bink before this one.
                    if (records[offset + 3] != 0 || records[offset + 11] != 1) return false;
                    group = new LordMessageGroup(header, speech, index, section, text, lord, player, (AILordMessageType)category);
                    return true;
                }
                // FEE50's optional frontend transition may stop/change music or ambient playback.
                // No arbitrary tile/UI records or another header may be crossed to find a plausible video.
                if (kind != 19 && kind != 17) return false;
            }
            return false;
        }
    }
}
