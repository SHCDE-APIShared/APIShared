using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.PlayerControl
{
    // Primitive for the current-image no-argument Chore handler ABI. It does not
    // publish a table entry, select a backend or grant control. A future table
    // owner must validate the entry, root both destinations before publication,
    // and never replace the stub when demand changes. The zero-demand path makes
    // no managed transition, reads no game state and tails the original once.
    internal static class ControlHandlerBridge
    {
        internal static void Emit(Assembler code, ulong demandCell, ulong original, ulong dispatch, byte opcode)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));
            if (demandCell == 0 || original == 0 || dispatch == 0 || opcode > 120)
                throw new ArgumentException("Complete, audited normal Chore handler destinations are required.");
            var active = code.CreateLabel();
            // A handler has no incoming arguments or flags. Only the ABI-volatile
            // R11 and flags are touched on the dormant branch; the complete
            // original handler still owns its result and all simulation effects.
            code.mov(r11, demandCell);
            code.cmp(__dword_ptr[r11], 0);
            code.jne(active);
            code.mov(r11, original);
            code.jmp(r11);
            code.Label(ref active);
            code.mov(ecx, (int)opcode);
            code.mov(r11, dispatch);
            code.jmp(r11);
        }
    }
}
