using APIShared.ModSettings;
using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MessagePack;

namespace APIShared.ModSettings
{
    /// <summary>Optional configuration endpoints, independent of their mod's UI registration.</summary>
    /// <remarks>Coordinate configuration changes on the Unity thread. This registry is not a concurrent network transport. Backend and storage errors propagate unless a method explicitly reports them; UI notifications are exception-isolated.</remarks>
    public static class ModSettingsApplication
    {
        private static readonly Dictionary<string, PresetLobbyModSettingsViewModel> endpoints = new Dictionary<string, PresetLobbyModSettingsViewModel>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> activationFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Synchronous notification on the thread changing preparation; no replay or automatic dispatch. Each subscriber failure is isolated so diagnostics cannot interrupt publication.</summary>
        public static event Action PreparationChanged;
        /// <summary>Whether a configuration integration has an unresolved activation failure that blocks launch preparation.</summary>
        public static bool HasActivationFailures => activationFailures.Count != 0;
        internal static void CheckRegistration(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || endpoints.ContainsKey(id))
                throw new InvalidOperationException("Duplicate or invalid configuration endpoint: " + id);
        }
        internal static void SetActivationFailure(string id, string error)
        {
            if (error == null) activationFailures.Remove(id); else activationFailures[id] = error;
            NotifyPreparationChanged();
        }
        private static void NotifyPreparationChanged()
        {
            foreach (Action callback in PreparationChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { callback(); } catch { /* A diagnostic view must not interrupt configuration publication. */ }
        }
        private static void RequireActivatedIntegrations()
        {
            if (HasActivationFailures) throw new InvalidOperationException(string.Join("; ", activationFailures.Select(x => x.Key + ": " + x.Value)) + " Restart the game before starting.");
        }
        private static string journalPath, contextId = "";
        private static PersonalDataFile journalFile;
        private static BepInEx.Logging.ManualLogSource journalLog;
        private static Dictionary<string, Dictionary<string, byte[]>> resume;
        private static string resumeContext;
        private static bool consumed;
        private static Dictionary<string, string> personalFingerprints;
        private static Dictionary<string, Dictionary<string, string>> preparedSources;
        /// <summary>Current temporary mission identity; empty denotes personal configuration.</summary>
        public static string ContextId => contextId;
        /// <summary>Detached dictionary of published participants by target GUID; the settings models themselves are process-lived.</summary>
        public static IReadOnlyDictionary<string, PresetLobbyModSettingsViewModel> Endpoints => new Dictionary<string, PresetLobbyModSettingsViewModel>(endpoints);

        internal static void Register(string id, PresetLobbyModSettingsViewModel endpoint, string assemblyPath)
        {
            if (endpoints.TryGetValue(id, out var previous) && !ReferenceEquals(previous, endpoint))
                throw new InvalidOperationException("Duplicate configuration endpoint: " + id);
            endpoints[id] = endpoint;
#if API_SHARED_PRESET_TESTS
            if (journalPath == null)
                journalPath = Path.Combine(Path.GetDirectoryName(assemblyPath), "RestartPreparation.json");
#endif
            EnsureJournalPath();
        }

        /// <summary>Discovers host-owned settings, including dynamic descriptors adapted for existing schema consumers.</summary>
        public static PropertyInfo[] GetHostProperties(object endpoint)
        {
            if (endpoint is PresetLobbyModSettingsViewModel model && model.System_HasDynamicSettings)
                return model.System_GetPresetSettingDescriptors().Where(x => x.Scope == PresetSettingScope.Host)
                    .Select(x => (PropertyInfo)new DescribedProperty(model, x)).ToArray();
            return endpoint.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.CanRead && x.CanWrite && x.GetCustomAttributes(true).Any(a => a.GetType().Name == "SyncHostOnlyAttribute") &&
                    !x.GetCustomAttributes(true).Any(a => a.GetType().Name == "DoNotPersistAttribute")).ToArray();
        }

        /// <summary>Discovers host settings owned by mission sources. Network host ownership is unchanged.</summary>
        public static PropertyInfo[] GetMissionHostProperties(object endpoint)
        {
            if (endpoint is PresetLobbyModSettingsViewModel model)
            {
                var independent = new HashSet<string>(model.System_GetPresetSettingDescriptors()
                    .Where(item => item.MissionIndependent).Select(item => item.PropertyName), StringComparer.Ordinal);
                return GetHostProperties(endpoint).Where(property => !independent.Contains(property.Name) &&
                    !property.IsDefined(typeof(MissionIndependentAttribute), true)).ToArray();
            }
            return GetHostProperties(endpoint).Where(property =>
                !property.IsDefined(typeof(MissionIndependentAttribute), true)).ToArray();
        }

        /// <summary>Whether an unconsumed durable startup preparation exists; may read and validate its journal.</summary>
        public static bool HasRestartPreparation
        {
            get
            {
                try { return resume != null || ReadJournalBytes() != null; }
                // Noesis binds this getter directly. Unresolved storage remains visible; the
                // description reports its error, and launch/read/discard still enforce safety gates.
                catch (Exception) { return true; }
            }
        }
        // The static coordinator and its callers survive the plugin's startup cleanup.
        internal static void InitializeJournalStorage(BepInEx.Logging.ManualLogSource logger) => journalLog = logger;

        // The normalized configuration root preserves the old journal's installation boundary.
        // Plugin versions and installation subfolders do not enter the identity.
        internal static PersonalDataFile CreateJournalStorage(string configPath)
        {
            string identity = PersonalDataIO.Normalize(configPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('/', '\\').ToUpperInvariant();
            string installationId = Digest(identity).Substring(0, 16).ToLowerInvariant();
            string documents = PersonalDataFile.TestDocumentsDirectory != null
                ? PersonalDataFile.TestDocumentsDirectory() : PersonalDataFile.GetDocuments();
            string target = Path.Combine(documents, "SHCDE", "APIShared", "Inst", installationId, "RestartPreparation.json");
            return new PersonalDataFile(target, Path.Combine(configPath, "APIShared", "RestartPreparation.json"),
                ReportJournalStorage, identity,
                maximumReadBytes: 8L * 1024 * 1024);
        }

        private static void ReportJournalStorage(string message)
        {
            if (message.Contains("failed") || message.Contains("pending") || message.Contains("retained"))
                NativeApiLog.Warning(journalLog, "Restart preparation storage: " + message);
            else NativeApiLog.Info(journalLog, "Restart preparation storage: " + message);
        }

        private static void EnsureJournalPath()
        {
            if (journalFile != null && string.Equals(journalFile.Path, journalPath, StringComparison.Ordinal)) return;
#if !API_SHARED_PRESET_TESTS
            journalFile = CreateJournalStorage(BepInEx.Paths.ConfigPath);
            journalPath = journalFile.Path;
#else
            // Classic fixtures explicitly select their isolated file. Storage/migration tests
            // use CreateJournalStorage with injected Documents and configuration roots.
            journalFile = journalPath == null ? null : new PersonalDataFile(journalPath, null,
                maximumReadBytes: 8L * 1024 * 1024);
#endif
        }

        private static byte[] ReadJournalBytes()
        {
            EnsureJournalPath();
            return journalFile?.ReadAllBytes();
        }

        private static bool IsJournalSizeFailure(Exception error)
        {
            for (; error != null; error = error.InnerException)
                if (error is PersonalDataSizeException) return true;
            return false;
        }

        private static void DeleteJournal(bool explicitDiscard = false)
        {
            EnsureJournalPath();
            try { journalFile?.Delete(); }
            catch (IOException error) when (explicitDiscard && IsJournalSizeFailure(error))
            {
                // This is a user-authorized reset, never an ordinary consume/rollback path.
                // The store revalidates receipts/identity/access and preserves oversized originals.
                journalFile.ResetAllBytes(Array.Empty<byte>());
                journalFile.Delete();
            }
        }
        /// <summary>Returns a user-facing explanation of startup preparation, including damaged journal diagnostics.</summary>
        public static string DescribeRestartPreparation()
        {
            var details = activationFailures.Select(x => x.Key + ": " + x.Value).ToList();
            try
            {
                ReadJournal();
                if (resume != null)
                {
                    details.Add(resumeContext);
                    foreach (string id in personalFingerprints.Keys)
                        details.Add(id + (endpoints.ContainsKey(id) ? "" : ": configuration provider unavailable"));
                }
            }
            catch (Exception ex) { details.Add(ex.GetBaseException().Message); }
            return string.Join("\n", details);
        }
        /// <summary>Whether any published participant supplies an application backend.</summary>
        public static bool HasApplicationEndpoints => endpoints.Values.Any(x => x.System_HasApplicationBackend);

        // A context is selected before any mission values are materialized. Its identity includes
        // the source content hash; stale preparations are never silently applied to new content.
        /// <summary>Selects a temporary mission identity and resumes compatible journal evidence. Rejects empty identities and conflicting preparations.</summary>
        public static void EnterContext(string identity)
        {
            RequireActivatedIntegrations();
            if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("Missing settings context identity.");
            ReadJournal();
            if (resume != null && !consumed && resumeContext != identity &&
                resumeContext.Substring(0, resumeContext.LastIndexOf('|') + 1) == identity.Substring(0, identity.LastIndexOf('|') + 1))
                throw new InvalidDataException("Mission settings changed after restart preparation. Discard the preparation before retrying.");
            if (resume != null && !consumed && resumeContext == identity)
                foreach (var entry in personalFingerprints)
                {
                    if (!endpoints.TryGetValue(entry.Key, out var endpoint))
                        throw new InvalidDataException("Required settings provider is missing: " + entry.Key);
                    if (endpoint.System_OwnConfigurationFingerprint() != entry.Value)
                        throw new InvalidDataException("Personal settings changed after restart preparation: " + entry.Key);
                }
            contextId = identity;
        }

        /// <summary>Returns a cloned preparation snapshot for this target, or null if consumed, missing or outside the current context.</summary>
        public static Dictionary<string, byte[]> ResumeSnapshot(string id)
        {
            if (resume == null || consumed || resumeContext != contextId) return null;
            if (!resume.TryGetValue(id, out var snapshot)) return null;
            return Clone(snapshot);
        }

        /// <summary>Restores saved source-selection metadata for the target in an unconsumed matching preparation.</summary>
        public static void RestorePreparedSources(string id)
        {
            if (resume == null || consumed || resumeContext != contextId || preparedSources == null) return;
            if (endpoints.TryGetValue(id, out var endpoint) && preparedSources.TryGetValue(id, out var sources))
                endpoint.RestoreRestartSources(sources);
        }

        /// <summary>Applies one participant and durably stages restart intent first when needed. Returns whether restart is required; backend errors propagate and restore prior preparation evidence.</summary>
        public static bool Commit(string id)
        {
            if (!endpoints.TryGetValue(id, out var endpoint)) return false;
            endpoint.PersistPersonalConfiguration();
            byte[] previous = ReadJournalBytes();
            var previousSources = preparedSources; var previousResume = resume; var previousFingerprints = personalFingerprints; string previousContext = resumeContext;
            bool written = contextId.Length != 0 && endpoint.System_ConfigurationNeedsRestart();
            if (written) SavePreparation(); // Durable intent precedes package publication.
            try { bool result = endpoint.System_ApplyConfiguration(contextId, true); NotifyPreparationChanged(); return result; }
            catch
            {
                if (written)
                {
                    if (previous == null) DeleteJournal();
                    else PublishJournal(previous);
                    preparedSources = previousSources; resume = previousResume; personalFingerprints = previousFingerprints; resumeContext = previousContext;
                }
                throw;
            }
        }

        /// <summary>Returns false until all required startup configurations are actually loaded.</summary>
        public static bool PrepareLaunch()
        {
            RequireActivatedIntegrations();
            foreach (var endpoint in endpoints.Values.ToArray()) endpoint.PersistPersonalConfiguration();
            ReadJournal();
            if (resume != null && !consumed && resumeContext == contextId)
                foreach (string id in resume.Keys)
                    if (!endpoints.ContainsKey(id)) throw new InvalidDataException("Required settings provider is missing: " + id);
            byte[] previous = ReadJournalBytes();
            var previousSources = preparedSources; var previousResume = resume;
            var previousPersonal = personalFingerprints;
            string previousContext = resumeContext;
            bool written = contextId.Length != 0 && endpoints.Values.Any(x => x.System_ConfigurationNeedsRestart());
            if (written) SavePreparation();
            try
            {
                bool ready = true;
                foreach (var item in endpoints)
                {
                    if (!item.Value.System_HasApplicationBackend) continue;
                    bool restart = item.Value.System_ApplyConfiguration(contextId);
                    item.Value.ReportConfigurationResult(restart);
                    if (restart) ready = false;
                }
                NotifyPreparationChanged();
                return ready;
            }
            catch
            {
                if (written)
                {
                    if (previous == null) DeleteJournal(); else PublishJournal(previous);
                    preparedSources = previousSources; resume = previousResume; personalFingerprints = previousPersonal; resumeContext = previousContext;
                }
                throw;
            }
        }

        /// <summary>Consumes the matching preparation after mission start, removing the journal while retaining active context for in-session restart.</summary>
        public static void ConfirmStarted()
        {
            if (resume != null && resumeContext != contextId) return;
            // Keep active settings and the identity available for an in-session mission restart.
            consumed = true;
            DeleteJournal();
            resume = null;
            NotifyPreparationChanged();
        }

        /// <summary>Leaves temporary context; restores personal configuration unless an unconsumed restart preparation must be preserved.</summary>
        public static void ExitContext()
        {
            bool preparingRestart = !consumed && resume != null && resumeContext == contextId;
            contextId = "";
            if (!preparingRestart)
                foreach (var endpoint in endpoints.Values)
                    endpoint.System_ReturnToOwnConfiguration();
            // An unconsumed preparation survives a process exit or menu re-entry.
            if (consumed) { resume = null; resumeContext = null; }
            consumed = false;
        }

        /// <summary>Explicitly discards startup preparation and participant packages; use DiscardPreparationWithReport to inspect unavailable providers.</summary>
        public static void DiscardPreparation()
        {
            DiscardPreparationWithReport();
        }

        /// <summary>Returns providers whose external packages could not be inspected or discarded.</summary>
        public static string[] DiscardPreparationWithReport()
        {
            EnsureJournalPath();
            var unavailable = new List<string>();
            try { ReadJournal(); }
            catch { unavailable.Add("Unknown providers (damaged preparation)"); }
            if (personalFingerprints != null)
                unavailable.AddRange(personalFingerprints.Keys.Where(id => !endpoints.ContainsKey(id)));
            foreach (var endpoint in endpoints.Values) endpoint.DiscardApplicationPackage();
            DeleteJournal(explicitDiscard: true);
            resume = null; resumeContext = null; consumed = false;
            personalFingerprints = null; preparedSources = null;
            foreach (var endpoint in endpoints.Values) endpoint.RefreshConfigurationBindings();
            NotifyPreparationChanged();
            return unavailable.ToArray();
        }

#if API_SHARED_PRESET_TESTS
        internal static void ResetForTests(string path)
        {
            endpoints.Clear(); journalPath = path; journalFile = null; resume = null; resumeContext = null;
            activationFailures.Clear(); personalFingerprints = null; preparedSources = null;
            contextId = ""; consumed = false;
        }

        internal static string ResetJournalStorageForTests(string configPath)
        {
            ResetForTests(null);
            journalFile = CreateJournalStorage(configPath);
            return journalPath = journalFile.Path;
        }
#endif

        private static Dictionary<string, byte[]> Clone(Dictionary<string, byte[]> source) =>
            source.ToDictionary(x => x.Key, x => (byte[])x.Value.Clone(), StringComparer.Ordinal);

        private static void SavePreparation()
        {
            ReadJournal();
            if (resume != null && !consumed && resumeContext != contextId)
                throw new InvalidOperationException("Another mission has a restart preparation. Discard it explicitly before replacing it.");
            if (journalPath == null) throw new InvalidOperationException("Restart storage is not initialized.");
            var fingerprints = endpoints.Where(x => x.Value.System_HasApplicationBackend)
                .ToDictionary(x => x.Key, x => x.Value.System_OwnConfigurationFingerprint(), StringComparer.Ordinal);
            var sources = endpoints.ToDictionary(x => x.Key, x => x.Value.CaptureRestartSources(), StringComparer.Ordinal);
            var mods = new Dictionary<string, object>(StringComparer.Ordinal);
            var snapshots = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
            foreach (var item in endpoints)
            {
                var snapshot = item.Value.CaptureApplicationSnapshot();
                if (snapshot.Count > 16384) throw new InvalidDataException("Too many settings.");
                snapshots[item.Key] = snapshot;
                mods[item.Key] = snapshot.ToDictionary(x => x.Key, x => (object)Convert.ToBase64String(x.Value), StringComparer.Ordinal);
            }
            string payload = DependencyFreeJson.Serialize(new Dictionary<string, object> { ["schema"] = 1, ["context"] = contextId, ["mods"] = mods, ["sources"] = sources.ToDictionary(x => x.Key, x => (object)x.Value), ["personal"] = fingerprints.ToDictionary(x => x.Key, x => (object)x.Value) });
            string json = DependencyFreeJson.Serialize(new Dictionary<string, object> { ["payload"] = payload, ["sha256"] = Digest(payload) });
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Restart preparation exceeds 8 MiB.");
            PublishJournal(bytes);
            preparedSources = sources; resume = snapshots; personalFingerprints = fingerprints; resumeContext = contextId; consumed = false;
        }

        // A legitimate personal edit must not invalidate an already prepared mission restart.
        // Validate before persistence; update only its ownership fingerprint afterwards, retaining
        // the prepared host snapshot and source metadata rather than capturing a newer draft.
        internal static void ValidatePersonalPersistence(string id)
        {
            ReadJournal();
            if (resume == null || consumed || personalFingerprints == null || !personalFingerprints.TryGetValue(id, out var expected)) return;
            // Personal values are not mission-supplied: only an unexplained change of the
            // own configuration is a conflict, independently of the currently entered context.
            if (!endpoints.TryGetValue(id, out var endpoint) || endpoint.System_OwnConfigurationFingerprint() != expected)
                throw new InvalidDataException("Personal settings changed after restart preparation: " + id);
        }

        internal static void PersonalValuesPersisted(string id)
        {
            if (resume == null || consumed || personalFingerprints == null || !personalFingerprints.ContainsKey(id)) return;
            var fingerprints = new Dictionary<string, string>(personalFingerprints, StringComparer.Ordinal);
            fingerprints[id] = endpoints[id].System_OwnConfigurationFingerprint();
            string payload = DependencyFreeJson.Serialize(new Dictionary<string, object> {
                ["schema"] = 1, ["context"] = resumeContext,
                ["mods"] = resume.ToDictionary(item => item.Key, item => (object)item.Value.ToDictionary(
                    value => value.Key, value => (object)Convert.ToBase64String(value.Value), StringComparer.Ordinal), StringComparer.Ordinal),
                ["sources"] = preparedSources.ToDictionary(item => item.Key, item => (object)item.Value),
                ["personal"] = fingerprints.ToDictionary(item => item.Key, item => (object)item.Value)
            });
            byte[] bytes = Encoding.UTF8.GetBytes(DependencyFreeJson.Serialize(new Dictionary<string, object> { ["payload"] = payload, ["sha256"] = Digest(payload) }));
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Restart preparation exceeds 8 MiB.");
            PublishJournal(bytes);
            personalFingerprints = fingerprints;
        }

        private static string Digest(string text)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        private static void PublishJournal(byte[] bytes)
        {
            EnsureJournalPath();
            if (journalFile == null) throw new InvalidOperationException("Restart storage is not initialized.");
            journalFile.WriteAllBytes(bytes);
        }

        private static void ReadJournal()
        {
            if (resume != null) return;
            byte[] bytes = ReadJournalBytes();
            if (bytes == null) return;
            using (var stream = new MemoryStream(bytes, false))
            {
                if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Restart preparation exceeds 8 MiB.");
                using (var reader = new StreamReader(stream))
                {
                    var envelope = DependencyFreeJson.Parse(reader.ReadToEnd()) as Dictionary<string, object>;
                    if (envelope == null || !envelope.TryGetValue("payload", out var payload) || !(payload is string content) ||
                        !envelope.TryGetValue("sha256", out var hash) || !(hash is string checksum) || Digest(content) != checksum)
                        throw new InvalidDataException("Restart preparation failed its integrity check.");
                    var root = DependencyFreeJson.Parse(content) as Dictionary<string, object>;
                    if (root == null || !root.TryGetValue("schema", out var schema) || Convert.ToInt32(schema) != 1 ||
                        !root.TryGetValue("context", out var context) || !(context is string identity) || string.IsNullOrWhiteSpace(identity) ||
                        !root.TryGetValue("mods", out var rawMods) || !(rawMods is Dictionary<string, object> mods))
                        throw new InvalidDataException("Invalid restart preparation.");
                    var parsed = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
                    foreach (var mod in mods)
                    {
                        if (!(mod.Value is Dictionary<string, object> values) || values.Count > 16384)
                            throw new InvalidDataException("Invalid restart snapshot.");
                        parsed[mod.Key] = values.ToDictionary(x => x.Key, x => Convert.FromBase64String((string)x.Value), StringComparer.Ordinal);
                    }
                    if (!root.TryGetValue("personal", out var personal) || !(personal is Dictionary<string, object> personalMap))
                        throw new InvalidDataException("Restart preparation has no personal configuration provenance.");
                    var fingerprints = personalMap.ToDictionary(x => x.Key, x => (string)x.Value, StringComparer.Ordinal);
                    if (!root.TryGetValue("sources", out var rawSources) || !(rawSources is Dictionary<string, object> sourceMap))
                        throw new InvalidDataException("Restart preparation has no source selection.");
                    var sources = sourceMap.ToDictionary(x => x.Key,
                        x => ((Dictionary<string, object>)x.Value).ToDictionary(v => v.Key, v => (string)v.Value), StringComparer.Ordinal);
                    personalFingerprints = fingerprints; preparedSources = sources;
                    resume = parsed; resumeContext = identity;
                }
            }
        }

        // Adapter for existing schema consumers. Reads are sourced from the provider's snapshot;
        // mutation is intentionally only possible through the complete endpoint snapshot API.
        private sealed class DescribedProperty : PropertyInfo
        {
            private readonly PresetLobbyModSettingsViewModel owner;
            private readonly PresetSettingDescriptor descriptor;
            internal DescribedProperty(PresetLobbyModSettingsViewModel owner, PresetSettingDescriptor descriptor) { this.owner = owner; this.descriptor = descriptor; }
            public override string Name => descriptor.PropertyName;
            public override Type PropertyType => descriptor.PropertyType;
            public override Type DeclaringType => owner.GetType();
            public override Type ReflectedType => owner.GetType();
            public override PropertyAttributes Attributes => PropertyAttributes.None;
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override object GetValue(object obj, BindingFlags invokeAttr, Binder binder, object[] index, CultureInfo culture) =>
                owner.System_ReadDescribedValue(Name);
            public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, object[] index, CultureInfo culture) => throw new InvalidOperationException("Apply a complete configuration snapshot.");
            public override MethodInfo[] GetAccessors(bool nonPublic) => Array.Empty<MethodInfo>();
            public override MethodInfo GetGetMethod(bool nonPublic) => null;
            public override MethodInfo GetSetMethod(bool nonPublic) => null;
            public override ParameterInfo[] GetIndexParameters() => Array.Empty<ParameterInfo>();
            public override object[] GetCustomAttributes(bool inherit) => Array.Empty<object>();
            public override object[] GetCustomAttributes(Type attributeType, bool inherit) => Array.Empty<object>();
            public override bool IsDefined(Type attributeType, bool inherit) => (attributeType == typeof(RequiresRestartAttribute) && descriptor.RequiresRestart) ||
                (attributeType == typeof(MissionIndependentAttribute) && descriptor.MissionIndependent);
        }
    }
}
