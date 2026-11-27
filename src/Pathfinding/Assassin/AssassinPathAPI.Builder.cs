using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared
{
    public static unsafe partial class AssassinPathAPI
    {
        /// <summary>Runs Vanilla through the shared original trampoline; it never calls a registered replacement.</summary>
        public static int RunVanillaBuilder(IntPtr context, int startX, int startY,
            int targetX, int targetY, int maximumNodes, int continuation)
        {
            // The handle is published with its original before the patch is written (EnsureInstalled), so a
            // native call cannot outrun it; IsInstalled becomes true only after whole-feature validation.
            DetourHandle<AssassinPathBuilder> published = Volatile.Read(ref builderHook);
            if (published == null || !published.TryGetOriginal(out AssassinPathBuilder original))
                throw new InvalidOperationException("Shared Assassin path hooks are unavailable.");
            int result = original(context, startX, startY, targetX, targetY, maximumNodes, continuation);
            BuildFrame frame = buildFrame;
            if (frame != null && frame.Depth != 0 && frame.Context == context) frame.Complete(result, ReadGeneration(context));
            return result;
        }

        // Vanilla 0xD9C40 starts every new search (continuation == 0) by incrementing the int at context+4
        // (D9D00) and stamps each tile it writes with it (D9F47); a stamp equal to the generation marks a
        // valid distance. Past 32000 it resets the generation to 1 and clears the stamp array (D9D0C-D9D26).
        // A consumer that writes the field must advance the generation first, as Vanilla does.
        private const int GenerationOffset = 4;

        private static unsafe int ReadGeneration(IntPtr context) =>
            context == IntPtr.Zero ? 0 : *(int*)((byte*)context + GenerationOffset);

        // Per thread that runs Build (the native pathfinding caller): whether the handler of the current
        // Build call completed Vanilla, its result and the generation right after it. Nested Build calls
        // keep the outer call's record (Enter/Leave).
        private sealed class BuildFrame
        {
            internal IntPtr Context;
            internal int Depth, VanillaResult, VanillaGeneration;
            internal bool VanillaCompleted;

            internal readonly struct Saved
            {
                internal readonly IntPtr Context;
                internal readonly int VanillaResult, VanillaGeneration;
                internal readonly bool VanillaCompleted;
                internal Saved(BuildFrame frame)
                {
                    Context = frame.Context; VanillaResult = frame.VanillaResult;
                    VanillaGeneration = frame.VanillaGeneration; VanillaCompleted = frame.VanillaCompleted;
                }
            }

            internal Saved Enter(IntPtr context)
            {
                var outer = new Saved(this);
                Context = context; VanillaCompleted = false; Depth++;
                return outer;
            }

            internal void Leave(Saved outer)
            {
                Depth--;
                Context = outer.Context; VanillaResult = outer.VanillaResult;
                VanillaGeneration = outer.VanillaGeneration; VanillaCompleted = outer.VanillaCompleted;
            }

            internal void Complete(int result, int generation)
            {
                VanillaCompleted = true; VanillaResult = result; VanillaGeneration = generation;
            }
        }

        [ThreadStatic] private static BuildFrame buildFrame;

        // Cold path: the first Build on a thread.
        private static BuildFrame CreateFrame() => buildFrame = new BuildFrame();

        private static int Build(IntPtr context, int startX, int startY, int targetX,
            int targetY, int maximumNodes, int continuation)
        {
            // Native boundary (issue #11): no managed exception may unwind into the native pathfinder, from
            // the handler, the first-call log or the frame setup. The success path adds only these protected
            // regions and the per-thread frame record; all failure work is in BuilderFailed.
            try
            {
                if (!publication.IsReady)
                    return RunVanillaBuilder(context, startX, startY, targetX, targetY, maximumNodes, continuation);
                if (Interlocked.Exchange(ref callbackLogged, 1) == 0)
                    NativeApiLog.Debug(log, "Assassin shared runtime hook confirmed after startup; " +
                        $"source=({startX},{startY}), target=({targetX},{targetY}), limit={maximumNodes}, continuation={continuation}.");
                BuildFrame frame = buildFrame ?? CreateFrame();
                BuildFrame.Saved outer = frame.Enter(context);
                try
                {
                    AssassinPathBuilder handler = Volatile.Read(ref weightedBuilder);
                    return handler == null
                        ? RunVanillaBuilder(context, startX, startY, targetX, targetY, maximumNodes, continuation)
                        : handler(context, startX, startY, targetX, targetY, maximumNodes, continuation);
                }
                catch (Exception error)
                {
                    return BuilderFailed(error, frame, context, startX, startY, targetX, targetY, maximumNodes, continuation);
                }
                finally { frame.Leave(outer); }
            }
            catch (Exception error)
            {
                return BuilderFailed(error, null, context, startX, startY, targetX, targetY, maximumNodes, continuation);
            }
        }

        // Cold path of Build (issue #36). Logs the first failure of the process, then answers this native call
        // without ever returning a partially replaced field:
        // - the handler completed Vanilla and the generation is unchanged since: Vanilla's field is intact, so
        //   its first result is returned and Vanilla does not run again;
        // - Vanilla did not run: it runs once, with the call's arguments;
        // - the handler changed the field after Vanilla: the field is rebuilt by a fresh search
        //   (continuation 0), because a resumed search cannot be replayed (Vanilla continues its queue in
        //   the context), while a fresh search from the same state rebuilds the same field.
        // A route the handler staged for this request is dropped, so the frame publishes none.
        // Besides the field, a Vanilla run only increments the generation and a per-run counter at
        // context+0xBC (D9D50) and, every 32000 searches, clears the stamp array; it draws no random
        // numbers and raises no events.
        private static int BuilderFailed(Exception error, BuildFrame frame, IntPtr context, int startX, int startY,
            int targetX, int targetY, int maximumNodes, int continuation)
        {
            if (Volatile.Read(ref builderFailureLogged) == 0 && Interlocked.Exchange(ref builderFailureLogged, 1) == 0)
            {
                try { NativeApiLog.Error(log, "Assassin path callback failed; Vanilla result returned. Later failures are not logged: " + error); }
                catch { }
            }
            try
            {
                AssassinRouteHandoff.DiscardStaged(context);
                bool completed = frame != null && frame.VanillaCompleted && frame.Context == context;
                if (completed && ReadGeneration(context) == frame.VanillaGeneration) return frame.VanillaResult;
                return RunVanillaBuilder(context, startX, startY, targetX, targetY, maximumNodes, completed ? 0 : continuation);
            }
            catch
            {
                return 0; // Last resort: Vanilla itself failed, and nothing may unwind into native code.
            }
        }
    }
}
