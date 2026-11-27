using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;

namespace APIShared.Wildlife
{
    // Flight gates: two purely native inline stubs where the deer (0x124F20) and goat (0x125330) updates read
    // their sector's threat counters (see HerdNativeAssembly.EmitFlightGate). They read one byte per herd from
    // calmHerds; the byte is set while any registered rule marks the herd calm. No managed transition.
    // Installed on the first rule registration inside NativeInlinePatchWindow, never unpatched.
    internal static unsafe partial class HerdRuntime
    {
        internal const string FlightSiteGroup = "Wildlife.HerdFlightGates";
        internal const int DeerFlightRva = 0x124F20, GoatFlightRva = 0x125330, FlightSpanLength = 18;
        internal const int MaximumFlightRules = 64;
        private const string FlightLabel = "Herd flight gate";

        // mov edi,[rax+rcx+0x28]; add edi,[rax+rcx+0x48]; mov r15d,[rax+rcx+0x40]; mov r12d,[rax+rcx+0x44]
        internal static readonly byte[] FlightBytes = WildlifeInlineSite.Hex("8B7C0828 037C0848 448B7C0840 448B640844");
        // mov word ptr [rbx+0x5F8], r13w
        internal static readonly byte[] FlightContinuation = WildlifeInlineSite.Hex("6644 89ABF8050000");

        private static readonly object FlightGate = new object();
        private static readonly NativeHookPublicationState FlightPublication = new NativeHookPublicationState();
        private static readonly HookHandle<X64InlineHook> DeerHandle = new HookHandle<X64InlineHook>();
        private static readonly HookHandle<X64InlineHook> GoatHandle = new HookHandle<X64InlineHook>();
        // Never freed: published stubs read it until the process ends.
        private static readonly IntPtr calmHerds = Marshal.AllocHGlobal(HerdNativeAssembly.HerdCapacity);
        private static readonly HerdCalmTable calm = new HerdCalmTable((byte*)calmHerds, HerdNativeAssembly.HerdCapacity);
        private static readonly List<HerdFlightRule> flightRules = new List<HerdFlightRule>();
        private static string flightFailure = "";
        private static bool flightAttempted;

        internal static NativeSites.NativeSiteSpan[] FlightSpans() => new[]
        {
            new NativeSites.NativeSiteSpan(DeerFlightRva, FlightSpanLength, NativeSites.NativeSiteMode.Exclusive),
            new NativeSites.NativeSiteSpan(GoatFlightRva, FlightSpanLength, NativeSites.NativeSiteMode.Exclusive)
        };

        internal static bool TryRegisterFlightRule(string owner, string id, out HerdFlightRule rule, out string reason)
        {
            rule = null;
            if (!ValidateRegistration(owner, id, owner, out reason)) return false;
            lock (Gate)
            {
                foreach (var old in flightRules)
                    if (StringComparer.Ordinal.Equals(old.OwnerGuid, owner) && StringComparer.Ordinal.Equals(old.RegistrationId, id))
                    { reason = "This owner already registered that ID."; return false; }
                if (flightRules.Count >= MaximumFlightRules) { reason = "No more herd flight rules can be registered."; return false; }
                if (!TryEnsureFlightGates(out reason)) return false;
                lock (FlightGate)
                {
                    rule = new HerdFlightRule(owner, id, calm.AddRule());
                    flightRules.Add(rule);
                }
            }
            NativeApiLog.Info(log, "Herd flight rule registered: owner=" + owner + ", id=" + id + ".");
            return true;
        }

        // Callers hold Gate. One attempt per process (fail closed).
        private static bool TryEnsureFlightGates(out string reason)
        {
            if (FlightPublication.IsReady) { reason = ""; return true; }
            if (flightAttempted) { reason = flightFailure; return false; }
            if (!NativeInlinePatchWindow.IsOpen) { reason = NativeInlinePatchWindow.ClosedReason; return false; }
            flightAttempted = true;
            var group = new NativeInlineStubGroup(FlightSiteGroup, FlightPublication);
            try
            {
                WildlifeInlineSite.CheckSite(loadContext, image, DeerFlightRva, FlightBytes, FlightContinuation, FlightLabel);
                WildlifeInlineSite.CheckSite(loadContext, image, GoatFlightRva, FlightBytes, FlightContinuation, FlightLabel);
                WildlifeInlineSite.ProbeLength(image, DeerFlightRva, FlightSpanLength, FlightLabel);
                WildlifeInlineSite.ProbeLength(image, GoatFlightRva, FlightSpanLength, FlightLabel);
                group.Claim(FlightSpans());
                group.Begin(loadContext.Region);
                group.Add(DeerHandle, image + DeerFlightRva, (a, original, back) => GenerateFlight(a, original), FlightSpanLength);
                group.Add(GoatHandle, image + GoatFlightRva, (a, original, back) => GenerateFlight(a, original), FlightSpanLength);
                var result = group.Commit("Herd flight gate preparation was incomplete.");
                VerifyFlight(false);
                group.Publish(result, typeof(HerdRuntime), () => VerifyFlight(true));
                NativeApiLog.Info(log, "Herd flight gates published: deer=0x" + DeerFlightRva.ToString("X") + ", goats=0x" + GoatFlightRva.ToString("X") +
                    "; native per-herd calm table.");
                reason = "";
                return true;
            }
            catch (Exception error)
            {
                flightFailure = "Herd flight rules are unavailable: " + error.Message;
                group.RollbackUnpublished(null);
                NativeApiLog.Error(log, flightFailure);
                reason = flightFailure;
                return false;
            }
        }

        private static void GenerateFlight(Assembler a, ReadOnlySpan<Instruction> original) =>
            HerdNativeAssembly.EmitFlightGate(a, original, unchecked((ulong)calmHerds.ToInt64()));

        private static void VerifyFlight(bool installed)
        {
            WildlifeInlineSite.Verify(DeerHandle, image, DeerFlightRva, FlightSpanLength, FlightBytes, GenerateFlight, installed, FlightLabel);
            WildlifeInlineSite.Verify(GoatHandle, image, GoatFlightRva, FlightSpanLength, FlightBytes, GenerateFlight, installed, FlightLabel);
        }

        // Simulation tick only, so that every peer changes the same marks in the same tick.
        internal static bool TrySetCalm(HerdFlightRule rule, int herdId, bool calmHerd)
        {
            if (rule == null || herdId < 1 || herdId > Herds.MaximumHerdId || !Simulation.SimulationMutationScope.IsTickActive) return false;
            lock (FlightGate) return calm.Set(rule.Slot, herdId, calmHerd);
        }

        internal static void ClearCalm(HerdFlightRule rule)
        {
            if (rule == null) return;
            lock (FlightGate) calm.Clear(rule.Slot);
        }

        // Mission initialization and end: herd IDs belong to one map.
        private static void ClearAllCalm()
        {
            lock (FlightGate) calm.ClearAll();
        }
    }
}
