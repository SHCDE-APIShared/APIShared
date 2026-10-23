# Versioned save data

`APIShared.Savegames.VersionedSaveData` stores one versioned record per mod in a save, on top
of Script Extender `ModSaveDataAPI`. It adds what the raw handler leaves to every mod: an
explicit record version, migration steps owned by the mod, entries from a newer mod version
that survive an older version's saves, documented results for missing, damaged or too-new
data, and exactly one load notification per load. It does no work outside save and load.

Contracts are documented at the declarations in
[VersionedSaveDataContracts.cs](../src/Savegames/VersionedSaveDataContracts.cs) and
[VersionedSaveDataRuntime.cs](../src/Savegames/VersionedSaveDataRuntime.cs). The compiled
[SaveDataExample](../examples/ThirdPartyMod/SaveDataExample.cs) shows a complete registration.

## Usage

Register once during plugin startup (`Awake` or the Extender's `LibraryLoaded`), before the
first load whose record you need, and never from a save or load callback:

```csharp
var registration = new VersionedSaveDataRegistration(
    ownerGuid: MyPlugin.Guid,
    saveDataIdentifier: "Author.MyMod",     // ModSaveDataAPI identifier, file-name safe
    currentVersion: 2,
    minimumReaderVersion: 1,                // version 2 only added entries
    knownEntries: new[] { "settings", "garrison/" },
    capture: Capture,
    loaded: OnLoaded);
registration.AddMigration(1, entries => { /* turn a version-1 record into version 2 */ });
if (!VersionedSaveData.TryRegister(registration, out var handle, out string reason)) Log(reason);
```

A record is a set of named byte arrays (entries); the mod chooses each entry's encoding.
`knownEntries` lists the names this build owns. An item ending in `/` owns every name that
starts with it, so `garrison/` covers `garrison/17`. Entries outside the known set are not
delivered and are written back unchanged.

**Capture** runs on the Extender's save thread for every in-scope save. Write each known
entry with `writer.Write(name, bytes)` and return true. Known entries you do not write are
deleted from the save, so a destroyed building disappears with its entry. Writing nothing
still writes an empty record, which replaces older data. Return false to write nothing for
this save; the Extender then keeps the entry its archive already holds.

**Loaded** runs once at the end of every Extender archive load (`OnLoadSave` or `OnLoadMap`,
in their Post notification) with that load's result for your record, including `NoRecord`
when the save has none. Store the entries and apply them when your mission state is ready.
Use `NoRecord` and `OutOfScope` to reset state from an earlier mission: the Extender never
calls the `onUnload` callback of `ModSaveDataAPI` in 2.14.1 (see
[implementation](#script-extender-behaviour-it-relies-on)).

**Scope.** `SaveGames` (default) reads and writes gameplay saves only. `SaveGamesAndMaps`
also writes map editor saves and reads every map load, for data that belongs to the map,
such as a theme chosen in the editor.

**Existing data.** A mod that already stores data under an identifier can keep that
identifier and set `LegacyReader`: data without the record signature is passed to it, and
the entries it returns continue through the migration steps from the version it reports.
Without a legacy reader, unversioned data reports `Unrecognized` and is kept unchanged, so
choose a new identifier instead.

## Record-version migration policy

This is the policy for versioned save records:

1. **One version per record format.** `currentVersion` starts at 1 and grows by one whenever
   the set or meaning of entries changes. Every save writes `currentVersion`.
2. **Declare who can read it.** `minimumReaderVersion` is the oldest version that reads this
   build's records correctly. Keep it unchanged when a version only adds entries; raise it
   to the current version when an existing entry changes meaning or encoding. A reader below
   it gets `TooNew` and keeps the record byte for byte.
3. **Names keep their meaning.** Prefer a new entry name to an incompatible change of an
   existing entry; retire the old name with a migration step that removes it.
4. **Migration steps go one version at a time.** `AddMigration(n, step)` turns version `n`
   into `n + 1`. Steps run in ascending order from the record's version up to the current
   one; a version without a step needs no change. A record from a newer version runs no
   step. A step that throws makes the load `MigrationFailed`, and the record is kept.
5. **Steps must respect entries that already exist.** An older version writes back the
   unknown entries of a newer version, so after a downgrade and upgrade a version-2 record
   can already contain version-3 entries. A step must not overwrite such an entry.
6. **Kept entries can be stale.** Unknown entries are copied verbatim while an older version
   plays on. Validate references between entries when you restore.
7. **Unreadable data is never destroyed by guesswork.** Data that is too new, unrecognized or
   failed to migrate is written back unchanged by every save of that mission. Only data
   with the record signature that fails its checksum or structure (`Corrupt`) is replaced,
   because no version can read it.

The savegame settings record that APIShared itself writes
([SavegameModSettings](../src/Savegames/SavegameModSettings.cs)) is not covered by this
policy: it accepts only schema version 3 and has no migration
(`SavegameModSettings.TryDeserialize`). Capture, validation and the reader share one set of
limits (1024 mods, 16384 settings or Trail rules per mod, 1 MiB per value), so every settings
map it captures loads. Changing that record or lowering a limit changes what existing saves load.

## Results

| Load status | Delivered to Loaded | Later saves of the same mission |
|---|---|---|
| `NoRecord` | nothing | capture |
| `Restored` | known entries after migration | capture plus the kept unknown entries |
| `OutOfScope` | nothing | capture in gameplay saves; map editor saves are not written |
| `TooNew` | nothing | the loaded bytes, unchanged |
| `Corrupt` | nothing | capture, which replaces the damaged data |
| `Unrecognized` | nothing | the loaded bytes, unchanged |
| `MigrationFailed` | nothing | the loaded bytes, unchanged |

If the Loaded callback throws for a restored record, later saves of that mission also keep
the loaded bytes, because the mod did not take them. `VersionedSaveDataHandle.KeepsLoadedRecord`
shows this state; `LastWriteStatus` reports the last save (`Written`, `KeptLoadedRecord`,
`NotWritten`, `CaptureFailed`, `TooLarge`). Failed captures and records over a limit return
nothing to the Extender, which keeps the entry its archive already holds. Every callback
exception is isolated, logged and counted in `VersionedSaveData.CallbackFailures`; nothing is
thrown into the game or the Extender.

## Public limits

- One record holds at most 16384 entries (`MaximumEntries`), the per-mod settings limit of the
  savegame settings record. Writer and reader use the same limit, so every record written is
  readable.
- An entry holds at most 1 MiB, a record at most 8 MiB, an entry name at most 256 characters.
  Kept unknown entries count toward the limits; a record over a limit is not written.
- Identifiers are unique ignoring case, because the Extender's archive entry names are.
- Thread: registration is thread-safe. Capture runs on the Extender's save thread; legacy
  readers, migration steps and Loaded run on the thread that raised the load event. APIShared
  adds no dispatch.
- Ordering: Loaded callbacks of different registrations run in ordinal owner GUID, then
  identifier order. For save loads entered through APIShared's mission lifecycle, the saved
  mod settings are applied before the native load
  ([SavegameModSettings preparation](../src/Savegames/SavegameModSettings.Preparation.cs)), so
  Loaded sees them; the mission start notification follows Loaded. Capture order across mods
  is the Extender's handler order and is unspecified; captures must not depend on each other.
- Lifetime: registrations last for the process and cannot be removed. Kept and unknown
  entries belong to one mission: they are dropped at the next load and at a map unload
  outside a load.
- Multiplayer: every peer reads its own save copy, and client copies can differ from the
  host's. Records declared `SimulationRelevant` are compared before a multiplayer save loads
  (see [multiplayer record comparison](#multiplayer-record-comparison)); other records are not.
  Nothing is synchronized or repaired.
- Load paths: the result `Source` names the Extender event (`SaveGame`, `MultiplayerSave`,
  `MapToPlay`, `EditorMap`). Multiplayer saves load through `OnLoadMap`, whose Extender
  `LoadContext` reports a map; `MultiplayerSave` is chosen while APIShared's confirmed
  multiplayer save load runs or when the native multiplayer-save flag is set, and the
  `LoadContext` flag is never used. Which other menu paths raise which event, and the thread
  of the save and load callbacks, are not yet verified in the game: single-player and
  multiplayer save loads, map and campaign starts, editor loads and saves are pending
  in-game checks, as is the multiplayer record comparison with two players.

## Multiplayer record comparison

Set `SimulationRelevant = true` on a registration whose record holds simulation state that
must be identical on every player, for example unit identities or building state that the
mod's simulation reads. Leave it false (the default) for local, view or presentation data,
and keep such data in a separate record. At most 64 registrations may declare it.

**What is compared.** When the host selects a multiplayer save, the save readiness agreement
([SavegameLoadCoordinator](../src/Savegames/SavegameLoadCoordinator.cs)) reads, on every
player, that player's own copy of the selected save and takes the SHA-256 of each declared
record's stored bytes (or notes that the record is absent or unreadable). Clients send their
list with their readiness confirmation; the host compares each list with its own and releases
the load only when every player's settings fingerprint and declared records match.

**What is not compared.** Undeclared records; mod settings (the agreement already brings
every client's host settings to the saved values and compares their fingerprint); anything in
memory, such as state of an earlier single-player mission or of the lobby; other saves; the
rest of the save file, whose bytes can legitimately differ between players. Players can
therefore change settings and play single-player freely before loading a shared multiplayer
save. Loading that save then replaces earlier state: the load reports each record with
`Restored` or `NoRecord`, and kept entries of the earlier mission are dropped.

**On a difference.** The load does not start. Every player sees an explanation naming the
player, the mod (owner GUID) and the record, and whether the record differs, is missing on one
side, is unreadable, or is declared by only one side (different mod versions). At most three
differences are listed. A hash comparison detects differences only; it neither copies nor
repairs data.

**Protocol version.** The readiness packets carry protocol version 2. A player whose
APIShared build speaks the first, unversioned protocol, or a later one, is named in a message
saying the versions differ, and the load is blocked. A build that cannot read version-2
packets cannot confirm either; the host's 60-second timeout then names the players that did
not confirm.

**Cost.** Nothing runs per tick. Per multiplayer load request, when at least one record is
declared and another player is present, each player reads its save's mod data archive once
and hashes the declared records. On an 8 MiB test save with a 256 KiB declared record this
took about 6 ms, against about 12 ms for the whole-file SHA-256 the agreement already takes
twice per player (`SavegameReadinessProtocolTests`: warm file cache, .NET Framework test host,
not the game's Mono runtime; not a frame-time measurement). Each declared record adds its
owner GUID, identifier and 32 bytes to the readiness packets, about 70 bytes for typical
names; the channel limit allows 64 records with names at their maximum length.

## Implementation

### Record format

Each record is the complete `ModSaveDataAPI` entry for its identifier
([codec](../src/Savegames/VersionedSaveRecordCodec.cs)). All integers are little-endian.

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Signature `APSV` |
| 4 | 2 | Envelope version of the writer (1) |
| 6 | 2 | Oldest envelope reader (1) |
| 8 | 2 | Header size (32) |
| 10 | 2 | Reserved, 0 |
| 12 | 4 | Record version |
| 16 | 4 | Minimum reader version |
| 20 | 4 | Entry count |
| 24 | 4 | Payload length |
| 28 | 4 | CRC-32 of bytes 0 to 27 and 32 to the end |

Entries follow in ordinal name order: a 2-byte name length, the UTF-8 name, a 4-byte data
length and the data. The same state always gives the same bytes. A future writer that changes
this layout or exceeds a version-1 limit must raise the oldest envelope reader, so version-1
readers report `TooNew` and keep the bytes rather than reporting `Corrupt`.

### Script Extender behaviour it relies on

At Script Extender v2.14.1 (`src/SHCDESE.BepInEx/`):

- One handler per identifier; a second registration returns false
  (`API/ModSaveDataAPI.cs:129-133`). Archive entry names are compared ignoring case
  (`:194`, `:248`).
- The save callback runs from the save-file write hook
  (`Detours/BulkMapLoaderDetours.cs:70-108` to `API/GameMapArchiveManagerAPI.cs:361-389`).
  A null or empty result writes nothing (`API/ModSaveDataAPI.cs:184-188`), and the archive
  loaded with the save stays in memory until the map unloads
  (`API/GameMapArchiveManagerAPI.cs:105-111`, `:324-342`), so its entry is carried into the
  next save.
- Loads: `OnLoadSave` and `OnLoadMap` load the archive in both their Pre and Post phase
  (`API/GameMapArchiveManagerAPI.cs:113-143`, raised at
  `Detours/BulkMapLoaderDetours.cs:178-185` and `:355-363`), and each load delivers every stored
  entry (`API/ModSaveDataAPI.cs:228-267`). A load without the entry delivers nothing
  (`:250-254`).
- `ModSaveDataAPI.InitializeSubscribers` (`:80-89`) has no caller, so its `onUnload` callback
  is never invoked.
- An exception from a load event subscriber would make the Extender's detour call the native
  loader again (`Detours/BulkMapLoaderDetours.cs:188-192`, `:367-371`); the binding catches
  everything.

### Load window

[The registry](../src/Savegames/VersionedSaveDataRegistry.cs) opens a window in its
`OnLoadSave`/`OnLoadMap` Pre handler and closes it in the Post handler. Whichever of
APIShared and the Extender's archive loader subscribed first, exactly one of the two
deliveries of a load falls inside that window, so only that one counts and the other is a
duplicate. The window opening drops the previous mission's kept entries; closing it decodes,
migrates and reports each registration once, including `NoRecord`. A map unload inside the
window is part of the load (the game unloads the old map while loading); one outside a load
drops the kept entries, as the Extender drops its archive. The subscriptions start with the
first registration, so the service is dormant until a mod uses it.

The savegame settings load entries (the mission lifecycle's save and `StartSave` hooks with
`SavegameLoadCoordinator`) wrap only save loads, from outside the native load, and agree on
settings before a multiplayer load is released. The Extender delivers stored entries only
inside its `OnLoadSave`/`OnLoadMap` notifications, which also cover map and editor loads, so
those notifications remain the window. The coordinator's confirmed multiplayer save state
classifies the source of an `OnLoadMap` load.

### Tests

`VersionedSaveDataTests` (Core.Tests) cover the round trip, the pinned byte layout, unknown
entries across a downgrade and upgrade, deletion of unwritten known entries, the migration
chain, failed migration, corrupt and too-new records, legacy import, `NoRecord` and mission
boundaries, both subscription orders, isolation and ordering of several mods, every limit on
writer and reader, scope and registration validation, the bounded set of simulation-relevant
declarations, and the single-player to multiplayer-save transition (earlier single-player
state and differing local records do not reject a shared save, the multiplayer load replaces
that state, a different declared record is named). `SavegameRecordComparisonTests`
(Core.Tests) cover every kind of difference, the explanation, peer list validation and the
host's acceptance rules. `VersionedSaveDataRuntimeTests` (APISharedTests) bind to the
installed Script Extender handler table and feed the binding the real event argument types;
`SavegameReadinessProtocolTests` cover the version-2 packet, protocol-1 recognition and reply,
later versions, malformed lists, and digests read from save files built with the Extender's
own archive class. None of them runs the game; in-game checks are listed under
[Public limits](#public-limits).
