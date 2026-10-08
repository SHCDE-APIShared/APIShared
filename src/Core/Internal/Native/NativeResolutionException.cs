using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace APIShared
{
    internal sealed class NativeResolutionException : Exception
    {
        public NativeResolutionException(NativeCapabilityState state, string message) : base(message) => State = state;
        public NativeCapabilityState State { get; }
    }
}
