# Lord message replacement

This guide is for authors replacing the presentation of an actual Lord message.
It requires APIShared 0.47.0 or later. The service does not enqueue messages or
change diplomacy, goods, commands, native queue contents or gameplay random state.

## Registering a rule

Register on Unity after APIShared readiness with
`LordMessageEvents.TryRegister(ownerGuid, registrationId, pre, post, out registration, out reason, order)`.
Marshal a readiness callback through `ApiShared.ForMod(ownerGuid).Ui` when needed;
`WhenReady` itself is not a thread dispatcher or an availability guarantee.
Check `LordMessageEvents.Available` / `Diagnostic` and the registration result.

Registrations and their handles live until process exit. Retain the handle and
use `SetEnabled` for logical activation; do not dispose hooks at map changes or
plugin startup cleanup. Callbacks run synchronously on Unity, outside registry
locks, without replay. Order is numeric order, ordinal owner GUID, then registration ID.

Pre receives an immutable context identifying this presentation by invocation ID
and header index. `VanillaLord` is present when the audited text index identifies
the original Vanilla speech Lord. `PlayerId` is nullable and is populated only
when the actual text format explicitly contains the player slot. A shared sound,
video name or cached current player never supplies missing identity.

## Keeping or replacing individual parts

Leave a part untouched to preserve the original path or text behaviour. Each of
`TryReplaceAudio`, `TryReplaceVideo` and `TryReplaceText` accepts a typed source:

| Source | Audio | Video | Text |
| --- | --- | --- | --- |
| A `VanillaLordMessage` from `VanillaLordMessages.TryGet(lord, type, ...)` | `LordMessageAudio.FromVanilla` | `LordMessageVideo.FromVanilla` | `LordMessageText.FromVanilla` |
| Private indexed mod resource | `LordMessageAudio.FromModAsset(ownerGuid, relativePath)` | `LordMessageVideo.FromModAsset(ownerGuid, relativePath)` | — |
| Text already localized by the consumer | — | — | `LordMessageText.FromText(text)` |

The three Vanilla references may name different Lords or categories. A Vanilla
reference resolves the installed original media mapping and uses the game's
existing speech-language and UI-language paths. It does not play a hidden sample
message and does not consume synchronized random numbers. Reader availability
is reported separately by `VanillaLordMessages.Available` / `Diagnostic`.

Pre's `Audio`, `Video` and `Text` initially have `LordMessageSourceKind.Original`.
Selected references report `Vanilla`, `ModAsset` or, for supplied text,
`LiteralText`. These kinds are separate from effect-table and play-list IDs;
choosing one field does not retarget either of the other fields.

Own resources must already be indexed under the supplied provider GUID. The
existing Extender loaders handle audio and video; no global filename override is
needed. Prepare `FromModAsset` targets on Unity after resource indexing and before
registration/callbacks. Preparation uses the Extender's internal resource resolvers
and may extract a packaged resource to its cache; `IsReady` records its result.
Retain these targets: selecting them only checks immutable preparation state and
does not query or extract resources. Calling `FromModAsset` in Pre/Post is rejected.
Recreate an unavailable target after its resource has become available.
Audio supports OGG Vorbis and PCM16, 44100 Hz mono/stereo WAV. Video uses
the Extender's existing WEBM/MP4 path. Resource validation is not decoder or
hardware-output acceptance. A resource known to be invalid or missing cannot
replace the current field; decoder failures are not reported as audible output.

Prepare reusable targets outside callbacks. Do not read files, decode assets,
block or change game state in Pre/Post. A successful mutation affects only this
presentation. The last valid replacement wins per part; an invalid source leaves
that part unchanged. A throwing callback rolls back its own changes and private
`State` while later registrations continue. Arguments freeze after Pre, and the
chosen targets remain fixed for that message even if registration activation changes.

Post receives the final `Audio`, `Video` and `Text` targets, original context and
that registration's private `State`. It proves submission to the presentation
path, not successful decoding, a visible frame or audible playback. Callback
failures are counted by `LordMessageEvents.CallbackFailures`.

See the [compiled consumer example](../examples/ThirdPartyMod/LordMessageExample.cs).

## Coverage and compatibility

Troop orders, recruitment and civilian comments belong to the separate
[Unit Voices API](AUDIO.md#unit-voices). Lord-message and unit-voice presentation
cooperate at the shared speech-record site: a recognised complete Lord message
keeps its grouped audio/video/text handling and does not enter unit-voice rules.
The existing SFX API continues to handle combat sounds and other effects.

The service recognises complete Lord message groups emitted by Vanilla:
record 20 (text header), 18 (speech), 22 (controls), then 21 (video), including
the audited optional music/ambient transitions or stop-old-video records before
the final video start. The header's fields 13 and 14 must both identify present
audio/video. Record 21's value zero is the ordinary nonlooping start, not a stop;
negative values stop an old video. Preliminary `IncomingMessage` announcements,
unrelated notifications and ambiguous or incomplete groups pass through unchanged.

The complete native record-production protocol is currently enabled only for the
audited game binary. Its availability is independent of the Vanilla catalog's
table-address pattern fallback: a table match alone does not establish the
message header flags or control-flow sequence of a new game version.

The category and available speaker identity come from that header's text fields.
`speechFileName` and `binkFileName` are single fields of the supplied PlayState,
not unique per-record identities. Their effective values are preserved by `Keep`
and are exposed as original paths, but never used to invent missing speaker data.

Selection happens at actual presentation after Vanilla queueing and Extender
media selection. Original record buffers, text indices, loop/wait flags and the
native queue remain unchanged. Own media references therefore do not traverse
the native queue's small fixed-size pathname buffers.

Own text is bound to the exact message-bar instance and presentation generation.
It is applied after the original Vanilla/Extender text rendering. Any new
message-bar assignment clears the previous binding, including repeated identical
headers. When retiring API-owned text from an already active slot, the next normal
text update is requested so a subsequent unchanged message cannot retain the old
subtitle. Initialization and mission transitions invalidate stale bindings.

`PackagedLordPresentation` retains its complete custom-Lord identity contract.
Lord message replacement does not register a Custom Lord or change its scripts,
name, portrait, metadata or synchronized media selection. Consumers choosing to
replace only generic messages must inspect the effective original audio/video
paths as well as Lord/category; an Extender-authored replacement is not a
placeholder merely because its Vanilla speech Lord is Rat, Snake, Pig or Wolf.

## Local test consumer

The workspace's `LordMessageReplacementTest` is an independent public-API consumer.
It replaces actual generic allied messages of Rat, Snake, Pig and Wolf with
`Taunt1` audio, video and text of the same Lord. Individual messages remain, as
do authored Extender media and Wolf's unrelated generic `KillNpc` message. No
preview, synthetic message, copied game asset or new voice recording is supplied.

The test emits one bounded submission confirmation per replaced Lord/category
pair. Those confirmations and offline tests do not establish listening/video
acceptance. Actual messages, queueing, save loading and host/client presentation
must be checked in the game; the test's deliberately unrelated taunt text is not
production dialogue for goods or assistance requests.
