using System;
using System.IO;

namespace APIShared.Units.AiMilitary.Quota
{
    // This is a mirror of contributions at the original native counter fold, not a
    // current-unit recount. Units can die/transition between that fold and a quota read.
    internal sealed class AiQuotaSnapshot
    {
        internal const int Players = 9, Columns = 17;
        internal const int Defender = 0, Harasser = 1, Sieger = 2, Economy = 3, Bodyguard = 4, Machine = 5;
        private readonly int[,] extras = new int[Players, Columns];
        internal long Fold { get; private set; }
        internal bool HasSnapshot { get; private set; }
        internal void Clear() { Array.Clear(extras, 0, extras.Length); Fold = 0; HasSnapshot = false; }
        internal void BeginFold() { Array.Clear(extras, 0, extras.Length); Fold++; HasSnapshot = true; }
        internal void Count(int player, int type, int role)
        {
            if (player < 0 || player >= Players || type == 55) return; // Lord excluded at native site
            if (role == 10) { extras[player, 6]++; extras[player, Sieger]++; return; }
            if (type == 30) return; // Engineer only contributes through the native role-10 exception
            int column;
            switch (role)
            {
                case 1: case 4: column = Defender; break;
                case 2: column = Harasser; break;
                case 6: column = Economy; break;
                case 7: column = Bodyguard; break;
                case 21: column = Machine; break;
                default:
                    if (role < 11 || role > 20) return;
                    extras[player, 6 + role - 10]++; extras[player, Sieger]++; return;
            }
            extras[player, column]++;
        }
        internal int GetExtra(int player, int column) => player >= 0 && player < Players &&
            column >= 0 && column < Columns && HasSnapshot ? extras[player, column] : 0;
        internal int Regular(int physical, int player, int column) => Math.Max(0, physical - GetExtra(player, column));
        internal void Write(BinaryWriter writer)
        {
            writer.Write(HasSnapshot); writer.Write(Fold);
            for (int p = 0; p < Players; p++) for (int c = 0; c < Columns; c++) writer.Write(extras[p, c]);
        }
        internal static AiQuotaSnapshot Read(BinaryReader reader)
        {
            var value = new AiQuotaSnapshot { HasSnapshot = reader.ReadBoolean(), Fold = reader.ReadInt64() };
            if (value.Fold < 0) throw new InvalidDataException("Negative quota fold.");
            for (int p = 0; p < Players; p++) for (int c = 0; c < Columns; c++)
            {
                int n = reader.ReadInt32();
                if (n < 0 || n > 10000) throw new InvalidDataException("Invalid additional fold count.");
                value.extras[p, c] = n;
            }
            return value;
        }
        internal void Restore(AiQuotaSnapshot value)
        { Array.Copy(value.extras, extras, extras.Length); Fold = value.Fold; HasSnapshot = value.HasSnapshot; }
    }
}
