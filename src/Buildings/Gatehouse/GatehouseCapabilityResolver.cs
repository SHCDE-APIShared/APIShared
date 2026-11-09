using BepInEx.Logging;
using RedBird.Core.Memory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared
{
    internal static class GatehouseCapabilityResolver
    {
        public static void Resolve(
            string binaryHash,
            long moduleBase,
            ReadOnlySpan<byte> memory,
            INativeMemory nativeMemory,
            NativeOwnershipRegistry ownership,
            object mutationSync,
            ManualLogSource log,
            GatehouseBuildTarget target,
            ScanRegion nativeRegion,
            out GatehouseDistanceOriginService distanceOriginService,
            out NativeCapabilityDiagnostic distanceOriginDiagnostic,
            out GatehouseTimingService timingService,
            out NativeCapabilityDiagnostic timingDiagnostic)
        {
            distanceOriginService = null;
            timingService = null;
            NativeSection functionSection;
            GatehousePermanentRuntimeState runtimeState;
            NativeResolutionException distanceLiveFailure = null;
            NativeResolutionException timingLiveFailure = null;

            if (!string.Equals(binaryHash, target.BuildHash, StringComparison.OrdinalIgnoreCase))
            {
                const string reason = "The installed CrusaderDE.dll hash is not present in the compiled target catalog.";
                distanceOriginDiagnostic = Diagnostic(
                    NativeCapabilityIds.GatehouseDistanceOrigin,
                    NativeCapabilityState.UnsupportedBuild,
                    binaryHash,
                    reason);
                timingDiagnostic = Diagnostic(
                    NativeCapabilityIds.GatehouseTiming,
                    NativeCapabilityState.UnsupportedBuild,
                    binaryHash,
                    reason);
                return;
            }

            try
            {
                if (moduleBase == 0 || memory.Length == 0)
                    throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The Crusader native module is unavailable.");
                if (nativeMemory == null)
                    throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The native memory adapter is unavailable.");
                if (ownership == null)
                    throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The native ownership registry is unavailable.");
                if (mutationSync == null)
                    throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The gatehouse mutation coordinator is unavailable.");

                NativePeImage pe = NativePeImage.Parse(memory);
                functionSection = pe.RequireExecutableRange(target.FunctionRva, target.FunctionSize, "gatehouse handler function");

                string actualFunctionHash = ApiSharedRuntime.ComputeSha256(memory.Slice(target.FunctionRva, target.FunctionSize));
                if (!string.Equals(actualFunctionHash, target.FunctionHash, StringComparison.OrdinalIgnoreCase))
                    throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, $"The gatehouse handler function hash changed: expected={target.FunctionHash}, actual={actualFunctionHash}.");

                try { ValidateLiveDistance(nativeMemory, moduleBase, target); }
                catch (NativeResolutionException ex) { distanceLiveFailure = ex; }
                try { ValidateLiveTiming(nativeMemory, moduleBase, target); }
                catch (NativeResolutionException ex) { timingLiveFailure = ex; }

                bool installDistance = distanceLiveFailure == null;
                bool installTiming = timingLiveFailure == null;
                GatehouseAutomationNativeState automation = nativeRegion != null && installTiming
                    ? GatehouseAutomationNativeState.Prepare(unchecked((ulong)moduleBase),
                        nativeRegion, memory, nativeMemory, log, ownership)
                    : null;
                if (nativeRegion != null && (installDistance || installTiming))
                {
                    // Claimed before the permanent transaction; released only if it was not published.
                    ApiSharedNativeSites.Claim(ownership, ApiSharedNativeSites.GatehousePermanent,
                        ApiSharedNativeSites.GatehousePermanentSpans(installDistance, installTiming, target),
                        reason => new NativeResolutionException(NativeCapabilityState.Conflict, reason));
                    var publication = new NativeHookPublicationState();
                    try
                    {
                        runtimeState = new GatehousePermanentRuntimeState(
                            nativeRegion,
                            unchecked((ulong)moduleBase),
                            installDistance,
                            installTiming,
                            publication,
                            automation);
                    }
                    catch
                    {
                        // A started publication may have written spans; its claim stays.
                        if (!publication.HasStarted)
                            ApiSharedNativeSites.Release(ownership, ApiSharedNativeSites.GatehousePermanent);
                        throw;
                    }
                }
                else
                {
                    runtimeState = GatehousePermanentRuntimeState.CreateTestState(
                        nativeRegion == null && installDistance, nativeRegion == null && installTiming);
                }
            }
            catch (NativeResolutionException ex)
            {
                distanceOriginDiagnostic = Diagnostic(NativeCapabilityIds.GatehouseDistanceOrigin, ex.State, binaryHash, ex.Message);
                timingDiagnostic = Diagnostic(NativeCapabilityIds.GatehouseTiming, ex.State, binaryHash, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                distanceOriginDiagnostic = Diagnostic(NativeCapabilityIds.GatehouseDistanceOrigin, NativeCapabilityState.Faulted, binaryHash, ex.Message);
                timingDiagnostic = Diagnostic(NativeCapabilityIds.GatehouseTiming, NativeCapabilityState.Faulted, binaryHash, ex.Message);
                return;
            }

            if (distanceLiveFailure == null)
                ResolveDistanceOrigin(
                binaryHash,
                moduleBase,
                memory,
                nativeMemory,
                ownership,
                mutationSync,
                log,
                target,
                functionSection,
                runtimeState,
                out distanceOriginService,
                out distanceOriginDiagnostic);
            else
                distanceOriginDiagnostic = Diagnostic(
                    NativeCapabilityIds.GatehouseDistanceOrigin,
                    distanceLiveFailure.State,
                    binaryHash,
                    distanceLiveFailure.Message);

            if (timingLiveFailure == null)
                ResolveTiming(
                binaryHash,
                moduleBase,
                memory,
                nativeMemory,
                ownership,
                mutationSync,
                log,
                target,
                functionSection,
                runtimeState,
                out timingService,
                out timingDiagnostic);
            else
                timingDiagnostic = Diagnostic(
                    NativeCapabilityIds.GatehouseTiming,
                    timingLiveFailure.State,
                    binaryHash,
                    timingLiveFailure.Message);
        }

        private static void ValidateLiveDistance(
            INativeMemory memory,
            long moduleBase,
            GatehouseBuildTarget target) =>
            RequireLiveBytes(
                memory,
                moduleBase + target.DistanceBlockRva,
                target.VanillaDistanceBlockBytes,
                "live gatehouse distance block");

        private static void ValidateLiveTiming(
            INativeMemory memory,
            long moduleBase,
            GatehouseBuildTarget target)
        {
            RequireLiveBytes(
                memory,
                moduleBase + target.DecisionBlockRva,
                target.DecisionBlockBytes,
                "live gatehouse decision block");
            RequireLiveBytes(
                memory,
                moduleBase + target.HumanDelayBlockRva,
                target.HumanDelayBlockBytes.Take(7).ToArray(),
                "live gatehouse human-delay block");
        }

        private static void RequireLiveBytes(
            INativeMemory memory,
            long address,
            byte[] expected,
            string name)
        {
            for (int index = 0; index < expected.Length; index++)
            {
                byte actual = memory.ReadByte(address + index);
                if (actual != expected[index])
                {
                    throw new NativeResolutionException(
                        NativeCapabilityState.ValidationFailed,
                        $"{name} changed at +0x{index:X}: expected=0x{expected[index]:X2}, actual=0x{actual:X2}.");
                }
            }
        }

        private static void ResolveDistanceOrigin(
            string binaryHash,
            long moduleBase,
            ReadOnlySpan<byte> memory,
            INativeMemory nativeMemory,
            NativeOwnershipRegistry ownership,
            object mutationSync,
            ManualLogSource log,
            GatehouseBuildTarget target,
            NativeSection functionSection,
            GatehousePermanentRuntimeState runtimeState,
            out GatehouseDistanceOriginService service,
            out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            try
            {
                if (target.VanillaDistanceBlockBytes == null || target.CenteredDistanceBlockBytes == null ||
                    target.VanillaDistanceBlockBytes.Length != target.CenteredDistanceBlockBytes.Length)
                    throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The gatehouse distance patch catalog is incomplete or has mismatched block lengths.");
                RequireInsideFunction(target, target.DistanceBlockRva, target.VanillaDistanceBlockBytes.Length, "gatehouse distance block");
                RequireSameSection(functionSection, target.DistanceBlockRva, target.VanillaDistanceBlockBytes.Length, "gatehouse distance block");
                RequireBytes(memory, target.DistanceBlockRva, target.VanillaDistanceBlockBytes, "gatehouse distance block");

                var memoryTarget = new GatehouseDistanceOriginTarget(
                    moduleBase + target.DistanceBlockRva,
                    target.VanillaDistanceBlockBytes,
                    target.CenteredDistanceBlockBytes);
                service = new GatehouseDistanceOriginService(
                    binaryHash, memoryTarget, runtimeState, ownership, mutationSync, log);
                diagnostic = AvailableDiagnostic(NativeCapabilityIds.GatehouseDistanceOrigin, binaryHash, target);
            }
            catch (NativeResolutionException ex)
            {
                diagnostic = Diagnostic(NativeCapabilityIds.GatehouseDistanceOrigin, ex.State, binaryHash, ex.Message);
            }
            catch (Exception ex)
            {
                diagnostic = Diagnostic(NativeCapabilityIds.GatehouseDistanceOrigin, NativeCapabilityState.Faulted, binaryHash, ex.Message);
            }
        }

        private static void ResolveTiming(
            string binaryHash,
            long moduleBase,
            ReadOnlySpan<byte> memory,
            INativeMemory nativeMemory,
            NativeOwnershipRegistry ownership,
            object mutationSync,
            ManualLogSource log,
            GatehouseBuildTarget target,
            NativeSection functionSection,
            GatehousePermanentRuntimeState runtimeState,
            out GatehouseTimingService service,
            out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            try
            {
                RequireInsideFunction(target, target.DecisionBlockRva, target.DecisionBlockBytes.Length, "gatehouse decision block");
                RequireInsideFunction(target, target.HumanDelayBlockRva, target.HumanDelayBlockBytes.Length, "gatehouse human delay block");
                RequireInsideFunction(target, target.AiCloseDistanceRva, 4, "gatehouse AI close distance");
                RequireInsideFunction(target, target.AiReopenDelayRva, 4, "gatehouse AI reopen delay");
                RequireInsideFunction(target, target.HumanCloseDistanceRva, 4, "gatehouse human close distance");
                RequireInsideFunction(target, target.HumanReopenDelayRva, 4, "gatehouse human reopen delay");
                RequireSameSection(functionSection, target.DecisionBlockRva, target.DecisionBlockBytes.Length, "gatehouse decision block");
                RequireSameSection(functionSection, target.HumanDelayBlockRva, target.HumanDelayBlockBytes.Length, "gatehouse human delay block");
                RequireBytes(memory, target.DecisionBlockRva, target.DecisionBlockBytes, "gatehouse decision block");
                RequireBytes(memory, target.HumanDelayBlockRva, target.HumanDelayBlockBytes, "gatehouse human delay block");
                RequireInt32(memory, target.AiCloseDistanceRva, GatehouseTimingTarget.VanillaAiDistance, "gatehouse AI close distance");
                RequireInt32(memory, target.AiReopenDelayRva, GatehouseTimingTarget.VanillaAiDelay, "gatehouse AI reopen delay");
                RequireInt32(memory, target.HumanCloseDistanceRva, GatehouseTimingTarget.VanillaHumanDistance, "gatehouse human close distance");
                RequireInt32(memory, target.HumanReopenDelayRva, GatehouseTimingTarget.VanillaHumanDelay, "gatehouse human reopen delay");

                IReadOnlyList<NativeByteInvariant> invariants = CreateInstructionInvariants(moduleBase, target);
                var memoryTarget = new GatehouseTimingTarget(
                    moduleBase + target.AiCloseDistanceRva,
                    moduleBase + target.AiReopenDelayRva,
                    moduleBase + target.HumanCloseDistanceRva,
                    moduleBase + target.HumanReopenDelayRva,
                    invariants);
                service = new GatehouseTimingService(
                    binaryHash, memoryTarget, runtimeState, ownership, mutationSync, log);
                diagnostic = AvailableDiagnostic(NativeCapabilityIds.GatehouseTiming, binaryHash, target);
            }
            catch (NativeResolutionException ex)
            {
                diagnostic = Diagnostic(NativeCapabilityIds.GatehouseTiming, ex.State, binaryHash, ex.Message);
            }
            catch (Exception ex)
            {
                diagnostic = Diagnostic(NativeCapabilityIds.GatehouseTiming, NativeCapabilityState.Faulted, binaryHash, ex.Message);
            }
        }

        private static NativeCapabilityDiagnostic AvailableDiagnostic(
            string capabilityId,
            string binaryHash,
            GatehouseBuildTarget target) =>
            Diagnostic(
                capabilityId,
                NativeCapabilityState.Available,
                binaryHash,
                $"Validated gatehouse handler RVA 0x{target.FunctionRva:X}-0x{target.FunctionEndRva:X} with function SHA-256 {target.FunctionHash}.");

        private static NativeCapabilityDiagnostic Diagnostic(
            string capabilityId,
            NativeCapabilityState state,
            string binaryHash,
            string reason) =>
            new NativeCapabilityDiagnostic(capabilityId, state, binaryHash, reason);

        private static IReadOnlyList<NativeByteInvariant> CreateInstructionInvariants(long moduleBase, GatehouseBuildTarget target)
        {
            var result = new List<NativeByteInvariant>();
            AddBlockInvariants(result, moduleBase, target.DecisionBlockRva, target.DecisionBlockBytes,
                target.AiCloseDistanceRva, target.AiReopenDelayRva, target.HumanCloseDistanceRva);
            AddBlockInvariants(result, moduleBase, target.HumanDelayBlockRva, target.HumanDelayBlockBytes.Take(7).ToArray(),
                target.HumanReopenDelayRva);
            return result;
        }

        private static void AddBlockInvariants(
            List<NativeByteInvariant> result,
            long moduleBase,
            int blockRva,
            byte[] bytes,
            params int[] immediateRvas)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                int rva = checked(blockRva + index);
                bool mutable = false;
                foreach (int immediateRva in immediateRvas)
                    if (rva >= immediateRva && rva < immediateRva + 4)
                        mutable = true;
                if (!mutable)
                    result.Add(new NativeByteInvariant(moduleBase + rva, bytes[index]));
            }
        }

        private static void RequireInsideFunction(GatehouseBuildTarget target, int rva, int length, string name)
        {
            if (rva < target.FunctionRva || length <= 0 || rva > target.FunctionEndRva - length)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, name + " lies outside the catalogued gatehouse function.");
        }

        private static void RequireSameSection(NativeSection section, int rva, int length, string name)
        {
            if (!section.Contains(rva, length))
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, name + " does not share the gatehouse function's executable section.");
        }

        private static void RequireBytes(ReadOnlySpan<byte> memory, int rva, byte[] expected, string target)
        {
            if (rva < 0 || rva > memory.Length - expected.Length)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, target + " lies outside the native image.");
            for (int index = 0; index < expected.Length; index++)
                if (memory[rva + index] != expected[index])
                    throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, $"{target} changed at +0x{index:X}: expected=0x{expected[index]:X2}, actual=0x{memory[rva + index]:X2}.");
        }

        private static void RequireInt32(ReadOnlySpan<byte> memory, int rva, int expected, string target)
        {
            int actual = NativePeImage.ReadInt32(memory, rva);
            if (actual != expected)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, $"{target} changed: expected={expected}, actual={actual}.");
        }
    }
}
