using BepInEx.Logging;
using RedBird.Core.Memory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared
{
    internal readonly struct NativeByteInvariant
    {
        public NativeByteInvariant(long address, byte value)
        {
            Address = address;
            Value = value;
        }

        public long Address { get; }
        public byte Value { get; }
    }

    internal sealed class GatehouseTimingTarget
    {
        public const int VanillaAiDistance = 200;
        public const int VanillaAiDelay = 1200;
        public const int VanillaHumanDistance = 140;
        public const int VanillaHumanDelay = 100;

        public GatehouseTimingTarget(
            long aiDistance,
            long aiDelay,
            long humanDistance,
            long humanDelay,
            IReadOnlyList<NativeByteInvariant> instructionInvariants = null)
        {
            AiDistance = aiDistance;
            AiDelay = aiDelay;
            HumanDistance = humanDistance;
            HumanDelay = humanDelay;
            InstructionInvariants = instructionInvariants ?? Array.Empty<NativeByteInvariant>();
            Intervals = new[]
            {
                new NativeInterval(aiDistance, aiDistance + 4),
                new NativeInterval(aiDelay, aiDelay + 4),
                new NativeInterval(humanDistance, humanDistance + 4),
                new NativeInterval(humanDelay, humanDelay + 4)
            };
        }

        public long AiDistance { get; }
        public long AiDelay { get; }
        public long HumanDistance { get; }
        public long HumanDelay { get; }
        public IReadOnlyList<NativeInterval> Intervals { get; }
        public IReadOnlyList<NativeByteInvariant> InstructionInvariants { get; }
    }
}
