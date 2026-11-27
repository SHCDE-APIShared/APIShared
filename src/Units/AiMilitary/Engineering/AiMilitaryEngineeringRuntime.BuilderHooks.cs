using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using APIShared.NativeSites;

namespace APIShared.Units.AiMilitary.Engineering
{
    internal static unsafe partial class AiMilitaryEngineeringRuntime
    {
        private static ulong Address(int rva) => unchecked((ulong)(module.ToInt64() + rva));
        private static void Validate(int index, bool installed)
        {
            var native = Hooks[index].Hook as NativeDetour<Builder>;
            if (!Hooks[index].Success) throw new InvalidOperationException("Engineering Indirect entry backend differs.");
            byte[] bytes = Quota.AiQuotaNativeContract.Hex(EntryHex[index]);
            AiEngineeringNativeGate.ValidateEntry(Hooks[index].Hook, module + Rvas[index], bytes, installed);
            Gates[index].Validate(native.HookEntryPointAddress, unchecked((ulong)native.TrampolineAddress.ToInt64()));
        }
        private static void Invoke(int index)
        {
            BuildScope previous = scope; scope = default;
            if (Publication.IsReady)
            {
                try { scope = Begin(index); } catch (Exception error) { Report(error); }
            }
            bool completed = false;
            try { Hooks[index].Original(); completed = true; }
            finally
            {
                try { if (completed && scope.History != null && scope.Generation == generation) Complete(scope); }
                catch (Exception error) { Report(error); }
                finally { scope = previous; }
            }
        }
        private static BuildScope Begin(int index)
        {
            int id = *(int*)(module + 0x8F9BE4); if (id <= 0 || id >= 4000) return default;
            byte* building = (byte*)(module + BuildingManager + id * BuildingStride);
            var key = new BuildingRef(id, *(uint*)(building + 0x134)); if (key.Global == 0) return default;
            if (!Builds.TryGetValue(key, out AiBuildProvenance history))
            {
                // An archive containing a progressed tent without our record has unknown
                // earlier workers. Treat it conservatively as regular, never ALL=true.
                history = new AiBuildProvenance((int)Types[index], *(int*)(building + 0x2D4) <= 0); Builds.Add(key, history);
                ((uint*)trackedBuildings)[id] = key.Global;
            }
            if (history.Type != (int)Types[index]) history.Known = false;
            int count = *(short*)(building + 0x1F6);
            if (count < 0 || count > 4) { history.Known = false; count = Math.Max(0, Math.Min(4, count)); }
            for (int i = 0; i < count; i++)
            {
                int worker = *(ushort*)(building + 0x1FA + i * 2); uint gid = *(uint*)(building + 0x204 + i * 4);
                var reference = new AiUnitRef(worker, gid);
                if (!TryUnit(reference, out byte* raw) || *(ushort*)(raw + 0x6E6) != (int)eChimps.CHIMP_TYPE_ENGINEER)
                { history.Observe(false, false); continue; }
                history.Observe(true, isAdditional(reference));
            }
            return new BuildScope { Index = index, Building = key, History = history, Generation = generation };
        }

        private static void Complete(BuildScope value)
        {
            if (value.Product.UnitId == 0) return;
            if (!TryUnit(value.Product, out byte* raw)) return;
            int owner = *(ushort*)(raw + 0x6EE);
            AiUnitRef[] crew = ReadCrew(raw, Crew[value.Index]);
            bool extra = value.History.IsAdditional;
            // A complete native builder may release mismatched crews and leave the new
            // machine empty. Historical provenance remains useful, but never imply that
            // it is a fully staffed/admissible machine.
            markProduct(value.Product, extra);
            ReplaceProductHistory(value.Product, new ProductHistory((int)Types[value.Index], owner, extra, crew));
            Builds.Remove(value.Building);
            if (((uint*)trackedBuildings)[value.Building.Id] == value.Building.Global) ((uint*)trackedBuildings)[value.Building.Id] = 0;
        }
    }
}
