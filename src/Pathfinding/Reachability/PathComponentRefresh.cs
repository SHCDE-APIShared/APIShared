using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.API.LowLevel;

namespace APIShared.Pathfinding
{
    /// <summary>Synchronously refreshes Vanilla's current path-component grid.</summary>
    public static class PathComponentRefresh
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Rebuild(IntPtr context, int force);

        private sealed class Binding
        {
            internal Binding(object engine, IntPtr context, Rebuild rebuild)
            { Engine = engine; Context = context; Invoke = rebuild; }

            internal readonly object Engine;
            internal readonly IntPtr Context;
            internal readonly Rebuild Invoke;
        }

        private static Binding binding;

        /// <summary>Whether the native call and engine-monitor contract were resolved at startup.</summary>
        public static bool IsAvailable => Volatile.Read(ref binding) != null;

        /// <summary>
        /// Call during an active world under the game's engine monitor, from native simulation or
        /// from a validated locked editor boundary. If <paramref name="force"/> is false, a clean
        /// grid succeeds without work. A rebuild invokes the live Vanilla entry, including any
        /// cooperating detour. Success with <paramref name="rebuilt"/> set means the native rebuild
        /// returned success and published a new generation before this method returned.
        /// </summary>
        /// <remarks>A failed native publication can have changed scratch. Do not retry or perform
        /// topology-dependent work in the same operation after failure.</remarks>
        public static bool TryRefresh(bool force, out bool rebuilt, out string reason)
        {
            rebuilt = false;
            Binding current = Volatile.Read(ref binding);
            if (current == null || !Monitor.IsEntered(current.Engine))
            {
                reason = "Path-component refresh requires an available binding and the game engine monitor.";
                return false;
            }
            var view = GamePathingManagerAPI.Instance.GetPathfindingContextView();
            if (view.Address != current.Context)
            {
                reason = "The current pathfinding context differs from the validated native context.";
                return false;
            }
            reason = string.Empty;
            if (!force && view.ComponentGridDirty == 0)
                return true;

            uint generation = view.ComponentGeneration;
            rebuilt = current.Invoke(view.Address, 1) == 1 && view.ComponentGeneration != generation;
            if (!rebuilt)
                reason = "The native rebuild did not publish a new path-component generation.";
            return rebuilt;
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            try
            {
                if (!string.Equals(hash, PathComponentRefreshContract.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The path-component context layout is not verified for this native hash.");

                int rva = PathComponentRefreshContract.RebuildRva;
                string[] pattern = PathComponentRefreshContract.RebuildPattern.Split(' ');
                if (rva > context.Memory.Length - pattern.Length)
                    throw new InvalidOperationException("The path-component entry is outside the load image.");
                for (int index = 0; index < pattern.Length; index++)
                    if (pattern[index] != "??" &&
                        context.Memory[rva + index] != byte.Parse(pattern[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                        throw new InvalidOperationException("The path-component entry differs from the audited load image.");

                object engine = EngineMonitor.Current ?? throw new InvalidOperationException("Game engine monitor unavailable.");
                IntPtr address = IntPtr.Add(context.ModuleHandle, PathComponentRefreshContract.ContextRva);
                if (GamePathingManagerAPI.Instance.GetPathfindingContextView().Address != address)
                    throw new InvalidOperationException("Pathfinding context address differs from the audited native layout.");
                var claim = NativeSiteRegistry.Register(APISharedPlugin.PluginGuid, "path-component refresh", rva, 1, NativeSiteMode.CallOnly);
                if (!claim.Succeeded)
                    throw new InvalidOperationException(claim.Reason);

                var invoke = Marshal.GetDelegateForFunctionPointer<Rebuild>(IntPtr.Add(context.ModuleHandle, rva));
                Volatile.Write(ref binding, new Binding(engine, address, invoke));
                NativeApiLog.Info(log, "Path-component refresh bound by reference-rva at 0x" + rva.ToString("X") + ".");
            }
            catch (Exception exception)
            {
                NativeApiLog.Error(log, "Path-component refresh unavailable: " + exception.Message);
            }
        }
    }
}
