using System;
using System.Collections.Generic;

namespace APIShared.Audio
{
    internal sealed class SoundSource
    {
        internal readonly int Index, ReturnRva, UnitType, Flag;
        internal readonly string Key;
        internal readonly int[] Effects;
        internal SoundSource(int index, int returnRva, int type, int flag, string key, params int[] effects)
        { Index = index; ReturnRva = returnRva; UnitType = type; Flag = flag; Key = key; Effects = effects; }
        internal bool Accepts(int effect) => Array.IndexOf(Effects, effect) >= 0;
        internal SoundSituation Situation(int effect)
        {
            if (ReturnRva == 0x15D2C6 || !Accepts(effect)) return SoundSituation.Unknown;
            switch (effect)
            {
                case 5: case 70: case 172: case 231: case 237: case 243: case 292: return SoundSituation.RangedWeaponFire;
                case 71: case 168: return SoundSituation.RangedWeaponReload;
                case 65: case 66: return SoundSituation.MeleeMetalContact;
                case 145: case 230: case 233: case 304: case 306: return SoundSituation.MeleeWeaponSwing;
                case 163: return SoundSituation.AttackStone;
                case 164: return SoundSituation.AttackWood;
                case 69: return SoundSituation.MoatDigging;
                case 158: return SoundSituation.TunnelDigging;
                case 160: return SoundSituation.TunnelCollapse;
                case 195: return SoundSituation.LadderPlacement;
                case 196: return SoundSituation.LadderBreaking;
                case 226: case 227: case 228: case 229: return SoundSituation.Climbing;
                case 90: case 245: return SoundSituation.MountedDeath;
                case 91: return SoundSituation.MountedFall;
                case 180: return SoundSituation.ArabianIdleSwordDraw;
                case 179: return SoundSituation.ArabianIdleSwordSheathe;
                case 232: case 221: return SoundSituation.FireSetting;
                case 102: return SoundSituation.OilPouring;
                case 299: case 305: return SoundSituation.WallDemolition;
                default: return SoundSituation.Unknown;
            }
        }
        internal SituationEvidence Evidence(int effect) => Situation(effect) == SoundSituation.Unknown ? SituationEvidence.Unknown :
            ReturnRva == 0x1700D2 && effect == 180 ? SituationEvidence.InGameConfirmed : SituationEvidence.Inferred;
    }
    internal static class SoundSources
    {
        internal const int MarkerStride = 330;
        internal static readonly SoundSource[] All = {
            new SoundSource(0, 0x12EB51, 5, 0, "Unit5.Source01", 158),
            new SoundSource(1, 0x12F076, 5, 0, "Unit5.Source02", 160),
            new SoundSource(2, 0x12F46A, 5, 0, "Unit5.Source03", 163, 164),
            new SoundSource(3, 0x1404E8, 22, 0, "Unit22.Source01", 69),
            new SoundSource(4, 0x140734, 22, 0, "Unit22.Source02", 69),
            new SoundSource(5, 0x140EF2, 22, 0, "Unit22.Source03", 5, 172),
            new SoundSource(6, 0x1427B1, 23, 0, "Unit23.Source01", 71),
            new SoundSource(7, 0x142D04, 23, 0, "Unit23.Source02", 70),
            new SoundSource(8, 0x143FBB, 24, 0, "Unit24.Source01", 163, 164),
            new SoundSource(9, 0x1445BF, 24, 0, "Unit24.Source02", 69),
            new SoundSource(10, 0x1447FE, 24, 0, "Unit24.Source03", 69),
            new SoundSource(11, 0x14594A, 25, 0, "Unit25.Source01", 163, 164),
            new SoundSource(12, 0x145D0B, 25, 0, "Unit25.Source02", 69),
            new SoundSource(13, 0x145F4B, 25, 0, "Unit25.Source03", 69),
            new SoundSource(14, 0x1473EF, 26, 0, "Unit26.Source01", 163, 164),
            new SoundSource(15, 0x1477C3, 26, 0, "Unit26.Source02", 69),
            new SoundSource(16, 0x147A0D, 26, 0, "Unit26.Source03", 69),
            new SoundSource(17, 0x148118, 26, 0, "Unit26.Source04", 145),
            new SoundSource(18, 0x14896C, 27, 0, "Unit27.Source01", 163, 164),
            new SoundSource(19, 0x148DB9, 27, 0, "Unit27.Source02", 65, 66),
            new SoundSource(20, 0x148DF8, 27, 0, "Unit27.Source03", 145),
            new SoundSource(21, 0x1499BD, 28, 0, "Unit28.Source01", 163, 164),
            new SoundSource(22, 0x149D0C, 28, 0, "Unit28.Source02", 65),
            new SoundSource(23, 0x149D52, 28, 0, "Unit28.Source03", 145),
            new SoundSource(24, 0x14A187, 28, 0, "Unit28.Source04", 90),
            new SoundSource(25, 0x14A1CA, 28, 0, "Unit28.Source05", 91),
            new SoundSource(26, 0x14A9EB, 29, 0, "Unit29.Source01", 195),
            new SoundSource(27, 0x14ACFC, 29, 0, "Unit29.Source02", 196),
            new SoundSource(28, 0x14BAA7, 30, 0, "Unit30.Source01", 69),
            new SoundSource(29, 0x14BD00, 30, 0, "Unit30.Source02", 69),
            new SoundSource(30, 0x14D23A, 30, 0, "Unit30.Source03", 102),
            new SoundSource(31, 0x15174F, 37, 0, "Unit37.Source01", 163, 164),
            new SoundSource(32, 0x151E15, 37, 0, "Unit37.Source02", 145),
            new SoundSource(33, 0x15BDE6, 55, 0, "Unit55.Source01", 233),
            new SoundSource(34, 0x15BEA1, 55, 0, "Unit55.Source02", 163, 164),
            new SoundSource(35, 0x15D053, 55, 0, "Unit55.Source03", 233),
            new SoundSource(36, 0x15D169, 55, 0, "Unit55.Source04", 65),
            new SoundSource(37, 0x15D2C6, 55, 1, "Unit55.Source05", 244),
            new SoundSource(38, 0x169045, 70, 0, "Unit70.Source01", 5, 172),
            new SoundSource(39, 0x16A3AD, 71, 0, "Unit71.Source01", 232),
            new SoundSource(40, 0x16A424, 71, 0, "Unit71.Source02", 221),
            new SoundSource(41, 0x16A76F, 71, 0, "Unit71.Source03", 232),
            new SoundSource(42, 0x16A973, 71, 0, "Unit71.Source04", 69),
            new SoundSource(43, 0x16ABBB, 71, 0, "Unit71.Source05", 69),
            new SoundSource(44, 0x16BE39, 72, 0, "Unit72.Source01", 231),
            new SoundSource(45, 0x16D43B, 73, 0, "Unit73.Source01", 163, 164),
            new SoundSource(46, 0x16D7D3, 73, 0, "Unit73.Source02", 226),
            new SoundSource(47, 0x16D814, 73, 0, "Unit73.Source03", 227),
            new SoundSource(48, 0x16D9A4, 73, 0, "Unit73.Source04", 228),
            new SoundSource(49, 0x16DCEB, 73, 0, "Unit73.Source05", 229),
            new SoundSource(50, 0x16DF18, 73, 0, "Unit73.Source06", 65),
            new SoundSource(51, 0x16DF57, 73, 0, "Unit73.Source07", 230),
            new SoundSource(52, 0x16EBE1, 74, 0, "Unit74.Source01", 163, 164),
            new SoundSource(53, 0x16EF41, 74, 0, "Unit74.Source02", 65),
            new SoundSource(54, 0x16EF8E, 74, 0, "Unit74.Source03", 145),
            new SoundSource(55, 0x16F2FD, 74, 0, "Unit74.Source04", 90),
            new SoundSource(56, 0x16F347, 74, 0, "Unit74.Source05", 91),
            new SoundSource(57, 0x16FB1E, 75, 0, "Unit75.Source01", 163, 164),
            new SoundSource(58, 0x16FEA6, 75, 0, "Unit75.Source02", 65),
            new SoundSource(59, 0x16FEE5, 75, 0, "Unit75.Source03", 145),
            new SoundSource(60, 0x1700D2, 75, 0, "Unit75.Source04", 180),
            new SoundSource(61, 0x170134, 75, 0, "Unit75.Source05", 179),
            new SoundSource(62, 0x171535, 76, 0, "Unit76.Source01", 237),
            new SoundSource(63, 0x172AD2, 77, 0, "Unit77.Source01", 168),
            new SoundSource(64, 0x172BF2, 77, 0, "Unit77.Source02", 243),
            new SoundSource(65, 0x1737BE, 78, 0, "Unit78.Source01", 163, 164),
            new SoundSource(66, 0x173E73, 78, 0, "Unit78.Source02", 245),
            new SoundSource(67, 0x173EB6, 78, 0, "Unit78.Source03", 91),
            new SoundSource(68, 0x1758DC, 80, 0, "Unit80.Source01", 69),
            new SoundSource(69, 0x175B22, 80, 0, "Unit80.Source02", 69),
            new SoundSource(70, 0x175D77, 80, 0, "Unit80.Source03", 305),
            new SoundSource(71, 0x176156, 80, 0, "Unit80.Source04", 65),
            new SoundSource(72, 0x17619B, 80, 0, "Unit80.Source05", 304),
            new SoundSource(73, 0x1761F1, 80, 0, "Unit80.Source06", 306),
            new SoundSource(74, 0x177B2F, 81, 0, "Unit81.Source01", 237),
            new SoundSource(75, 0x178F6F, 82, 0, "Unit82.Source01", 163, 164),
            new SoundSource(76, 0x17933B, 82, 0, "Unit82.Source02", 69),
            new SoundSource(77, 0x179585, 82, 0, "Unit82.Source03", 69),
            new SoundSource(78, 0x179C08, 82, 0, "Unit82.Source04", 292),
            new SoundSource(79, 0x17ACA6, 83, 0, "Unit83.Source01", 163, 164),
            new SoundSource(80, 0x17B2D4, 83, 0, "Unit83.Source02", 245),
            new SoundSource(81, 0x17B317, 83, 0, "Unit83.Source03", 91),
            new SoundSource(82, 0x17BF2D, 84, 0, "Unit84.Source01", 163, 164),
            new SoundSource(83, 0x17C2F7, 84, 0, "Unit84.Source02", 69),
            new SoundSource(84, 0x17C541, 84, 0, "Unit84.Source03", 69),
            new SoundSource(85, 0x17CC91, 84, 0, "Unit84.Source04", 145),
            new SoundSource(86, 0x17DC63, 85, 0, "Unit85.Source01", 299),
            new SoundSource(87, 0x17E05F, 85, 0, "Unit85.Source02", 69),
            new SoundSource(88, 0x17E2A3, 85, 0, "Unit85.Source03", 69),
            new SoundSource(89, 0x17E449, 85, 0, "Unit85.Source04", 69),
            new SoundSource(90, 0x17F017, 85, 0, "Unit85.Source05", 145),
        };
        private static readonly Dictionary<int, SoundSource> returns = Build();
        private static Dictionary<int, SoundSource> Build()
        { var result = new Dictionary<int, SoundSource>(); foreach (var entry in All) result.Add(entry.ReturnRva, entry); return result; }
        internal static bool TryGet(int returnRva, out SoundSource value) => returns.TryGetValue(returnRva, out value);
        internal static bool TryDecode(short marker, int effect, int flag, out SoundSource entry)
        {
            entry = null;
            int code = marker - short.MinValue, index = code / MarkerStride;
            if (marker >= -1 || index >= All.Length || code % MarkerStride != effect) return false;
            var source = All[index];
            if (source.Flag != flag || source.Situation(effect) == SoundSituation.Unknown) return false;
            entry = source; return true;
        }
        internal static short MarkerFor(SoundSource source, int effect) => checked((short)(short.MinValue + source.Index * MarkerStride + effect));

        // Fatal branch of the melee damage routine 0x199110 (taken once victim health is <= 0).
        // Each cue is selected by the attacker type: the kill switch at 0x199652 (table 0x199AD4,
        // index 0x199AF0, type-3) and the special-victim switch at 0x1992E2 (table 0x1999A0,
        // index 0x1999B8, type-3). Type lists mirror those tables; other effects reaching these
        // CALLs (victim death voices, siege-victim cues) are deliberately not kill cues.
        private static readonly int[] Axes = { 3, 84 }, Lord = { 55 }, Spears = { 22, 24, 70, 72, 74, 76, 82 },
            Pikes = { 25, 78 }, Maces = { 26, 85 }, Swords = { 27, 28, 73, 75, 80, 81, 83 };
        internal static readonly SoundKillSource[] Kills = {
            // CALL 0x199952 at the victim tile; ebx selects the special cue, victim+0x380 bit 0 the variant.
            new SoundKillSource(0x199957, "MeleeKill.Source01", false,
                Pair(240, Axes, Lord, Pikes), Pair(241, Lord, Pikes), Pair(242, Axes, Lord), Pair(186, Pikes),
                Pair(118, Spears), Pair(153, Spears), Pair(187, Spears),
                Pair(116, Maces), Pair(151, Maces), Pair(185, Maces), Pair(188, Swords)),
            new SoundKillSource(0x199823, "MeleeKill.Source02", false, Pair(119, Swords)),
            new SoundKillSource(0x199837, "MeleeKill.Source03", false, Pair(154, Swords)),
            // CALL 0x199314 at the attacker tile, only for a victim with the native byte at +0x328 set.
            // That victim then skips the victim-tile cue above (0x199462).
            new SoundKillSource(0x199319, "MeleeKill.Source04", true,
                Pair(242, Axes, Lord), Pair(187, new[] { 24, 70, 72, 74, 76, 82 }), Pair(186, Pikes), Pair(185, Maces), Pair(188, Swords)),
        };
        private static readonly Dictionary<int, SoundKillSource> killReturns = BuildKills();
        private static Dictionary<int, SoundKillSource> BuildKills()
        { var result = new Dictionary<int, SoundKillSource>(); foreach (var entry in Kills) result.Add(entry.ReturnRva, entry); return result; }
        internal static bool TryGetKill(int returnRva, out SoundKillSource value) => killReturns.TryGetValue(returnRva, out value);
        private static KeyValuePair<int, int[]> Pair(int effect, params int[][] groups)
        {
            var types = new List<int>(); foreach (var group in groups) types.AddRange(group);
            types.Sort(); return new KeyValuePair<int, int[]>(effect, types.ToArray());
        }
    }

    // A kill cue is attributed to the attacker and victim passed to the melee damage routine,
    // never to the current-unit global: instant-kill callers (0x1AD64, 0x1ADD4, 0x198F84) pass
    // units that need not be the one whose update is running.
    internal sealed class SoundKillSource
    {
        internal readonly int ReturnRva;
        internal readonly string Key;
        internal readonly bool AttackerTile;
        private readonly Dictionary<int, int[]> effects = new Dictionary<int, int[]>();
        internal SoundKillSource(int returnRva, string key, bool attackerTile, params KeyValuePair<int, int[]>[] cues)
        { ReturnRva = returnRva; Key = key; AttackerTile = attackerTile; foreach (var cue in cues) effects.Add(cue.Key, cue.Value); }
        internal bool Accepts(int effect) => effects.ContainsKey(effect);
        internal bool Accepts(int effect, int attackerType) => effects.TryGetValue(effect, out var types) && Array.BinarySearch(types, attackerType) >= 0;
        internal IEnumerable<KeyValuePair<int, int[]>> Cues => effects;
        // The routine has already written health 0 on this branch (0x19925C) and takes the
        // tiles from the attacker (0x199314) or the victim (other CALLs) without adjustment.
        internal bool Matches(int effect, int attackerType, short attackerX, short attackerY,
            short victimX, short victimY, uint victimHealth, short x, short y) =>
            Accepts(effect, attackerType) && victimHealth == 0 &&
            (AttackerTile ? attackerX == x && attackerY == y : victimX == x && victimY == y);
    }
}
