using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using APIShared.Units.AiMilitary;
using APIShared.Units.AiMilitary.Engineering;
using APIShared.Units.AiMilitary.Quota;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed unsafe class AiMilitaryEngineeringRuntimeTests
    {
        [TestMethod]
        public void HistoricalBuildAllRuleAndExplicitFixesReplacementSurviveSaveAndGenerationBoundaries()
        {
            using (var fixture = new Fixture())
            {
                fixture.Workers(true, true);
                fixture.Build(500, 0, new AiUnitRef(10, 110));
                Assert.IsTrue(fixture.Marked[new AiUnitRef(10, 110)], "Every contributing engineer was additional.");
                byte[] payload = AiMilitaryEngineeringRuntime.Capture();
                AiMilitaryEngineeringRuntime.Clear(2); AiMilitaryEngineeringRuntime.Restore(payload);
                CollectionAssert.AreEqual(payload, AiMilitaryEngineeringRuntime.Capture(), "Canonical history round-trip.");

                AiMilitaryEngineeringRuntime.Clear(3); fixture.Marked.Clear();
                fixture.Workers(true, false);
                object firstTick = fixture.Begin(501, 0);
                fixture.Workers(true, true);
                fixture.Build(501, 1, new AiUnitRef(11, 111));
                Assert.IsFalse(fixture.Marked[new AiUnitRef(11, 111)], "A later all-additional crew cannot erase earlier regular work.");
                Assert.IsNotNull(firstTick);
                AiMilitaryEngineeringRuntime.Clear(4); fixture.Marked.Clear();
                fixture.Build(502, 50, new AiUnitRef(12, 112));
                Assert.IsFalse(fixture.Marked[new AiUnitRef(12, 112)], "Unobserved earlier progress remains unknown/regular.");

                AiMilitaryEngineeringRuntime.Clear(5); fixture.Marked.Clear();
                var old = new AiUnitRef(20, 120); var moved = new AiUnitRef(21, 121); var unrelated = new AiUnitRef(22, 122);
                fixture.Machine(old, true); fixture.Extras.Add(old);
                var scope = AiMilitaryEngineeringRuntime.BeginReplacement(old.UnitId, old.GlobalId);
                Assert.IsNotNull(scope);
                fixture.Machine(moved, false); fixture.Delete(old);
                AiMilitaryEngineeringRuntime.OnUnitCreated(moved, eChimps.CHIMP_TYPE_CATAPULT);
                AiMilitaryEngineeringRuntime.EndReplacement(scope, true);
                // Same crew on an unrelated creation is deliberately insufficient.
                fixture.Machine(unrelated, true);
                AiMilitaryEngineeringRuntime.OnUnitCreated(unrelated, eChimps.CHIMP_TYPE_CATAPULT);
                AiMilitaryEngineeringRuntime.Reconcile();
                Assert.IsFalse(fixture.Marked.ContainsKey(unrelated));
                Assert.IsFalse(fixture.Marked.ContainsKey(moved), "Delayed incomplete crew is never acknowledged as restored.");
                payload = AiMilitaryEngineeringRuntime.Capture();
                AiMilitaryEngineeringRuntime.Clear(6); AiMilitaryEngineeringRuntime.Restore(payload);
                fixture.Machine(moved, true); AiMilitaryEngineeringRuntime.Reconcile();
                Assert.IsTrue(fixture.Marked[moved], "Exact scoped new ID plus the original crew identities restores lineage after load.");
                Assert.IsFalse(fixture.Marked.ContainsKey(unrelated));

                AiMilitaryEngineeringRuntime.Clear(7); fixture.Marked.Clear();
                fixture.Machine(old, true); fixture.Extras.Add(old);
                scope = AiMilitaryEngineeringRuntime.BeginReplacement(old.UnitId, old.GlobalId);
                fixture.Machine(moved, true); AiMilitaryEngineeringRuntime.OnUnitCreated(moved, eChimps.CHIMP_TYPE_CATAPULT);
                AiMilitaryEngineeringRuntime.EndReplacement(scope, true); // Original deletion was vetoed.
                AiMilitaryEngineeringRuntime.Reconcile(); Assert.IsFalse(fixture.Marked.ContainsKey(moved));

                scope = AiMilitaryEngineeringRuntime.BeginReplacement(old.UnitId, old.GlobalId);
                fixture.Delete(old); AiMilitaryEngineeringRuntime.OnUnitCreated(moved, eChimps.CHIMP_TYPE_CATAPULT);
                AiMilitaryEngineeringRuntime.EndReplacement(scope, false); // Original threw.
                AiMilitaryEngineeringRuntime.Reconcile(); Assert.IsFalse(fixture.Marked.ContainsKey(moved));

                fixture.Machine(old, true); scope = AiMilitaryEngineeringRuntime.BeginReplacement(old.UnitId, old.GlobalId);
                AiMilitaryEngineeringRuntime.Clear(8); fixture.Delete(old);
                AiMilitaryEngineeringRuntime.OnUnitCreated(moved, eChimps.CHIMP_TYPE_CATAPULT);
                AiMilitaryEngineeringRuntime.EndReplacement(scope, true); AiMilitaryEngineeringRuntime.Reconcile();
                Assert.IsFalse(fixture.Marked.ContainsKey(moved), "An old-generation scope cannot publish in the next world.");
            }
        }
        private sealed class Fixture : IDisposable
        {
            private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
            private static readonly Type Runtime = typeof(AiMilitaryEngineeringRuntime);
            private readonly Dictionary<FieldInfo, object> fields = new Dictionary<FieldInfo, object>();
            private readonly byte[] before;
            private readonly IntPtr image, tracked, demand;
            private readonly object publication;
            private readonly FieldInfo publicationState;
            private readonly int oldPublication;
            internal readonly HashSet<AiUnitRef> Extras = new HashSet<AiUnitRef>();
            internal readonly Dictionary<AiUnitRef, bool> Marked = new Dictionary<AiUnitRef, bool>();
            internal Fixture()
            {
                publication = Runtime.GetField("Publication", Static).GetValue(null);
                publicationState = publication.GetType().GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
                oldPublication = (int)publicationState.GetValue(publication);
                Assert.AreEqual(0, oldPublication, "The fixture must not replace a process-published game owner.");
                before = AiMilitaryEngineeringRuntime.Capture();
                image = Marshal.AllocHGlobal(0x9000000); tracked = Marshal.AllocHGlobal(16000); demand = Marshal.AllocHGlobal(4);
                Set(Runtime, "module", image); Set(Runtime, "trackedBuildings", tracked);
                Set(Runtime, "isAdditional", (Func<AiUnitRef, bool>)Extras.Contains);
                Set(Runtime, "markProduct", (Action<AiUnitRef, bool>)((unit, value) => { Marked[unit] = value; if (value) Extras.Add(unit); else Extras.Remove(unit); }));
                Set(Runtime, "generation", 1L); Set(typeof(AiMilitaryQuotaRuntime), "state", demand);
                Marshal.WriteInt32(demand, 1); publicationState.SetValue(publication, 2);
                AiMilitaryEngineeringRuntime.Clear(1);
                new Span<byte>((void*)tracked, 16000).Clear();
            }
            private void Set(Type owner, string name, object value)
            { FieldInfo field = owner.GetField(name, Static); fields.Add(field, field.GetValue(null)); field.SetValue(null, value); }
            internal byte* Unit(AiUnitRef reference, eChimps type)
            {
                byte* raw = (byte*)(image + 0x67E8400 + reference.UnitId * 0x490);
                new Span<byte>(raw + 0x65C, 0x490).Clear();
                *(ushort*)(raw + 0x6E4) = (ushort)AliveState.IsAlive; *(ushort*)(raw + 0x6E6) = (ushort)type;
                *(ushort*)(raw + 0x6EE) = 1; *(uint*)(raw + 0x6F0) = reference.GlobalId;
                return raw;
            }
            internal void Workers(bool first, bool second)
            {
                var a = new AiUnitRef(1, 101); var b = new AiUnitRef(2, 102);
                Unit(a, eChimps.CHIMP_TYPE_ENGINEER); Unit(b, eChimps.CHIMP_TYPE_ENGINEER);
                if (first) Extras.Add(a); else Extras.Remove(a); if (second) Extras.Add(b); else Extras.Remove(b);
            }
            internal void Machine(AiUnitRef reference, bool crew)
            {
                byte* raw = Unit(reference, eChimps.CHIMP_TYPE_CATAPULT);
                if (!crew) return;
                *(ushort*)(raw + 0xA0C) = 2; *(ushort*)(raw + 0x96C) = 1; *(ushort*)(raw + 0x96E) = 2;
                *(uint*)(raw + 0x974) = 101; *(uint*)(raw + 0x978) = 102;
            }
            internal void Delete(AiUnitRef reference) => *(ushort*)((byte*)(image + 0x67E8400 + reference.UnitId * 0x490) + 0x6E4) = (ushort)AliveState.MarkedForDeletion;
            internal object Begin(uint buildingGlobal, int progress)
            {
                Marshal.WriteInt32(image, 0x8F9BE4, 1);
                byte* raw = (byte*)(image + 0x64CCBB0 + 0x32C); new Span<byte>(raw + 0x5C, 0x32C).Clear();
                *(ushort*)(raw + 0x12C) = (ushort)AliveState.IsAlive; *(uint*)(raw + 0x134) = buildingGlobal;
                *(int*)(raw + 0x2D4) = progress; *(short*)(raw + 0x1F6) = 2;
                *(ushort*)(raw + 0x1FA) = 1; *(ushort*)(raw + 0x1FC) = 2;
                *(uint*)(raw + 0x204) = 101; *(uint*)(raw + 0x208) = 102;
                return Runtime.GetMethod("Begin", Static).Invoke(null, new object[] { 1 });
            }
            internal void Build(uint buildingGlobal, int progress, AiUnitRef product)
            {
                object value = Begin(buildingGlobal, progress); var field = Runtime.GetField("scope", Static); field.SetValue(null, value);
                Machine(product, true); AiMilitaryEngineeringRuntime.OnUnitCreated(product, eChimps.CHIMP_TYPE_CATAPULT);
                Runtime.GetMethod("Complete", Static).Invoke(null, new[] { field.GetValue(null) });
                field.SetValue(null, Activator.CreateInstance(field.FieldType));
            }
            public void Dispose()
            {
                AiMilitaryEngineeringRuntime.Clear(0);
                foreach (var pair in fields) pair.Key.SetValue(null, pair.Value);
                AiMilitaryEngineeringRuntime.Restore(before); publicationState.SetValue(publication, oldPublication);
                Marshal.FreeHGlobal(demand); Marshal.FreeHGlobal(tracked); Marshal.FreeHGlobal(image);
            }
        }
    }
}
