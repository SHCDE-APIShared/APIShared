# Shared paint sites

`APIShared.Presentation.PaintEvents` lets several mods change how units, tiles and map
objects are drawn through one APIShared hook per game draw method, instead of each mod
installing its own hook on the same method. Registrations run after the game has drawn,
in a documented order, and use a claim protocol so that two mods replacing the same sprite
do not silently overwrite each other.

Contracts are documented at the declarations in
[PaintEvents.cs](../src/Presentation/Paint/PaintEvents.cs) and
[PaintContracts.cs](../src/Presentation/Paint/PaintContracts.cs). The compiled
[PaintExample](../examples/ThirdPartyMod/PaintExample.cs) shows a registration with a fallback.

## Usage

Register once during plugin startup with your plugin GUID and a stable registration ID.
The first successful registration for a site installs its hook; until then the game method
runs with no APIShared code. Registrations last for the process: return early from the
callback while your feature is off.

```csharp
if (!PaintEvents.TryRegisterBodySprite(MyPlugin.Guid, "unit-art", AfterPaint,
        out string reason, order: -100, files: new[] { myCarrierFile }))
    InstallOwnHook(reason);   // the broker refused or is missing: keep your previous path

static void AfterPaint(ref BodySpritePaint paint)
{
    if (!MyBindings.TryGetArt(paint.Renderer, paint.Image, out Sprite sprite)) return;
    if (!paint.TryClaim()) return;        // a lower order already replaced this paint
    paint.Renderer.sprite = sprite;
}
```

The callback receives the paint by reference and must not keep or copy it. It runs on the
painting thread (the Unity main thread for every current caller), after the game's method and
every hook installed beneath the broker, outside any lock.

### Sites

| Site | Game method | When your callback runs | What you may change | File filter |
|---|---|---|---|---|
| `BodySprite` | `SpriteMapping.SetBodySprite` | After the renderer's sprite, shared material and colour were set, including Script Extender's custom-material hook | The renderer's sprite and material (claim first), colour or other renderer state | Yes |
| `TileGraphic` | `SpriteMapping.setGenericBuildingTileGraphic` | After the tile's image was chosen | `Tile.tileImage` (claim first) | Yes |
| `Chevron` | `SpriteMapping.getChevronImage` | After the chevron sprite was looked up | The returned sprite, through `TryReplace` | Yes |
| `TileColour` | `gameTile.setTileColour` | After the cell colour was written to the tile's tilemap, including Script Extender's visualizers | The cell colour on `Tile.tilemapRef`; claim it for exclusive use | No |
| `PixieLayer`, `FlyLayer`, `OrgLayer`, `BuildingAnimLayer` | `GameMap.addUpdatePixie`, `addUpdateFly`, `addUpdateOrg`, `addUpdateBuildingAnim` | After the draw-list entry was added or updated, outside the update's scope | The entry's renderer (claim first) | Yes |

A file filter lists the sprite files (GM numbers, 0 to 1023) a registration wants. A paint of
any other file never reaches it, and a paint whose file no registration asked for costs one
table lookup. Pass `null` to receive every file, for example to record every paint of a
renderer you track.

### Order and claims

Callbacks run in ascending `order`, then ordinal owner GUID, then registration ID. A
registration added while a paint is being dispatched takes effect from the next paint.

A registration that replaces the result of a paint calls `TryClaim` (`TryReplace` for the
chevron) first and changes the result only when it returns true. The first claim wins;
later registrations see `IsClaimed` and `ClaimedBy` and leave the result alone. Changes that
do not replace the result, such as a tint, need no claim and compose in order. A mod that
deliberately draws over another known mod's result uses `ClaimOver(thatOwnerGuid)`
(`ReplaceOver` for the chevron), which succeeds only while exactly that owner holds the claim.

Give exact-identity art (one unit, one building) a lower order than file-wide or theme art,
so the more specific art claims first. Two registrations of the same owner share the owner's
claim identity.

| Site | Who wins | How later registrations chain |
|---|---|---|
| `BodySprite` | The first claim takes the sprite and material | Tints apply in order on top; `ClaimOver` layers art over a named owner |
| `TileGraphic` | The first claim takes the tile image | Unclaiming registrations still see the final image |
| `Chevron` | The first `TryReplace` takes the returned sprite | `Result` shows the current sprite; `ReplaceOver` replaces a named owner's sprite |
| `TileColour` | No winner by default: each registration reads and writes the current colour in order | A claim marks exclusive use; others should skip while `IsClaimed` |
| Layers | The first claim takes the entry's renderer | As for `BodySprite` |

### Layer scope

Body paints inside a `GameMap.addUpdate*` call can learn which update encloses them through
`BodySpritePaint.Layer` (or `PaintEvents.CurrentLayer`): the layer, object ID, file and image
passed to the update. Only layers with at least one registration are tracked; register the
layer with a null callback when you need only the scope. The scope is per thread and is
restored when the update returns or throws.

### Restoring a native frame

`PaintEvents.CallOriginalBodySprite` paints beneath the broker: the game's method and the hooks
installed before the broker run, registrations are not notified. Use it to give a pooled
renderer its native frame back when your identity leaves it. Calling
`SpriteMapping.SetBodySprite` instead repaints through every registration.

### Failures and refusal

A throwing callback is counted (`GetCallbackFailures`), its own claim (and chevron
replacement) is undone and the next registration runs; writes it made to Unity objects are
not undone. Repeated failures are logged at exponentially spaced counts. An exception from
the game's method propagates and skips the callbacks.

Registration returns false with a reason when the arguments are invalid, the owner/ID pair is
taken, or the site cannot be installed. Installation is refused, and logged as
`PAINT_SITE_REFUSED`, when the method's signature changed or its compiled entry was redirected
by something other than MonoMod. A later registration retries. Keep your own hook as the
fallback for a refusal and for APIShared versions without this broker. `GetStatus` reports
`Dormant`, `Installed` or `Unavailable: reason`.

## Limitations

- Painting is local presentation. Never change simulation state or send packets from a
  callback; peers paint independently.
- The broker does not resolve unit identity. Keep your own renderer bindings, for example from
  Script Extender's unit visual events.
- Callbacks run after the game's method; there is no veto or replacement of the game's paint
  itself.
- There is no unregistration and no replay.
- MonoMod places hooks by installation time: a hook another mod installs on the same method
  after the broker runs around the broker, and one installed before it runs beneath it. Mods
  that keep their own hooks are not ordered against registrations.
- A paint started from inside a callback is dispatched like any other paint. Do not repaint
  the same renderer from its own callback.

## Implementation

Each site is a static class in [PaintSites.cs](../src/Presentation/Paint/PaintSites.cs) with
one MonoMod `Hook` from the game method to a static method. Static hook methods avoid the
per-call target lookup MonoMod generates for instance hooks. The hook calls the original
through a trampoline kept in a static field, reads the site's registration table (one volatile
field, indexed by sprite file, rebuilt and republished on each registration) and returns when
the entry for that file is empty. Otherwise a separate method that is never inlined builds the
paint value on the stack and calls each registration through its delegate. Keeping that value
out of the hook method matters: a struct holding references is zeroed in the prologue of the
method that declares it, on every call.

The claim state inside each paint value stores registration positions as integers; marking the
running registration writes one integer, and only a successful claim remembers the claim it
replaced, which is what a failed callback rolls back. Layer sites save and restore a
thread-static scope around the original call. Nothing in the dispatch path allocates, takes a
lock or touches game state.

### Installation guard

[PaintSiteGuard.cs](../src/Presentation/Paint/PaintSiteGuard.cs) checks a site before its hook
is installed:

1. The method has the exact declaring type, name, static or instance form, return type and
   parameter types the site was written for.
2. The native-site registry seam (`PaintSiteGuard.OwnershipVeto`) has no objection. It is
   unset until a registry can name an exclusive owner of a managed method.
3. The compiled entry is not redirected outside MonoMod. MonoMod hooks on one method compose
   (Script Extender already hooks `SetBodySprite` and `setTileColour`; HarmonyX patches apply
   as MonoMod `ILHook`s), so applied MonoMod detours, counted from MonoMod's detour registry,
   mean the entry is MonoMod's. Without them, on Mono, an entry that starts with a jump was
   written by another patcher, which installing would overwrite: the site is refused. On
   .NET Framework and CoreCLR (test hosts) a method's stable entry can itself be a jump stub,
   so this last check is not applied there.

`PAINT_SITE_READY` logs the MonoMod detour count, the runtime and whether the entry jumps.

### Measurements

`PaintPerformanceTests` binds sites to static stand-ins with the game methods' parameters and
an identical trivial body, and compares them with today's consumer pattern on the same host:
a static MonoMod hook that calls its trampoline and then filters the paint (renderer-binding
dictionary lookup and covered-file check for body sprites, a file/image check for tiles, a
flag and grid check for tile colour). Broker variants run the same filter as registrations.
Paints use a file nobody covers, so the consumer work is the common early return. 64-bit
.NET Framework 4.8.1 test host, 20000 warm-up calls, eleven rounds of 200000 calls with the
variants alternating in rotating order; values are medians in ns per call across three runs.

| Variant | APIShared optimized | APIShared as built today |
|---|---:|---:|
| Plain call, no hook | 1.2 to 1.5 | 2.0 |
| Site bound, no registration (dormant) | 1.3 to 1.7 | 2.0 |
| One consumer hook | 13.3 to 14.4 | 17.9 |
| Broker, one registration for another file | 8.7 to 9.6 | 27.1 |
| Broker, one registration receiving the paint | 29.2 to 30.7 | 82.1 |
| Eight consumer hooks | 167 to 181 | 168 |
| Broker, eight registrations | 102 to 107 | 201 |
| Two tile hooks | 12.9 to 14.5 | 17.1 |
| Broker, two tile registrations (one receives the paint) | 18.6 to 28.0 | 72.5 |
| One tile-colour hook | 11.6 to 12.4 | 13.6 |
| Broker, one tile-colour registration | 24.3 to 27.5 | 88.4 |

Every broker and dormant variant allocated 0 bytes per call after warm-up (minimum of the
rounds, also asserted separately over five samples), and an IL test rejects allocations,
delegates and boxing in the compiled dispatch methods.

On this host the broker costs about 15 ns more than one direct hook for a paint a
registration receives, about 5 ns less for a paint it does not, and about 40 percent less than
eight hooks. "As built today": `APIShared.csproj` sets no `Optimize` property, so builds mark
the assembly with `DebuggableAttribute(DisableOptimizations)` and the .NET Framework JIT
compiles all of APIShared unoptimized; the column shows that effect on the same code.
Whether Unity's Mono honours that attribute was not verified. Mono, the real game methods,
Unity's renderer property costs and real consumer callbacks are outside these measurements;
they are not an FPS measurement. Timing bounds are asserted only for an optimized build
(`-p:Optimize=true`), as regression guards.
