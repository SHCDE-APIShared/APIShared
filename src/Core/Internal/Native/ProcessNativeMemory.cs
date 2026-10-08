using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace APIShared
{
    internal sealed class ProcessNativeMemory : INativeMemory
    {
        public byte ReadByte(long address) => Marshal.ReadByte(new IntPtr(address));
        public int ReadInt32(long address) => Marshal.ReadInt32(new IntPtr(address));
    }
}
