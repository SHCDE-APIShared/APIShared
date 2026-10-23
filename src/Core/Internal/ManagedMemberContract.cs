using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace APIShared
{
    // Startup check of the Script Extender and game members a capability's own code binds (issue #7), in
    // the per-feature style of SoundRuntime.ValidateMembers and BuildingTooltipPresentation.Field. The
    // runtime binds a compiled member reference only when that code first runs, so without this check a
    // renamed or retyped member fails on every call while the capability still reports Available.
    // Each capability lists exactly the members its code references; ManagedMemberContractTests compare
    // each list with the compiled references. The check runs once, in TryCreate; nothing runs per call.
    internal sealed class ManagedMemberContract
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly List<Entry> entries = new List<Entry>();

        internal ManagedMemberContract Field(Type declaringType, string name, Type fieldType)
        {
            entries.Add(new Entry(declaringType, name, fieldType, null));
            return this;
        }

        // ".ctor" names a constructor; returnType is then typeof(void).
        internal ManagedMemberContract Method(Type declaringType, string name, Type returnType, params Type[] parameters)
        {
            entries.Add(new Entry(declaringType, name, returnType, parameters));
            return this;
        }

        // Resolves every entry by exact name and signature. Returns null when all resolve, otherwise a
        // description of the first member that is missing or has another type or signature.
        internal string FindMismatch(List<MemberInfo> resolved = null)
        {
            foreach (Entry entry in entries)
            {
                MemberInfo member = entry.Resolve();
                if (member == null) return entry.ToString();
                resolved?.Add(member);
            }
            return null;
        }

        private sealed class Entry
        {
            private readonly Type declaringType, type;
            private readonly string name;
            private readonly Type[] parameters;

            internal Entry(Type declaringType, string name, Type type, Type[] parameters)
            {
                this.declaringType = declaringType;
                this.name = name;
                this.type = type;
                this.parameters = parameters;
            }

            internal MemberInfo Resolve()
            {
                if (parameters == null)
                {
                    FieldInfo field = declaringType.GetField(name, Any);
                    return field != null && field.FieldType == type ? field : null;
                }
                if (name == ".ctor")
                    return type == typeof(void) ? declaringType.GetConstructor(Any & ~BindingFlags.Static, null, parameters, null) : null;
                MethodInfo method = declaringType.GetMethod(name, Any, null, parameters, null);
                return method != null && method.ReturnType == type ? method : null;
            }

            public override string ToString() => parameters == null
                ? "field " + type + " " + declaringType + "." + name
                : "method " + type + " " + declaringType + "." + name + "(" + string.Join(", ", parameters.Select(p => p.ToString()).ToArray()) + ")";
        }
    }

    // Capability ID to the member list its TryCreate checks. Tests replace an entry to simulate a member
    // changed by a Script Extender or game update and restore it afterwards.
    internal static class ManagedMemberContracts
    {
        internal static readonly Dictionary<string, Func<ManagedMemberContract>> ByCapability =
            new Dictionary<string, Func<ManagedMemberContract>>(StringComparer.Ordinal)
            {
                [NativeCapabilityIds.MissionLifecycle] = MissionLifecycleService.MemberContract,
                [NativeCapabilityIds.LobbyState] = LobbyStateService.MemberContract,
                [NativeCapabilityIds.PlayerDefeat] = PlayerDefeatService.MemberContract,
                [NativeCapabilityIds.BriefingGoldPresentation] = BriefingGoldPresentationService.MemberContract,
                [NativeCapabilityIds.HudExtrasButtons] = HudExtrasButtonsService.MemberContract,
                [NativeCapabilityIds.BuildingRepair] = BuildingRepairService.MemberContract,
                [NativeCapabilityIds.UnitHudPresentation] = UnitHudPresentationService.MemberContract,
                [NativeCapabilityIds.CoopAiStartImport] = CoopAiStartImportService.MemberContract,
            };

        // Called once at the start of the capability's creation. On a mismatch only this capability reports
        // ValidationFailed with the member, and one error is logged; the capability is not created, so no
        // per-call code runs and nothing repeats. Other capabilities are checked independently.
        internal static bool TryValidate(string capabilityId, ManualLogSource log, out NativeCapabilityDiagnostic diagnostic)
        {
            string mismatch;
            try { mismatch = ByCapability[capabilityId]().FindMismatch(); }
            // A type named by the list no longer loads, so the list itself cannot be built.
            catch (Exception error) { mismatch = error.GetType().Name + ": " + error.Message; }
            if (mismatch == null)
            {
                diagnostic = null;
                return true;
            }
            diagnostic = new NativeCapabilityDiagnostic(capabilityId, NativeCapabilityState.ValidationFailed, string.Empty,
                "A Script Extender or game member this capability uses is missing or changed: " + mismatch + ".");
            try { NativeApiLog.Error(log, "capability=" + capabilityId + ", status=unavailable, reason=" + diagnostic.Reason); }
            catch { }
            return false;
        }
    }
}
