using System;
using System.Threading;
using System.Reflection;
using APIShared.Events;
using BepInEx.Logging;

namespace APIShared.Maps
{
    /// <summary>Outcome of an exact-name/CRC multiplayer map lookup.</summary>
    public enum MultiplayerMapLookupStatus
    {
        /// <summary>No provider claimed the query; Header is the unchanged Vanilla result.</summary>
        Vanilla,
        /// <summary>Exactly one provider supplied a matching header.</summary>
        Resolved,
        /// <summary>Different providers claimed the query; no header is returned.</summary>
        Conflict
    }

    /// <summary>Immutable query. CRC uses Vanilla's signed transport representation.</summary>
    public sealed class MultiplayerMapLookupRequest
    {
        /// <summary>Requested Vanilla map filename, without extension.</summary>
        public string FileName { get; }
        /// <summary>Requested CRC, with unchanged Vanilla comparison semantics.</summary>
        public int Crc { get; }
        internal MultiplayerMapLookupRequest(string name, int crc) { FileName = name; Crc = crc; }
    }

    /// <summary>Read-only lookup outcome. Treat the game-owned Header as borrowed and do not mutate it.</summary>
    public sealed class MultiplayerMapLookupResult
    {
        /// <summary>Original query.</summary>
        public MultiplayerMapLookupRequest Request { get; }
        /// <summary>Resolution status; a Vanilla result can also have a null Header.</summary>
        public MultiplayerMapLookupStatus Status { get; }
        /// <summary>Borrowed resolved header, or null.</summary>
        public FileHeader Header { get; }
        /// <summary>Technical resolution detail; never a network authorization decision.</summary>
        public string Diagnostic { get; }
        internal MultiplayerMapLookupResult(MultiplayerMapLookupRequest request, MultiplayerMapLookupStatus status,
            FileHeader header, string diagnostic) { Request = request; Status = status; Header = header; Diagnostic = diagnostic; }
    }

    /// <summary>Process-wide exact-name/CRC map lookup broker, independent of any content mod.</summary>
    /// <remarks>Registrations are permanent and ordered by owner/ID ordinal. Register on the Unity thread;
    /// registration before initialization is allowed. Providers return null while inactive and retain package,
    /// lobby and content authority. Callbacks execute synchronously on the validated Unity thread, outside
    /// locks. Other threads and recursive queries use Vanilla. Callback exceptions are isolated and logged;
    /// original exceptions propagate. Different claimed headers fail closed. No global catalog is changed.</remarks>
    public static class MultiplayerMapLookup
    {
        private delegate FileHeader Lookup(MapFileManager self, string name, int crc);
        private static readonly ManagedInterceptionHook<Lookup> Hook = new ManagedInterceptionHook<Lookup>();
        private static readonly MultiplayerMapLookupDispatch Dispatch = new MultiplayerMapLookupDispatch(Report);
        private static ManualLogSource log;
        private static int mainThread;
        [ThreadStatic] private static bool resolving;
        /// <summary>Whether the permanent managed hook initialized successfully after LibraryLoaded.</summary>
        public static bool IsAvailable { get; private set; }
        /// <summary>Adds a permanent provider. False reports invalid input or a duplicate owner/ID.</summary>
        public static bool TryRegisterProvider(string ownerGuid, string registrationId,
            Func<MultiplayerMapLookupRequest, FileHeader> provider, out string diagnostic) =>
            Dispatch.Providers.TryAdd(ownerGuid, registrationId, provider, out diagnostic);
        /// <summary>Adds a permanent observer for completed broker queries, without replay.</summary>
        public static bool TryRegisterObserver(string ownerGuid, string registrationId,
            Action<MultiplayerMapLookupResult> observer, out string diagnostic) =>
            Dispatch.Observers.TryAdd(ownerGuid, registrationId, observer, out diagnostic);

        internal static void Initialize(ManualLogSource logger)
        {
            if (IsAvailable) return;
            if (SynchronizationContext.Current?.GetType().FullName != "UnityEngine.UnitySynchronizationContext")
                throw new InvalidOperationException("Map lookup must initialize on the Unity thread.");
            log = logger;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            foreach (var field in new[] { new { Name = "fileName", Type = typeof(string) },
                new { Name = "filePath", Type = typeof(string) }, new { Name = "crc", Type = typeof(uint) } })
                if (typeof(FileHeader).GetField(field.Name, BindingFlags.Public | BindingFlags.Instance)?.FieldType != field.Type)
                    throw new MissingFieldException(typeof(FileHeader).FullName, field.Name);
            if (typeof(MapFileManager).GetMethod("GetHeaderFromFileNameMP", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(int) }, null)?.ReturnType != typeof(FileHeader))
                throw new MissingMethodException(typeof(MapFileManager).FullName, "GetHeaderFromFileNameMP(string,int)");
            Hook.Install(typeof(MapFileManager), "GetHeaderFromFileNameMP", typeof(FileHeader),
                new[] { typeof(string), typeof(int) }, Resolve, "MultiplayerMapLookup");
            IsAvailable = true;
        }
        private static FileHeader Resolve(MapFileManager self, string name, int crc)
        {
            if (resolving || Thread.CurrentThread.ManagedThreadId != mainThread || !Dispatch.HasDemand)
                return Hook.Original(self, name, crc);
            return ResolveWithProviders(self, name, crc);
        }
        private static FileHeader ResolveWithProviders(MapFileManager self, string name, int crc)
        {
            resolving = true;
            try { return Dispatch.Resolve(name, crc, () => Hook.Original(self, name, crc)).Header; }
            finally { resolving = false; }
        }
        private static void Report(string detail) => NativeApiLog.Error(log, "Multiplayer map lookup: " + detail);
    }

    internal sealed class MultiplayerMapLookupDispatch
    {
        internal readonly OwnedCallbacks<Func<MultiplayerMapLookupRequest, FileHeader>> Providers =
            new OwnedCallbacks<Func<MultiplayerMapLookupRequest, FileHeader>>();
        internal readonly OwnedCallbacks<Action<MultiplayerMapLookupResult>> Observers =
            new OwnedCallbacks<Action<MultiplayerMapLookupResult>>();
        private readonly Action<string> report;
        private void Report(string detail) { try { report?.Invoke(detail); } catch { } }
        internal MultiplayerMapLookupDispatch(Action<string> report) { this.report = report; }
        internal bool HasDemand => Providers.Snapshot.Length != 0 || Observers.Snapshot.Length != 0;
        internal MultiplayerMapLookupResult Resolve(string name, int crc, Func<FileHeader> original)
        {
            var request = new MultiplayerMapLookupRequest(name, crc);
            var providers = Providers.Snapshot;
            var observers = Observers.Snapshot;
            FileHeader header = null;
            string owner = string.Empty, conflict = string.Empty;
            foreach (var entry in providers)
            {
                try
                {
                    FileHeader proposal = entry.Callback(request);
                    if (proposal == null) continue;
                    if (!string.Equals(proposal.fileName, name, StringComparison.OrdinalIgnoreCase) ||
                        proposal.crc != crc || string.IsNullOrWhiteSpace(proposal.filePath))
                        throw new InvalidOperationException("Provider header does not match name/CRC or has no local path.");
                    if (header != null && !ReferenceEquals(header, proposal))
                        conflict = (conflict.Length == 0 ? owner : conflict) + ", " + entry.Owner + "/" + entry.Id;
                    else { header = proposal; owner = entry.Owner + "/" + entry.Id; }
                }
                catch (Exception error) { Report(entry.Owner + "/" + entry.Id + ": " + error); }
            }
            var status = conflict.Length != 0 ? MultiplayerMapLookupStatus.Conflict :
                header != null ? MultiplayerMapLookupStatus.Resolved : MultiplayerMapLookupStatus.Vanilla;
            if (status == MultiplayerMapLookupStatus.Conflict) { header = null; Report("Conflicting providers: " + conflict); }
            else if (status == MultiplayerMapLookupStatus.Vanilla) header = original();
            var result = new MultiplayerMapLookupResult(request, status, header, conflict.Length == 0 ? owner : conflict);
            foreach (var entry in observers)
                try { entry.Callback(result); }
                catch (Exception error) { Report(entry.Owner + "/" + entry.Id + ": " + error); }
            return result;
        }
    }
}
