using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace APIShared
{
    internal interface INativeMemory
    {
        byte ReadByte(long address);
        int ReadInt32(long address);
    }
}
