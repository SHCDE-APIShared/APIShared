using BepInEx.Logging;
using RedBird.Core.Memory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared
{
    internal sealed class GatehouseBuildTarget
    {
        public static GatehouseBuildTarget Supported { get; } = new GatehouseBuildTarget(
            "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2",
            0xB73D0,
            2325,
            "F73E9FF6F69D9EC1ECD59D528BC6D4861739F54E0A9C59C6E6BAD91369FA57C8",
            0xB7B70,
            Hex("0F BF 8C 2A 0E 8B 7E 06 42 8D 2C FD 00 00 00 00 8B C1 44 8B C5 2B C5 44 2B C1 3B E9 42 8D 2C E5 00 00 00 00 44 0F 4E C0 48 8D 05 61 84 F4 FF 0F BF 8C 02 10 8B 7E 06 8B D5 2B D1 8B C1 2B C5 3B E9 0F 4E D0 44 3B C2 44 0F 4C C2"),
            0xB7BBB,
            Hex("40 84 F6 75 10 41 81 F8 C8 00 00 00 7D 10 B8 B0 04 00 00 EB 69 41 81 F8 8C 00 00 00 7C 5B"),
            0xB7C32,
            Hex("EB 50 B8 64 00 00 00 48 8D 2D C0 83 F4 FF"),
            0xB7BC3,
            0xB7BCA,
            0xB7BD3,
            0xB7C35);

        public GatehouseBuildTarget(
            string buildHash,
            int functionRva,
            int functionSize,
            string functionHash,
            int distanceBlockRva,
            byte[] vanillaDistanceBlockBytes,
            int decisionBlockRva,
            byte[] decisionBlockBytes,
            int humanDelayBlockRva,
            byte[] humanDelayBlockBytes,
            int aiCloseDistanceRva,
            int aiReopenDelayRva,
            int humanCloseDistanceRva,
            int humanReopenDelayRva)
        {
            BuildHash = buildHash;
            FunctionRva = functionRva;
            FunctionSize = functionSize;
            FunctionHash = functionHash;
            DistanceBlockRva = distanceBlockRva;
            VanillaDistanceBlockBytes = vanillaDistanceBlockBytes;
            DecisionBlockRva = decisionBlockRva;
            DecisionBlockBytes = decisionBlockBytes;
            HumanDelayBlockRva = humanDelayBlockRva;
            HumanDelayBlockBytes = humanDelayBlockBytes;
            AiCloseDistanceRva = aiCloseDistanceRva;
            AiReopenDelayRva = aiReopenDelayRva;
            HumanCloseDistanceRva = humanCloseDistanceRva;
            HumanReopenDelayRva = humanReopenDelayRva;
        }

        public string BuildHash { get; }
        public int FunctionRva { get; }
        public int FunctionSize { get; }
        public int FunctionEndRva => checked(FunctionRva + FunctionSize);
        public string FunctionHash { get; }
        public int DistanceBlockRva { get; }
        public byte[] VanillaDistanceBlockBytes { get; }
        public int DecisionBlockRva { get; }
        public byte[] DecisionBlockBytes { get; }
        public int HumanDelayBlockRva { get; }
        public byte[] HumanDelayBlockBytes { get; }
        public int AiCloseDistanceRva { get; }
        public int AiReopenDelayRva { get; }
        public int HumanCloseDistanceRva { get; }
        public int HumanReopenDelayRva { get; }

        private static byte[] Hex(string text)
        {
            string[] tokens = text.Split(' ');
            var bytes = new byte[tokens.Length];
            for (int index = 0; index < tokens.Length; index++)
                bytes[index] = Convert.ToByte(tokens[index], 16);
            return bytes;
        }
    }
}
