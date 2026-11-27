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
        /// <summary>
        /// Stages an exact low-nibble-first route for the current owned single-unit builder.
        /// The synchronous validator is called again before publication; no route survives its frame.
        /// Returns false for a null or empty owner.
        /// </summary>
        public static bool TryStageWeightedRoute(string ownerGuid, IntPtr context,
            int startX, int startY, int targetX, int targetY, int playerId,
            byte[] packedDirections, int directionCount, Func<bool> validate)
        {
            if (string.IsNullOrEmpty(ownerGuid) || !string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal)) return false;
            return AssassinRouteHandoff.Stage(context, startX, startY, targetX, targetY,
                playerId, packedDirections, directionCount, validate);
        }

        /// <summary>TEMP_GATE_ROUTE_ACCEPTANCE: reason from the existing handoff guard; never probes identity.</summary>
        public static string TemporaryWeightedRequestReason => AssassinRouteHandoff.TemporaryRequestReason ?? "unknown";

        /// <summary>Returns the current owned unit's full control word and movement delay without scanning units.</summary>
        public static bool TryGetCurrentWeightedRequest(string ownerGuid, IntPtr context,
            int startX, int startY, int targetX, int targetY, out int controlPlayer, out int speedDelay)
        {
            controlPlayer = speedDelay = -1;
            return !string.IsNullOrEmpty(ownerGuid) &&
                string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal) &&
                AssassinRouteHandoff.TryResolve(context, startX, startY, targetX, targetY,
                    out controlPlayer, out speedDelay);
        }

        /// <summary>Stages a route and reports final validation/publication duration and success; observer errors are isolated.</summary>
        public static bool TryStageWeightedRoute(string ownerGuid, IntPtr context,
            int startX, int startY, int targetX, int targetY, int playerId,
            byte[] packedDirections, int directionCount, Func<bool> validate, Action<long, bool> completion)
        {
            if (string.IsNullOrEmpty(ownerGuid) || !string.Equals(weightedOwner, ownerGuid, StringComparison.Ordinal)) return false;
            return AssassinRouteHandoff.Stage(context, startX, startY, targetX, targetY,
                playerId, packedDirections, directionCount, validate, completion);
        }

        /// <summary>A single-unit builder is active; failed staging must preserve its native field.</summary>
        public static bool HasSingleUnitRoutePublication => AssassinRouteHandoff.HasFrame;

        /// <summary>
        /// Opens the single-unit route output frame around one synchronous native tile-path builder call.
        /// </summary>
        /// <remarks>
        /// Only the mod that owns the movement path-builder detour opens frames; the registered weighted builder
        /// stages routes into the innermost frame through <see cref="TryStageWeightedRoute(string, IntPtr, int, int, int, int, int, byte[], int, Func{bool})"/>.
        /// Open the frame on the builder's thread before calling the original builder, pass its result to
        /// <see cref="AssassinRouteOutputFrame.Complete"/>, and always call <see cref="AssassinRouteOutputFrame.Leave"/>
        /// in a finally block. Frames nest per thread and must be left in reverse order. A frame without
        /// <paramref name="identityValid"/> or <paramref name="publish"/> only shadows an outer frame: staging into it
        /// is declined, so a nested unrelated builder can never publish an outer unit's route. The callbacks run
        /// synchronously on that thread; <paramref name="publish"/> writes the staged directions into the native output
        /// and returns the builder result to report.
        /// </remarks>
        /// <param name="context">The native path manager passed to the builder.</param>
        /// <param name="startX">The request's starting tile X coordinate.</param>
        /// <param name="startY">The request's starting tile Y coordinate.</param>
        /// <param name="targetX">The request's destination tile X coordinate.</param>
        /// <param name="targetY">The request's destination tile Y coordinate.</param>
        /// <param name="playerId">The one-based player game ID a staged route must name.</param>
        /// <param name="identityValid">Proves that the request still belongs to the same unit and output buffer.</param>
        /// <param name="publish">Writes packed directions (bytes, count) into the native output; returns the result.</param>
        /// <param name="controlPlayer">The unit's full control word for TryGetCurrentWeightedRequest; -1 when unknown.</param>
        /// <param name="speedDelay">The unit's movement delay for TryGetCurrentWeightedRequest; -1 when unknown.</param>
        /// <param name="reportStage">Optional diagnostic sink (stage, result, detail); its failures are isolated.</param>
        /// <returns>The opened frame, already the current frame of this thread.</returns>
        public static AssassinRouteOutputFrame BeginRouteOutput(IntPtr context, int startX, int startY,
            int targetX, int targetY, int playerId, Func<bool> identityValid, Func<byte[], int, int> publish,
            int controlPlayer = -1, int speedDelay = -1, Action<string, string, string> reportStage = null) =>
            new AssassinRouteOutputFrame(new AssassinRouteHandoff(context, startX, startY, targetX, targetY,
                playerId, identityValid, publish, controlPlayer, speedDelay, reportStage));
    }
}
