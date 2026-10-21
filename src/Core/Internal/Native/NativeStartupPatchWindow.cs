using System.Threading;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace APIShared
{
    // Detour backend selection for the synchronous startup run (startup cost).
    // RedBird's NativeX64 backend suspends every other process thread around each detour patch
    // (no-GC region, thread snapshot, suspend/resume; ~60 ms per detour on a typical machine).
    // That protects a patch site which another thread might execute while it is written.
    //
    // The window covers the run that raises CrusaderLibrary.LibraryLoaded: APIShared's own handler
    // and every handler invoked after it in the same synchronous run (BepInEx loads plugins in one
    // loop, and the Script Extender invokes a late subscriber's handler while it subscribes). The
    // native library has just loaded; no simulation, AI, pathing, market or unit-speech code has run.
    // The Script Extender writes its own detours there without suspension, and consumers such as
    // BugfixesAndQoL do the same for their detours. Without this window, shared hooks those
    // consumers request from their handlers (Assassin, gate routing, market prices) and APIShared's
    // own startup detours (audio, AIV build step) paid the suspension for every detour.
    //
    // The window is bound to the thread that opened it and closes on the next Unity main-thread
    // turn, which cannot run before the synchronous run returns. If no dispatcher is available it
    // covers only APIShared's own handler. Hooks installed later keep thread suspension; inline and
    // context hooks never suspend and are unaffected.
    internal static class NativeStartupPatchWindow
    {
        // Managed ID of the thread that opened the window (0 = closed).
        private static int windowThread;
        private static int opened;
        private static int closeAfterHandler;

        internal static bool IsOpen
        {
            get
            {
                int thread = Volatile.Read(ref windowThread);
                return thread != 0 && thread == Thread.CurrentThread.ManagedThreadId;
            }
        }

        // NativeX64 detour backends use this as their SuspendThreadsDuringPatch value.
        internal static bool SuspendThreads => !IsOpen;

        // Opens the window once per process, at the start of APIShared's LibraryLoaded handler.
        internal static void OpenForStartupRun()
        {
            if (Interlocked.CompareExchange(ref opened, 1, 0) != 0)
                return;
            Volatile.Write(ref windowThread, Thread.CurrentThread.ManagedThreadId);
            if (!APIShared.Internal.UnityMainThreadDispatch.TryEnqueue(Close))
                Volatile.Write(ref closeAfterHandler, 1);
        }

        // End of APIShared's own handler; closes the window only when no dispatcher turn will.
        internal static void EndApiSharedHandler()
        {
            if (Volatile.Read(ref closeAfterHandler) != 0)
                Close();
        }

        internal static void Close() => Volatile.Write(ref windowThread, 0);

        // Same NativeX64 options without thread suspension inside the window; otherwise the given
        // backend (null = the transaction default). Other backends are never changed.
        internal static IHookBackend Backend(IHookBackend backend = null)
        {
            if (!IsOpen)
                return backend;
            NativeDetourOptions options = backend == null
                ? NativeDetourBackend.Instance.Options
                : (backend as NativeDetourBackend)?.Options;
            if (options == null || !options.SuspendThreadsDuringPatch)
                return backend;
            return new NativeDetourBackend(options with { SuspendThreadsDuringPatch = false });
        }

        // Test-only reset of the once-per-process state.
        internal static void ResetForTest()
        {
            Volatile.Write(ref windowThread, 0);
            Volatile.Write(ref opened, 0);
            Volatile.Write(ref closeAfterHandler, 0);
        }
    }
}
