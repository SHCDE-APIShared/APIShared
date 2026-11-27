using System;
using System.Collections.Generic;
using Iced.Intel;

namespace APIShared.Units.AiMilitary.Quota
{
    // Hash-bound read-only interception. These spans do not modify native counters:
    // real strength, crew staffing and the physical 20-engineer cap retain Vanilla values.
    internal static class AiQuotaNativeContract
    {
        internal const string Hash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int PlayerStride = 0x583C, UnitManager = 0x67E8400, UnitStride = 0x490;
        internal static readonly int[] Fields = { 0x379DEB8, 0x379DEBC, 0x379DEC0, 0x379E70C, 0x379E714,
            0x379E6EC, 0x379D978, 0x379D97C, 0x379D980, 0x379D984, 0x379D988, 0x379D98C,
            0x379D990, 0x379D994, 0x379D998, 0x379D99C, 0x379D9A0 };
        internal static readonly Site[] Sites = {
            new Site(0x1835F0,"8915CECC7A0044392B0F8E01060000",-1),
            new Site(0x183780,"490FBF8B820A000083F90A751C4869CF3C580000",-1),
            new Site(0x2F2FD,"4139842EAC1C13007C42418BDDEB3D",1),
            new Site(0x2F325,"33C08BD84969C5E405000041BD020000008B8438740100004139842EA81C1300410F4DDD",0),
            new Site(0x408A0,"428B8C1EB8DE79034585C97E118D049500000000",0),
            new Site(0x408E2,"398416BCDE79030F8D7A040000EB13",1),
            new Site(0x40CA6,"398416B8DE79038BD37C07E87A87FEFF",0),
            new Site(0x4096A,"0FBF84117C67CC073B8360010000",-1),
            new Site(0x40280,"43398429042513000F8D82010000",4),
            new Site(0x4048F,"43398429FC2413000F8D63010000",3),
            new Site(0x2ED6E,"42398C2BECE679030F8DF1020000",5),
            new Site(0x2CEE5,"41398C3278D979037D0D41B801000000",6),
            new Site(0x2CF10,"413984327CD979037D2649638432A8D97903",7),
            new Site(0x2CF4B,"4139843280D979037D0D41FFC0C705521E2A0001000000",8),
            new Site(0x2CF76,"4139843284D979037D0D41FFC0C7052F1E2A0001000000",9),
            new Site(0x2CF98,"4139843288D979037D0D41FFC0C705151E2A0001000000",10),
            new Site(0x2CFBA,"413984328CD979037D0D41FFC0C705FB1D2A0001000000",11),
            new Site(0x2CFE5,"4139843290D979037D0D41FFC0C705D81D2A0001000000",12),
            new Site(0x2D010,"4139843294D979037D0D41FFC0C705B51D2A0001000000",13),
            new Site(0x2D03B,"4139843298D979037D0D41FFC0C705921D2A0001000000",14),
            new Site(0x2D066,"413984329CD979037D0D41FFC0C7056F1D2A0001000000",15),
            new Site(0x2D091,"41398432A0D979037D0D41FFC0C7054C1D2A0001000000",16),
            new Site(0x2EDC7,"4C69F6880600004139440E3474094869DA3C580000EB0D6641837C0E5C020F8DE10000004C8D352E79C907",-1)
        };
        internal readonly struct Site
        {
            internal Site(int rva, string hex, int column) { Rva = rva; Bytes = Hex(hex); Column = column; }
            internal int Rva { get; }
            internal byte[] Bytes { get; }
            internal int Column { get; }
        }
        internal static byte[] Hex(string value)
        { var result = new byte[value.Length / 2]; for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return result; }
        internal static Instruction[] Decode(byte[] bytes, ulong ip)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = ip;
            var result = new List<Instruction>();
            while (decoder.IP < ip + (uint)bytes.Length)
            { var instruction = decoder.Decode(); if (instruction.IsInvalid || instruction.NextIP > ip + (uint)bytes.Length)
                    throw new InvalidOperationException("Invalid AI quota instruction boundary."); result.Add(instruction); }
            return result.ToArray();
        }
        internal static void Validate(ReadOnlySpan<byte> image)
        {
            foreach (var site in Sites)
                if (site.Rva < 0 || site.Rva > image.Length - site.Bytes.Length || !image.Slice(site.Rva, site.Bytes.Length).SequenceEqual(site.Bytes))
                    throw new InvalidOperationException("AI quota image contract differs at 0x" + site.Rva.ToString("X"));
        }
        // Arithmetic status bits written by CMP. All unrelated flags retain their entry value.
        internal static ulong CompareFlags(ulong flags, uint left, uint right)
        {
            uint result = unchecked(left - right); ulong bits = 0;
            if (left < right) bits |= 1;
            uint parity = result & 255; parity ^= parity >> 4; parity &= 15;
            if (((0x6996 >> (int)parity) & 1) == 0) bits |= 4;
            if (((left ^ right ^ result) & 16) != 0) bits |= 16;
            if (result == 0) bits |= 64;
            if ((result & 0x80000000) != 0) bits |= 128;
            if (((left ^ right) & (left ^ result) & 0x80000000) != 0) bits |= 2048;
            return (flags & ~0x8D5UL) | bits;
        }
    }
}
