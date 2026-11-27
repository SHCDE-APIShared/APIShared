using System;
using System.Collections.Generic;
using APIShared.Presentation;
using UnityEngine;

namespace ThirdPartyMod
{
    // Optional presentation: draw replacement art on renderers this mod tracks (for example units it
    // tagged through Script Extender's unit visual events). Unity thread only. When registration fails
    // (an older APIShared, or a refused installation) a real mod keeps its previous SetBodySprite hook.
    internal static class PaintExample
    {
        private static readonly Dictionary<SpriteRenderer, Sprite> replacements = new Dictionary<SpriteRenderer, Sprite>();
        private static bool registered;

        // replacedFile: the sprite file (GM number) this mod's art replaces. Exact-unit art registers
        // with a low order so it claims before file-wide or theme art.
        internal static void Register(string ownerGuid, int replacedFile, Action<string> logger)
        {
            registered = PaintEvents.TryRegisterBodySprite(ownerGuid, "unit-art", AfterBodyPaint,
                out string reason, order: -100, files: new[] { replacedFile });
            if (!registered) logger("Paint broker unavailable; the mod's own hook stays in use: " + reason);
        }

        internal static void Track(SpriteRenderer renderer, Sprite sprite)
        {
            if (renderer != null && sprite != null) replacements[renderer] = sprite;
        }

        // The game may give this renderer to another unit: paint its native frame back beneath the broker.
        internal static void Release(SpriteRenderer renderer, int file, int image, int colour, bool alternateFrame,
            int chopFeet, int transparency)
        {
            if (!replacements.Remove(renderer) || renderer == null || !registered) return;
            PaintEvents.CallOriginalBodySprite(renderer, file, image, colour, alternateFrame, chopFeet, transparency);
        }

        private static void AfterBodyPaint(ref BodySpritePaint paint)
        {
            SpriteRenderer renderer = paint.Renderer;
            if (ReferenceEquals(renderer, null) || !replacements.TryGetValue(renderer, out Sprite sprite)) return;
            if (renderer == null) { replacements.Remove(renderer); return; }
            // Another mod with a lower order already replaced this paint; keep its result.
            if (!paint.TryClaim()) return;
            renderer.sprite = sprite;
        }
    }
}
