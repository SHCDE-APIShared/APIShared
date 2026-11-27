using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Wildlife;
using RedBird.X64.Extensions;

// Executes APIShared's herd flight gate (Wildlife.HerdRuntime flight gates) at the two audited spans of the private
// image copy: a calm herd reads no threat, every other herd reads Vanilla's sum, the other two counters and all
// other live registers stay as Vanilla leaves them.
internal static partial class Program
{
    private const int DeerFlightRva = 0x124F20, GoatFlightRva = 0x125330, FlightSpan = 18;

    private static void TestHerdFlightGates()
    {
        IntPtr calm = Marshal.AllocHGlobal(HerdNativeAssembly.HerdCapacity);
        IntPtr sector = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.WriteInt32(sector, HerdNativeAssembly.SectorPredatorsOffset, 2);
            Marshal.WriteInt32(sector, HerdNativeAssembly.SectorUnitsOffset, 3);
            Marshal.WriteInt32(sector, 0x40, 11);
            Marshal.WriteInt32(sector, 0x44, 12);
            foreach (int site in new[] { DeerFlightRva, GoatFlightRva })
            {
                Check(Convert.ToHexString(Read(site, FlightSpan)) == "8B7C0828037C0848448B7C0840448B640844",
                    "Herd flight span is the audited threat read at 0x" + site.ToString("X") + ".");
                var original = WorkerProductionBonusNativeDefinition.Decode(Read(site, FlightSpan), Address(site));
                const int herd = 7;
                foreach (bool calmHerd in new[] { false, true })
                {
                    for (int index = 0; index < HerdNativeAssembly.HerdCapacity; index++) Marshal.WriteByte(calm, index, 0);
                    Marshal.WriteByte(calm, herd, calmHerd ? (byte)1 : (byte)0);
                    // A calm neighbour must not affect this herd.
                    Marshal.WriteByte(calm, herd + 1, 1);
                    using var harness = new MachineHarness(a =>
                    {
                        HerdNativeAssembly.EmitFlightGate(a, original, unchecked((ulong)calm.ToInt64()));
                        a.AddUnrestrictedJmp(Address(site + FlightSpan));
                    }, new Dictionary<int, int> { [site + FlightSpan] = 1 });
                    harness.ResetInput();
                    // The deer and goat updates address the sector as [rax + rcx]; RBP is the herd.
                    harness.Set(0, unchecked((ulong)sector.ToInt64()) - 0x40);
                    harness.Set(1, 0x40);
                    harness.Set(5, herd);
                    harness.Run();
                    Check(harness.Marker == 1, "Herd flight gate reaches its continuation.");
                    Check(harness.Get(7) == (calmHerd ? 0UL : 5UL), "Herd flight gate gives " + (calmHerd ? "a calm" : "another") + " herd its threat.");
                    Check(harness.Get(15) == 11 && harness.Get(12) == 12, "Herd flight gate keeps the other two sector counters.");
                    harness.CheckPreserved(0, 7, 12, 15);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(calm);
            Marshal.FreeHGlobal(sector);
        }
    }
}
