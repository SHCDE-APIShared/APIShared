using System;
using System.Threading;
using System.Reflection;
using APIShared.Events;
using BepInEx.Logging;
using CrusaderDE;

namespace APIShared.Lobby
{
    /// <summary>Immutable text passed to the local Vanilla lobby renderer. It conveys no gameplay authority.</summary>
    public sealed class LobbyChatMessage
    {
        /// <summary>Vanilla display name; not an authenticated identity.</summary>
        public string Name { get; }
        /// <summary>Unmodified received text.</summary>
        public string Message { get; }
        /// <summary>Whether Vanilla already classified the message as a system hint.</summary>
        public bool IsSystemMessage { get; }
        internal LobbyChatMessage(string name, string message, bool system)
        { Name = name; Message = message; IsSystemMessage = system; }
    }

    /// <summary>Shared classification of lobby system hints without changing text or sending packets.</summary>
    /// <remarks>Permanent owner/ID registrations run synchronously on the validated Unity thread in ordinal
    /// owner/ID order. Return true to retain this message in muted chat. Rules only OR the system flag;
    /// they cannot hide messages, alter text, authorize actions or invoke the renderer twice. Exceptions
    /// are isolated and logged. Other threads and recursive calls follow Vanilla. No replay or teardown.</remarks>
    public static class LobbyChatPresentation
    {
        private delegate void Render(FRONT_Multiplayer self, string name, string text, int colour, bool system);
        private static readonly ManagedInterceptionHook<Render> Hook = new ManagedInterceptionHook<Render>();
        private static readonly LobbyChatPresentationDispatch Dispatch = new LobbyChatPresentationDispatch(Report);
        private static ManualLogSource log;
        private static int mainThread;
        [ThreadStatic] private static bool classifying;
        /// <summary>Whether the permanent Vanilla renderer hook initialized after LibraryLoaded.</summary>
        public static bool IsAvailable { get; private set; }
        /// <summary>Registers a permanent system-message rule; invalid or duplicate identities return false.</summary>
        public static bool TryRegisterSystemMessageRule(string ownerGuid, string registrationId,
            Func<LobbyChatMessage, bool> rule, out string diagnostic) =>
            Dispatch.Rules.TryAdd(ownerGuid, registrationId, rule, out diagnostic);
        internal static void Initialize(ManualLogSource logger)
        {
            if (IsAvailable) return;
            if (SynchronizationContext.Current?.GetType().FullName != "UnityEngine.UnitySynchronizationContext")
                throw new InvalidOperationException("Lobby presentation must initialize on the Unity thread.");
            log = logger;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            if (typeof(FRONT_Multiplayer).GetMethod("receivedLobbyChat", BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(string), typeof(int), typeof(bool) }, null)?.ReturnType != typeof(void))
                throw new MissingMethodException(typeof(FRONT_Multiplayer).FullName, "receivedLobbyChat");
            Hook.Install(typeof(FRONT_Multiplayer), "receivedLobbyChat", typeof(void),
                new[] { typeof(string), typeof(string), typeof(int), typeof(bool) }, Present, "LobbyChatPresentation");
            IsAvailable = true;
        }
        private static void Present(FRONT_Multiplayer self, string name, string text, int colour, bool system)
        {
            if (classifying || Thread.CurrentThread.ManagedThreadId != mainThread || Dispatch.Rules.Snapshot.Length == 0)
            { Hook.Original(self, name, text, colour, system); return; }
            classifying = true;
            try { Hook.Original(self, name, text, colour, Dispatch.Classify(name, text, system)); }
            finally { classifying = false; }
        }
        // Renders a local system hint through Vanilla's receiver without network traffic or reclassification.
        // Vanilla's receiver only appends to the lobby chat list, flags the Coop chat and refreshes the view.
        internal static bool TryShowLocal(FRONT_Multiplayer view, string text)
        {
            if (!IsAvailable || ReferenceEquals(view, null) || Thread.CurrentThread.ManagedThreadId != mainThread) return false;
            Hook.Original(view, string.Empty, text, -1, true);
            return true;
        }
        private static void Report(string detail) => NativeApiLog.Error(log, "Lobby system message: " + detail);
    }
    internal sealed class LobbyChatPresentationDispatch
    {
        internal readonly OwnedCallbacks<Func<LobbyChatMessage, bool>> Rules = new OwnedCallbacks<Func<LobbyChatMessage, bool>>();
        private readonly Action<string> report;
        private void Report(string detail) { try { report?.Invoke(detail); } catch { } }
        internal LobbyChatPresentationDispatch(Action<string> report) { this.report = report; }
        internal bool Classify(string name, string text, bool system)
        {
            var message = new LobbyChatMessage(name, text, system);
            foreach (var entry in Rules.Snapshot)
                try { system |= entry.Callback(message); }
                catch (Exception error) { Report(entry.Owner + "/" + entry.Id + ": " + error); }
            return system;
        }
    }
}
