using System;
using System.Reflection;
using System.Collections.Generic;
using BepInEx.Logging;
using APIShared.ScriptExtenderFixes;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;

namespace APIShared.Savegames
{
    internal enum PreCaptureSaveRoute { Unknown, NormalSave, EditorMap, Trail, Synchronized }

    internal readonly struct PreCaptureSaveSupport
    {
        private readonly bool factory, normal, trail, synchronized, extender;

        internal PreCaptureSaveSupport(bool factory, bool normal, bool trail, bool synchronized, bool extender)
        {
            this.factory = factory;
            this.normal = normal;
            this.trail = trail;
            this.synchronized = synchronized;
            this.extender = extender;
        }

        internal bool Supports(VersionedSaveScope scope) => factory &&
            (normal || synchronized || extender || trail && scope == VersionedSaveScope.SaveGamesAndMaps);

        internal string MissingRoutes()
        {
            if (!factory) return "archive factory binding unavailable";
            var missing = new List<string>();
            if (!normal) missing.Add("normal game and editor saves");
            if (!trail) missing.Add("Trail exports");
            if (!synchronized) missing.Add("synchronized saves");
            if (missing.Count == 0) return null;
            return "framework context unavailable for " + string.Join(", ", missing) +
                (extender ? "; optional Script Extender provider is bound and checked per invocation" :
                    "; no optional Script Extender provider is bound");
        }

        internal static void ReportStartup(ManualLogSource log)
        {
            if (!Economy.WorkerProductionCriticalSaveRuntime.IsAvailable)
            {
                NativeApiLog.Warning(log, "ATOMIC_SAVE_SUPPORT: protected writer unavailable for all save routes.");
                return;
            }
            string missing = Economy.WorkerProductionCriticalSaveRuntime.ContextSupport.MissingRoutes();
            if (missing != null) NativeApiLog.Warning(log, "ATOMIC_SAVE_SUPPORT: " + missing + ".");
        }
    }

    // Identifies the metadata producer, not a native save permission or receipt.
    internal enum PreCaptureSaveContextSource
    {
        None = 0,
        ExtenderExport = 1,
        SynchronizedPolicy = 2,
        FrameworkTrail = 3,
        FrameworkNormal = 4,
    }

    // Optional startup binding only. No hook, registry, callback, reflection on a
    // save, loaded-filename fallback or writer attachment is installed here. A
    // caller must still own its demanded PRE-freeze native invocation; a context
    // value by itself cannot prove that capture has not already happened.
    internal sealed class PreCaptureSaveContextReader
    {
        private delegate bool ActiveContextQuery(out SaveContext context);
        private readonly ActiveContextQuery query;
        private const string Missing = "No owned early save context is available and the optional Script Extender provider is unavailable.";
        private const string NoContext = "No usable owned save context is available before archive selection.";
        private const string QueryFailed = "The active-save context query failed; context-dependent capture is unavailable.";

        private PreCaptureSaveContextReader(ActiveContextQuery query) { this.query = query; }
        internal bool HasExtenderExportProvider => query != null;

        // Resolve before touching the singleton. With old SE, no reference to the
        // new member is emitted and no unrelated capability is made unavailable.
        // Call once at the owning optional feature's startup, not from a save.
        internal static PreCaptureSaveContextReader Create(out string reason)
        {
            MethodInfo method;
            if (!TryResolve(typeof(ModSaveDataAPI), out method))
            { reason = Missing; return new PreCaptureSaveContextReader(null); }
            try { return Bind(method, ModSaveDataAPI.Instance, out reason); }
            catch (Exception) { reason = Missing; return new PreCaptureSaveContextReader(null); }
        }

        // The same exact-signature resolver/binder is exercised with controlled
        // provider objects by the existing SaveContextTests fixture. This does
        // not publish a provider replacement or mutate process-global state.
        internal static PreCaptureSaveContextReader CreateForProvider(Type type, object provider, out string reason)
        {
            MethodInfo method;
            if (!TryResolve(type, out method) || provider == null || !type.IsInstanceOfType(provider))
            { reason = Missing; return new PreCaptureSaveContextReader(null); }
            return Bind(method, provider, out reason);
        }

        private static bool TryResolve(Type type, out MethodInfo method)
        {
            method = null;
            if (type == null) return false;
            try
            {
                var contract = new ManagedMemberContract().Method(type, "TryGetActiveSaveContext", typeof(bool),
                    typeof(SaveContext).MakeByRefType());
                if (contract.FindMismatch() != null) return false;
                var candidate = type.GetMethod("TryGetActiveSaveContext", BindingFlags.Public | BindingFlags.Instance |
                    BindingFlags.DeclaredOnly, null, new[] { typeof(SaveContext).MakeByRefType() }, null);
                if (candidate == null || candidate.DeclaringType != type || candidate.ReturnType != typeof(bool) ||
                    candidate.IsStatic || candidate.ContainsGenericParameters) return false;
                var parameters = candidate.GetParameters();
                if (parameters.Length != 1 || !parameters[0].IsOut || parameters[0].IsIn || parameters[0].IsOptional) return false;
                method = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static PreCaptureSaveContextReader Bind(MethodInfo method, object provider, out string reason)
        {
            try
            {
                var read = (ActiveContextQuery)Delegate.CreateDelegate(typeof(ActiveContextQuery), provider, method, true);
                reason = string.Empty;
                return new PreCaptureSaveContextReader(read);
            }
            catch (Exception) { reason = Missing; return new PreCaptureSaveContextReader(null); }
        }

        // Query only behind the real context-dependent demand and native-owner
        // pre-freeze guards. The normal/export provider refuses after its archive
        // factory selects a context. Chore policy is a constant value for its
        // existing validated thread-local scope: a late policy read can neither
        // change transport nor attest an earlier query/capture. No native save
        // authority, operation identity or publication follows from this result.
        internal bool TryReadSnapshot(out VersionedSaveContext context,
            out PreCaptureSaveContextSource source, out string reason)
        {
            context = default;
            source = PreCaptureSaveContextSource.None;
            SaveContext observed;
            try
            {
                if (SaveContextRuntime.TryGetSynchronizedSaveContext(out observed))
                {
                    context = new VersionedSaveContext(observed.IsSaveFile, observed.IsMapEditorSave, observed.FilePath);
                    source = PreCaptureSaveContextSource.SynchronizedPolicy;
                    reason = string.Empty;
                    return true;
                }
                if (SaveContextRuntime.TryGetNormalSaveContext(out observed, out bool normalActive))
                {
                    context = new VersionedSaveContext(observed.IsSaveFile, observed.IsMapEditorSave, observed.FilePath);
                    source = PreCaptureSaveContextSource.FrameworkNormal;
                    reason = string.Empty;
                    return true;
                }
                if (normalActive) { reason = "The current normal/editor save has no usable context before archive selection."; return false; }
                if (SaveContextRuntime.TryGetTrailSaveContext(out observed, out bool trailActive))
                {
                    observed = SaveContextRuntime.NormalizeSnapshot(observed);
                    context = new VersionedSaveContext(observed.IsSaveFile, observed.IsMapEditorSave, observed.FilePath);
                    source = PreCaptureSaveContextSource.FrameworkTrail;
                    reason = string.Empty;
                    return true;
                }
                // An unidentified/nested wrapper must not borrow an outer SE export.
                if (trailActive) { reason = "The current Trail export has no usable source-map context before archive selection."; return false; }
                if (query == null) { reason = Missing; return false; }
                if (!query(out observed) || observed == null) { reason = NoContext; return false; }
                // Share the existing policy; never derive flags/path from a
                // manager getter, extension, map byte or native temporary file.
                observed = SaveContextRuntime.NormalizeSnapshot(observed);
                context = new VersionedSaveContext(observed.IsSaveFile, observed.IsMapEditorSave, observed.FilePath);
                source = PreCaptureSaveContextSource.ExtenderExport;
                reason = string.Empty;
                return true;
            }
            catch (Exception)
            {
                context = default;
                source = PreCaptureSaveContextSource.None;
                reason = QueryFailed;
                return false;
            }
        }
    }
}
