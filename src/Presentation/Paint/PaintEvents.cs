using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace APIShared.Presentation
{
    /// <summary>Shared paint broker: one APIShared hook per managed draw method, ordered after-paint callbacks,
    /// and a claim protocol so several mods can replace or adjust what the game drew without each installing
    /// its own hook. See docs/PAINT.md for the per-site composition rules and measurements.</summary>
    /// <remarks>
    /// <para><b>Demand.</b> No site is hooked until its first successful registration; an unregistered site runs
    /// the game's method with no APIShared code in the call. Registrations and hooks last for the process; there is
    /// no unregistration. Ignore calls through your own logical settings.</para>
    /// <para><b>Order.</b> Callbacks run after the original and every hook installed beneath the broker, in
    /// ascending order, then ordinal owner GUID, then registration ID. A registration added during a paint takes
    /// effect from the next paint. MonoMod places hooks installed after the broker outside it: they run around
    /// the whole broker.</para>
    /// <para><b>Claims.</b> A registration that replaces the result (sprite, material, tile image, chevron)
    /// claims it first; the first claim wins and later registrations see <c>IsClaimed</c>/<c>ClaimedBy</c>.
    /// Give exact-identity art (one unit, one building) a lower order than file-wide or theme art so it claims
    /// first. A registration that deliberately draws over a known mod's result uses <c>ClaimOver</c> with that
    /// owner GUID. Adjustments that do not replace the result (tints) compose in order without claiming.</para>
    /// <para><b>Files.</b> Sites with a sprite file (GM number) accept an optional file filter: a registration
    /// with filter receives only those files, and a paint whose file nobody registered costs one table lookup.
    /// Pass null to receive every file.</para>
    /// <para><b>Thread and recursion.</b> Callbacks run synchronously on the painting thread (the Unity main
    /// thread for every current caller), outside locks. A paint started from inside a callback is dispatched
    /// like any other; avoid repainting the same renderer from its own callback.</para>
    /// <para><b>Failures.</b> A throwing callback is counted, its claim (and chevron replacement) is rolled back,
    /// and the next registration runs; renderer writes it made are not undone. Repeated failures are logged at
    /// exponentially spaced counts. An exception from the original propagates and skips the callbacks.</para>
    /// <para><b>Refusal.</b> Installation fails, and the registration returns false with the reason, when the
    /// method's signature changed or its compiled entry was redirected by something other than MonoMod (a
    /// foreign detour APIShared will not overwrite). Keep your own hook as the fallback for that case. A later
    /// registration retries the installation.</para>
    /// <para><b>Multiplayer.</b> Painting is local presentation. Never change simulation state in a callback.</para>
    /// </remarks>
    public static class PaintEvents
    {
        // Binding names the game methods only; nothing is resolved or hooked before a registration.
        static PaintEvents()
        {
            BodySpriteSite<GameSlot>.Bind(PaintBinding.Game(typeof(SpriteMapping), nameof(SpriteMapping.SetBodySprite), true, typeof(void), PaintCalls.BodySignature),
                SpriteMapping.SetBodySprite);
            TileGraphicSite<GameSlot>.Bind(PaintBinding.Game(typeof(SpriteMapping), nameof(SpriteMapping.setGenericBuildingTileGraphic), true, typeof(void), PaintCalls.TileGraphicSignature));
            ChevronSite<GameSlot>.Bind(PaintBinding.Game(typeof(SpriteMapping), nameof(SpriteMapping.getChevronImage), true, typeof(Sprite), PaintCalls.ChevronSignature));
            TileColourSite<GameSlot>.Bind(PaintBinding.Game(typeof(gameTile), "setTileColour", false, typeof(void), PaintCalls.TileColourSignature));
            PixieSite<GameSlot>.Bind(PaintBinding.Game(typeof(GameMap), nameof(GameMap.addUpdatePixie), false, typeof(void), PaintCalls.PixieSignature));
            FlySite<GameSlot>.Bind(PaintBinding.Game(typeof(GameMap), nameof(GameMap.addUpdateFly), false, typeof(void), PaintCalls.FlySignature));
            OrgSite<GameSlot>.Bind(PaintBinding.Game(typeof(GameMap), nameof(GameMap.addUpdateOrg), false, typeof(void), PaintCalls.OrgSignature));
            BuildingAnimSite<GameSlot>.Bind(PaintBinding.Game(typeof(GameMap), nameof(GameMap.addUpdateBuildingAnim), false, typeof(void), PaintCalls.BuildingAnimSignature));
        }

        /// <summary>Registers an after-paint callback for <c>SpriteMapping.SetBodySprite</c> (see the class remarks).</summary>
        /// <param name="ownerGuid">Your plugin GUID; also the claim identity.</param>
        /// <param name="registrationId">Stable ID, unique within your owner GUID.</param>
        /// <param name="afterPaint">Runs after each matching paint.</param>
        /// <param name="reason">Empty on success; otherwise why registration or installation failed.</param>
        /// <param name="order">Ascending dispatch order (default 0).</param>
        /// <param name="files">Sprite files (GM numbers, 0..1023) to receive, or null for every file. Copied.</param>
        /// <returns>False for missing arguments, a duplicate owner/ID pair, an invalid filter or a refused installation.</returns>
        public static bool TryRegisterBodySprite(string ownerGuid, string registrationId, PaintHandler<BodySpritePaint> afterPaint,
            out string reason, int order = 0, int[] files = null) =>
            BodySpriteSite<GameSlot>.Registry.TryRegister(ownerGuid, registrationId, afterPaint, order, files, false, out reason);

        /// <summary>Registers an after-paint callback for <c>SpriteMapping.setGenericBuildingTileGraphic</c>.</summary>
        /// <param name="ownerGuid">Your plugin GUID; also the claim identity.</param>
        /// <param name="registrationId">Stable ID, unique within your owner GUID.</param>
        /// <param name="afterPaint">Runs after each matching tile paint.</param>
        /// <param name="reason">Empty on success; otherwise the reason.</param>
        /// <param name="order">Ascending dispatch order (default 0).</param>
        /// <param name="files">Sprite files to receive, or null for every file. Copied.</param>
        /// <returns>See <see cref="TryRegisterBodySprite"/>.</returns>
        public static bool TryRegisterTileGraphic(string ownerGuid, string registrationId, PaintHandler<TileGraphicPaint> afterPaint,
            out string reason, int order = 0, int[] files = null) =>
            TileGraphicSite<GameSlot>.Registry.TryRegister(ownerGuid, registrationId, afterPaint, order, files, false, out reason);

        /// <summary>Registers a callback for <c>SpriteMapping.getChevronImage</c> that may replace the returned sprite.</summary>
        /// <param name="ownerGuid">Your plugin GUID; also the claim identity.</param>
        /// <param name="registrationId">Stable ID, unique within your owner GUID.</param>
        /// <param name="afterPaint">Runs after each matching query; use <see cref="ChevronPaint.TryReplace"/>.</param>
        /// <param name="reason">Empty on success; otherwise the reason.</param>
        /// <param name="order">Ascending dispatch order (default 0).</param>
        /// <param name="files">Sprite files to receive, or null for every file. Copied.</param>
        /// <returns>See <see cref="TryRegisterBodySprite"/>.</returns>
        public static bool TryRegisterChevron(string ownerGuid, string registrationId, PaintHandler<ChevronPaint> afterPaint,
            out string reason, int order = 0, int[] files = null) =>
            ChevronSite<GameSlot>.Registry.TryRegister(ownerGuid, registrationId, afterPaint, order, files, false, out reason);

        /// <summary>Registers an after-colour callback for <c>gameTile.setTileColour</c>. Every redrawn cell reaches
        /// every registration (there is no file filter), so keep the callback cheap.</summary>
        /// <param name="ownerGuid">Your plugin GUID; also the claim identity.</param>
        /// <param name="registrationId">Stable ID, unique within your owner GUID.</param>
        /// <param name="afterColour">Runs after each cell colour is written.</param>
        /// <param name="reason">Empty on success; otherwise the reason.</param>
        /// <param name="order">Ascending dispatch order (default 0).</param>
        /// <returns>See <see cref="TryRegisterBodySprite"/>.</returns>
        public static bool TryRegisterTileColour(string ownerGuid, string registrationId, PaintHandler<TileColourPaint> afterColour,
            out string reason, int order = 0) =>
            TileColourSite<GameSlot>.Registry.TryRegister(ownerGuid, registrationId, afterColour, order, null, false, out reason);

        /// <summary>Registers for one <c>GameMap.addUpdate*</c> layer update. Installing a layer also makes its
        /// updates visible to nested body paints through <see cref="BodySpritePaint.Layer"/> and
        /// <see cref="CurrentLayer"/>; pass a null callback to get only that scope.</summary>
        /// <param name="layer">The layer update; not <see cref="PaintLayer.None"/>.</param>
        /// <param name="ownerGuid">Your plugin GUID; also the claim identity.</param>
        /// <param name="registrationId">Stable ID, unique within your owner GUID.</param>
        /// <param name="afterUpdate">Runs after each matching update, outside its scope; null for scope only.</param>
        /// <param name="reason">Empty on success; otherwise the reason.</param>
        /// <param name="order">Ascending dispatch order (default 0).</param>
        /// <param name="files">Sprite files to receive, or null for every file. Copied.</param>
        /// <returns>See <see cref="TryRegisterBodySprite"/>; also false for an unknown layer.</returns>
        public static bool TryRegisterLayer(PaintLayer layer, string ownerGuid, string registrationId, PaintHandler<LayerPaint> afterUpdate,
            out string reason, int order = 0, int[] files = null)
        {
            PaintRegistry<LayerPaint> registry = LayerRegistry(layer);
            if (registry == null) { reason = "Unknown paint layer."; return false; }
            return registry.TryRegister(ownerGuid, registrationId, afterUpdate, order, files, true, out reason);
        }

        /// <summary>The tracked layer update running on this thread, or a scope with <see cref="PaintLayer.None"/>.
        /// Only layers with a registration are tracked.</summary>
        public static PaintLayerScope CurrentLayer => PaintScope.Tracked ? PaintScope.Current : default;

        /// <summary>Paints through <c>SpriteMapping.SetBodySprite</c> beneath the broker: the game's method and every
        /// hook installed before the broker run, registrations are not notified. Use it to put a pooled renderer
        /// back to its native frame. Before the site is installed this is the ordinary method call.</summary>
        /// <param name="renderer">The renderer.</param>
        /// <param name="file">Sprite file (GM number).</param>
        /// <param name="image">One-based image.</param>
        /// <param name="colour">Colour index.</param>
        /// <param name="alternateFrame">Alternate frame flag.</param>
        /// <param name="chopFeet">Chop-feet value.</param>
        /// <param name="transparency">Transparency.</param>
        public static void CallOriginalBodySprite(SpriteRenderer renderer, int file, int image, int colour, bool alternateFrame,
            int chopFeet, int transparency) =>
            BodySpriteSite<GameSlot>.CallOriginal(renderer, file, image, colour, alternateFrame, chopFeet, transparency);

        /// <summary>True when the site's hook is installed.</summary>
        /// <param name="site">The paint site.</param>
        public static bool IsInstalled(PaintSite site) => Registry(site)?.IsInstalled ?? false;

        /// <summary>"Dormant" before the first registration, "Installed", or "Unavailable: reason" after a refused
        /// installation; "Unknown" for an undefined site.</summary>
        /// <param name="site">The paint site.</param>
        public static string GetStatus(PaintSite site) => Registry(site)?.Status ?? "Unknown";

        /// <summary>Total isolated callback failures at the site; 0 for an undefined site.</summary>
        /// <param name="site">The paint site.</param>
        public static long GetCallbackFailures(PaintSite site) => Registry(site)?.CallbackFailures ?? 0;

        // APISharedPlugin.Awake: diagnostics only; no hook is installed here.
        internal static void Initialize(ManualLogSource log) => PaintDiagnostics.Log = log;

        private static IPaintRegistry Registry(PaintSite site)
        {
            switch (site)
            {
                case PaintSite.BodySprite: return BodySpriteSite<GameSlot>.Registry;
                case PaintSite.TileGraphic: return TileGraphicSite<GameSlot>.Registry;
                case PaintSite.Chevron: return ChevronSite<GameSlot>.Registry;
                case PaintSite.TileColour: return TileColourSite<GameSlot>.Registry;
                case PaintSite.PixieLayer: return LayerRegistry(PaintLayer.Pixie);
                case PaintSite.FlyLayer: return LayerRegistry(PaintLayer.Fly);
                case PaintSite.OrgLayer: return LayerRegistry(PaintLayer.Org);
                case PaintSite.BuildingAnimLayer: return LayerRegistry(PaintLayer.BuildingAnim);
                default: return null;
            }
        }

        private static PaintRegistry<LayerPaint> LayerRegistry(PaintLayer layer)
        {
            switch (layer)
            {
                case PaintLayer.Pixie: return PixieSite<GameSlot>.Registry;
                case PaintLayer.Fly: return FlySite<GameSlot>.Registry;
                case PaintLayer.Org: return OrgSite<GameSlot>.Registry;
                case PaintLayer.BuildingAnim: return BuildingAnimSite<GameSlot>.Registry;
                default: return null;
            }
        }
    }

    internal static class PaintDiagnostics
    {
        internal static ManualLogSource Log;
        internal static void ReportCallback(string registration, Exception error) =>
            NativeApiLog.Error(Log, "Paint callback failed: " + registration + ": " + error);
        internal static void Ready(string site, MethodBase target, string entry)
        { try { NativeApiLog.Info(Log, "PAINT_SITE_READY site=" + site + ",target=" + target.DeclaringType?.Name + "." + target.Name + "," + entry); } catch { } }
        internal static void Refused(string site, string reason)
        { try { NativeApiLog.Warning(Log, "PAINT_SITE_REFUSED site=" + site + ": " + reason + "; consumers keep their own paths."); } catch { } }
    }
}
