using System;
using System.Reflection;
using System.Threading;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using SHCDESE.Interop;
using APIShared.Events;

namespace APIShared.Recruitment
{
    /// <summary>Shared European recruitment material-display policy. Native costs, affordability and recruitment remain the caller's responsibility.</summary>
    public static class RecruitmentMaterialUi
    {
        private static readonly object sync = new object();
        private static Registration[] registrations = Array.Empty<Registration>();
        private static Patching.SafeIlHook hook;
        private static long failures;
        [ThreadStatic] private static bool notifying;
        /// <summary>Registers a process-lifetime predicate on the Unity startup thread. It is queried synchronously on the GUI-check caller thread for each affected European type. A true contribution bypasses only Vanilla's fixed weapon-stock presentation; all other UI gates survive. Contributions combine by OR and are ordered by ordinal owner/ID. Exceptions mean false, are isolated and counted. Do not mutate UI/world state or issue commands from this predicate. No replay, automatic dispatch or hook teardown. Duplicate identities and changed installed IL return false with a reason.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<eChimps, bool> bypassFixedWeaponStocks, out string reason)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || bypassFixedWeaponStocks == null)
            { reason = "Owner, ID and predicate are required."; return false; }
            lock (sync)
            {
                if (OrderedRegistrations.Contains(registrations, Registration.Key, ownerGuid, registrationId))
                { reason = "This owner already registered that ID."; return false; }
                if (hook == null)
                {
                    try
                    {
                        MethodInfo method = typeof(FatControler).GetMethod("NoesisGUIUpdateChecksInGame",
                            BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                        if (method == null || method.ReturnType != typeof(void)) throw new MissingMethodException("FatControler.NoesisGUIUpdateChecksInGame");
                        RecruitmentMaterialUiIlContract.Validate(method);
                        if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.Recruitment.MaterialUi", method,
                            context => RecruitmentMaterialUiIlContract.Apply(context, ShouldBypass), null, out var installed, out string failure))
                            throw new InvalidOperationException(failure);
                        hook = installed;
                    }
                    catch (Exception ex)
                    {
                        reason = "Recruitment material UI unavailable: " + ex.Message;
                        return false;
                    }
                }
                Volatile.Write(ref registrations, OrderedRegistrations.WithAdded(
                    registrations, new Registration(ownerGuid, registrationId, bypassFixedWeaponStocks), Registration.Key));
            }
            reason = string.Empty; return true;
        }
        /// <summary>Total isolated predicate errors.</summary>
        public static long CallbackFailures => Interlocked.Read(ref failures);
        internal static bool ShouldBypass(eChimps unitType)
        {
            if (notifying) return false;
            notifying = true;
            bool result = false;
            try
            {
                foreach (var entry in Volatile.Read(ref registrations))
                    try { result |= entry.Predicate(unitType); }
                    catch (Exception ex)
                    {
                        if (FailureThrottle.ShouldReport(ref failures))
                            InterceptionDiagnostics.Report(entry.Owner + "/" + entry.Id + " material UI", ex);
                    }
                return result;
            }
            finally { notifying = false; }
        }
        private sealed class Registration
        {
            internal readonly string Owner, Id;
            internal readonly Func<eChimps, bool> Predicate;
            internal Registration(string owner, string id, Func<eChimps, bool> predicate)
            { Owner = owner; Id = id; Predicate = predicate; }
            internal static readonly Func<Registration, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id);
        }
    }
}
