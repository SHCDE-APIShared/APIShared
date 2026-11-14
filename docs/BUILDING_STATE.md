# Building state

`APIShared.Buildings.BuildingStates` keeps small per-building values for a mod: a player's choice
for one of his buildings (a slider, a mode) and the simulation state that goes with it. APIShared
synchronizes owner changes through one tick-synchronous authenticated Chore, saves each table in a
versioned record, clears values for new buildings and fresh missions, and exposes a fixed native
layout so the mod's own native code can read the values. The mod decides what the values mean.

Contracts are documented at the declarations in
[BuildingStateContracts.cs](../src/Buildings/State/BuildingStateContracts.cs) and
[BuildingStates.cs](../src/Buildings/State/BuildingStates.cs).

## Usage

Register each table once at startup on the initialized Unity thread after Script Extender
readiness, unconditionally and in the same order on every peer, like a Chore channel:

    var registration = new BuildingStateRegistration(MyGuid, "meat", "MyMod.TanneryMeat",
        new[] { eStructs.STRUCT_TANNERS_WORKSHOP },
        new[]
        {
            new BuildingStateValue("meat-runs", 3, BuildingStateValueAccess.OwnerRequest),
            new BuildingStateValue("meat-trip", 1, BuildingStateValueAccess.Simulation)
        })
    { AcceptsOwnerRequests = () => featureEnabledByHost };
    if (!BuildingStates.TryRegister(registration, out BuildingStateTable table, out string reason)) Log(reason);

- **Values.** One to 12 bytes per building, each from 0 to its declared maximum, all starting
  at 0. The declaration order is the value index, the save format and the native layout: append
  new values only at the end. Saved records with fewer values load with the new ones at 0.
- **Owner requests.** `table.RequestChange(buildingId, globalId, index, value)` on Unity sends an
  explicit target value. Nothing changes locally; every peer, the sender included, applies it in
  the same tick only if `AcceptsOwnerRequests` is true there, the value is an owner value within
  its maximum, and the building with this ID and GlobalId is alive, of a registered type and owned
  by the Chore's verified sender. Rejections are silent. `AcceptsOwnerRequests` must give the same
  result on every peer (a host-synchronized setting, not a local option).
- **Pending requests.** `HasPendingRequest` stays true until this peer's latest request has been
  executed or rejected, so a slider can stay under the player's hand meanwhile. Call
  `ForgetPendingRequest` when the presentation closes; it does not cancel the request.
- **Simulation values.** Only the registering mod's deterministic simulation code changes them:
  `TrySetFromSimulation` from managed simulation callbacks, or its native code directly.
- **Reading.** `GetValue` is a memory read from any thread and returns 0 for another building
  identity. Combine it with [building panel sections](BUILDING_PANELS.md) to show the value.

## Identity, missions and saves

- An entry belongs to one one-based building ID together with its GlobalId. Reads for another
  identity return 0; the first write for a new identity clears the earlier values. Entries of
  destroyed buildings are inert and are replaced when the slot is reused. Buildings with IDs of
  `Capacity` (0x2000) or more are not stored.
- Each table owns a [versioned save record](SAVE_DATA.md) under the consumer's GUID and the
  given identifier: version 1, entry `values`, declared simulation-relevant so multiplayer saves
  are compared before loading. Every archive load replaces the table with the record or clears it
  (no record, out of scope, damaged); a damaged record is logged once. A mission start without an
  archive load clears the table; every mission start forgets pending requests.
- Record format of `values`: value count (byte), entry count (int32), then entries in ascending
  building ID with ID (uint16), GlobalId (uint32) and one byte per value, little-endian; entries
  whose values are all 0 are omitted. Equal tables give equal bytes on every peer.

## Native layout

Fixed for the process: `Capacity` entries of `EntrySize` bytes at `NativeAddress`, indexed by the
building ID. `EntrySize` is 8 for up to 4 values and 16 otherwise, so one scaled index register
addresses an entry. The entry holds the GlobalId as uint32 at `GlobalIdOffset` (0) and value `i`
as a byte at `ValuesOffset` (4) + `i`. Native readers compare the entry's GlobalId with the live
building before using a value, check the ID against `Capacity` unsigned, and write only
simulation values of a matching entry. The memory is never freed or moved.

## Limits

- Registration, request submission and pending state belong to the Unity thread; requests execute
  in the native simulation context. There is no change notification; read the values where needed.
- One APIShared Chore carries the requests of all tables. It is registered with the first table of
  the process, so peers need the same mods registering tables in the same order, as for any Chore.
- Tables, their memory and their save records live for the process.
