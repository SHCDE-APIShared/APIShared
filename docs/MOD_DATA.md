# Mod data access

Use `APIShared.Content.dll` for game-independent GUID-separated JSON and
`APIShared.dll` for runtime sources. Consumers reference both with `Private=false`
and install the complete central APIShared package. No ExtendedData reference is
needed for a map or explicitly local file read.

## Read a source

Call on the Unity thread after Extender initialization. Prefer an actual successful
mission-start notification for map data; an early load callback can still precede
replacement of the Extender archive. Do not treat a file name as launch confirmation.

```csharp
using APIShared.ModData;

ModDataReadResult map = ModDataAccess.ReadCurrentMapNamespace("Your.Mod.Guid");
ModDataReadResult selected = ModDataAccess.ReadSelectedLordNamespace(playerId, "Your.Mod.Guid");
ModDataReadResult local = ModDataAccess.ReadLocalLordNamespace(lordJsonPath, "Your.Mod.Guid");
if (map.Success) {
    // map.Data is a detached, recursively immutable namespace snapshot.
}
```

The sources have different authority:

| Operation | Meaning |
|---|---|
| ReadCurrentMapNamespace | Read `modmap.json` in the currently loaded in-memory Extender archive, including pending edits |
| ReadSelectedLordNamespace | Ask the registered provider for its currently verified one-based player selection |
| ReadLocalLordNamespace | Explicitly read the `.modlord.json` sidecar beside a supplied `.lordjson` path |

An explicitly local result is not host confirmation. Reading a selected Lord never
searches local files when a provider or host snapshot is unavailable. A provider may
publish a specifically verified local single-player selection. `Source` describes
provenance; `HostDataUnavailable` is distinct from `FileNotFound`, `NamespaceNotFound`,
`InvalidRequest`, `InvalidDocument` and `ReadError`. `HasSelectedLordSource` reports
registration only, not readiness of the current selection.

The service caches no results. Keep each result with its own mission/selection
identity and discard it on end, replacement or selection change. Previously returned
objects remain immutable snapshots; they do not become authoritative for a later game.
No read applies gameplay values, writes files, resolves AIVs or synchronizes peers.

## Bounded sidecar codec

`ModDataDocumentJson` lives in the Content assembly. `Parse` validates every namespace,
including unrequested namespaces, and returns a caller-owned mutable DOM. Defaults
preserve the Lord contract: 64 KiB for raw UTF-8 document bytes, 16 KiB per namespace
as compact UTF-8 JSON. Blank GUID keys, ambiguous casing and non-object namespaces
are rejected. Custom positive limits can be supplied to Parse.

`ReadNamespace` performs bounded document validation and returns the same immutable
result contract as `ModDataNamespaceReader`. `ReadUtf8` accepts a caller-owned stream,
reads at most the document limit plus one byte, decodes strict UTF-8, strips leading
BOM characters and validates. It leaves the stream open and does not open files.
Invalid data/size throws `InvalidDataException`; malformed UTF-8 throws
`DecoderFallbackException`. `ModDataReadResult.Failure` constructs failure results
only; successful immutable snapshots come from namespace readers.

The generic `ModDataNamespaceReader` and current-map reader retain their existing
unrestricted document-size contract. The runtime local-Lord reader and selected
Lord provider enforce the bounded sidecar contract. ExtendedData retains its
separate whole-snapshot/protocol limits and its public Core adapters.

## Provide a confirmed selection

An optional mod may own the single selected-Lord source:

```csharp
IDisposable registration = ModDataAccess.RegisterSelectedLordSource(
    "Your.Provider.Guid", "confirmed-lord-data", (playerId, modGuid) => {
        // Verify the current selection and host transport authority first.
        // Never guess a local replacement when host data is not ready.
        return ModDataReadResult.Failure(ModDataReadStatus.HostDataUnavailable,
            modGuid, "current selection", "Host confirmation is pending.");
    });
```

Retain the token for logical unregistration. Register/dispose on the Unity thread;
duplicate registrations throw instead of silently replacing an owner. The callback
runs synchronously outside registry locks. It must return the requested namespace,
enforce the sidecar limits and invalidate its current selection on lifecycle/reset
events. `ModDataDocumentJson.ReadNamespace` is the shared validator for confirmed
JSON. Provider exceptions become `ReadError`, null means unavailable, recursive
reads are unavailable, and replacement/disposal during a read invalidates its result.
Disposing an old token cannot remove a newer registration. The service itself does
not establish transport authority or add a parallel Lord-selection system.

ExtendedData implements this provider using its existing confirmed host snapshot,
verified legacy local selection and single-player selection. It resets those sources
through its existing map/lobby/save/restart lifecycle and logically unregisters on
initialization-failure cleanup. Its public legacy overloads preserve their content
selection/name/checksum behavior and adapt results from the shared readers.

## Validation limits

Automated tests cover immutable results, bounded streams, invalid UTF-8, exact byte
limits, real installed Extender ZIP reads, absent/changed/recursive providers and
legacy facade/source transitions. These do not replace actual multiplayer host
confirmation, mission transitions or UI/game acceptance. Use the complete matched
development package while this migration is not yet released.
