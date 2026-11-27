using UnityEngine;

namespace APIShared.Presentation
{
    /// <summary>Managed draw methods shared through <see cref="PaintEvents"/>. Each has at most one APIShared
    /// hook, installed at its first successful registration; a site without registrations is not hooked.</summary>
    public enum PaintSite
    {
        /// <summary><c>SpriteMapping.SetBodySprite</c>: body sprites of units, animals, editor ghosts and other
        /// renderers the game paints by sprite file (GM) and image.</summary>
        BodySprite,
        /// <summary><c>SpriteMapping.setGenericBuildingTileGraphic</c>: the image of a building or ground tile.</summary>
        TileGraphic,
        /// <summary><c>SpriteMapping.getChevronImage</c>: the chevron sprite returned for a tile.</summary>
        Chevron,
        /// <summary><c>gameTile.setTileColour</c>: the colour of each map tile cell the tilemap redraws.</summary>
        TileColour,
        /// <summary><c>GameMap.addUpdatePixie</c>.</summary>
        PixieLayer,
        /// <summary><c>GameMap.addUpdateFly</c>.</summary>
        FlyLayer,
        /// <summary><c>GameMap.addUpdateOrg</c>.</summary>
        OrgLayer,
        /// <summary><c>GameMap.addUpdateBuildingAnim</c>.</summary>
        BuildingAnimLayer
    }

    /// <summary>The <c>GameMap.addUpdate*</c> draw-list update a paint belongs to.</summary>
    public enum PaintLayer
    {
        /// <summary>No tracked layer update encloses the paint.</summary>
        None,
        /// <summary><c>GameMap.addUpdatePixie</c>.</summary>
        Pixie,
        /// <summary><c>GameMap.addUpdateFly</c>.</summary>
        Fly,
        /// <summary><c>GameMap.addUpdateOrg</c>.</summary>
        Org,
        /// <summary><c>GameMap.addUpdateBuildingAnim</c>.</summary>
        BuildingAnim
    }

    /// <summary>Receives one completed paint by reference. The value is valid only during the call: do not
    /// keep or copy it to claim later. Runs synchronously on the painting thread (the Unity main thread for
    /// every current caller), after the game's method and every hook installed beneath the broker.</summary>
    /// <typeparam name="TPaint">The site's paint value.</typeparam>
    /// <param name="paint">The request, the enclosing layer and the claim state shared by all registrations
    /// of this call.</param>
    public delegate void PaintHandler<TPaint>(ref TPaint paint) where TPaint : struct;

    /// <summary>The layer update that encloses a paint. Only layers with at least one registration are tracked;
    /// see <see cref="PaintEvents.TryRegisterLayer"/>.</summary>
    public readonly struct PaintLayerScope
    {
        internal PaintLayerScope(PaintLayer layer, int objectId, int file, int image)
        { Layer = layer; ObjectId = objectId; File = file; Image = image; }
        /// <summary>The enclosing update, or <see cref="PaintLayer.None"/>.</summary>
        public PaintLayer Layer { get; }
        /// <summary>The object ID passed to the enclosing update; 0 without one.</summary>
        public int ObjectId { get; }
        /// <summary>The sprite file (GM) passed to the enclosing update; 0 without one.</summary>
        public int File { get; }
        /// <summary>The image passed to the enclosing update; 0 without one.</summary>
        public int Image { get; }
        /// <summary>True inside a tracked layer update.</summary>
        public bool IsActive => Layer != PaintLayer.None;
    }

    // Lets the shared dispatcher mark the running registration and roll back a failed callback's claim
    // without boxing: generic dispatch calls these through a constrained call on the struct.
    internal interface IPaintNotification
    {
        void Enter(int registration);
        void Rollback();
    }

    // The claim protocol shared by every paint value. Registrations are 1-based indexes into the dispatched
    // list, so marking the caller writes one integer; 0 means none. A claim remembers the claim it replaced,
    // so a failed callback's claim can be undone without a checkpoint per callback.
    internal struct PaintClaimState
    {
        private readonly PaintEntry[] registrations;
        private int caller, claim, replaced;
        internal PaintClaimState(PaintEntry[] registrations) { this.registrations = registrations; caller = claim = replaced = 0; }
        internal bool IsClaimed => claim != 0;
        internal string ClaimedBy => claim == 0 ? null : registrations[claim - 1].Owner;
        internal bool TryClaim()
        {
            if (claim != 0 || caller == 0) return false;
            replaced = 0; claim = caller; return true;
        }
        internal bool ClaimOver(string currentOwner)
        {
            if (caller == 0 || claim == 0 || claim == caller ||
                !string.Equals(registrations[claim - 1].Owner, currentOwner, System.StringComparison.Ordinal)) return false;
            replaced = claim; claim = caller; return true;
        }
        internal void Enter(int registration) => caller = registration;
        // Only the failing registration's own claim is undone; it can have made at most one.
        internal bool Rollback()
        {
            if (claim != caller || caller == 0) return false;
            claim = replaced; replaced = 0; return true;
        }
    }

    /// <summary>One completed <c>SpriteMapping.SetBodySprite</c> call. The game has already set the renderer's
    /// sprite, shared material and colour from the request, and so has every hook installed beneath the broker
    /// (for example Script Extender's custom-material hook).</summary>
    /// <remarks>To replace the drawn sprite or material, call <see cref="TryClaim"/> first and write the renderer
    /// only if it returned true. Ascending registration order decides who claims first; a registration that
    /// deliberately draws over a known mod's result uses <see cref="ClaimOver"/> with that mod's owner GUID.
    /// Changes that do not replace the result (for example a tint) need no claim. Unit identity is not
    /// resolved here: keep your own renderer bindings.</remarks>
    public struct BodySpritePaint : IPaintNotification
    {
        private PaintClaimState claim;
        internal BodySpritePaint(SpriteRenderer renderer, int file, int image, int colour, bool alternateFrame,
            int chopFeet, int transparency, PaintEntry[] registrations)
        {
            Renderer = renderer; File = file; Image = image; Colour = colour; AlternateFrame = alternateFrame;
            ChopFeet = chopFeet; Transparency = transparency;
            claim = new PaintClaimState(registrations);
        }
        /// <summary>The painted renderer. Unity objects may be destroyed; test them before use.</summary>
        public SpriteRenderer Renderer { get; }
        /// <summary>The requested sprite file (GM number).</summary>
        public int File { get; }
        /// <summary>The requested one-based image within the file.</summary>
        public int Image { get; }
        /// <summary>The requested colour index.</summary>
        public int Colour { get; }
        /// <summary>Whether the alternate frame was requested.</summary>
        public bool AlternateFrame { get; }
        /// <summary>The requested chop-feet value.</summary>
        public int ChopFeet { get; }
        /// <summary>The requested transparency.</summary>
        public int Transparency { get; }
        /// <summary>The tracked layer update that encloses this paint, if any. Read it during the callback.</summary>
        public PaintLayerScope Layer => PaintScope.Tracked ? PaintScope.Current : default;
        /// <summary>True when a registration claimed the result of this paint.</summary>
        public bool IsClaimed => claim.IsClaimed;
        /// <summary>The owner GUID that claimed the result, or null.</summary>
        public string ClaimedBy => claim.ClaimedBy;
        /// <summary>Claims the result for your owner GUID if no registration claimed it.</summary>
        public bool TryClaim() => claim.TryClaim();
        /// <summary>Takes over a claim held by exactly <paramref name="currentOwnerGuid"/>; false if the result is
        /// unclaimed or claimed by anyone else.</summary>
        public bool ClaimOver(string currentOwnerGuid) => claim.ClaimOver(currentOwnerGuid);
        void IPaintNotification.Enter(int registration) => claim.Enter(registration);
        void IPaintNotification.Rollback() => claim.Rollback();
    }

    /// <summary>One completed <c>SpriteMapping.setGenericBuildingTileGraphic</c> call; the game has set the
    /// tile's image. To replace <c>Tile.tileImage</c>, claim first as described for <see cref="BodySpritePaint"/>.</summary>
    public struct TileGraphicPaint : IPaintNotification
    {
        private PaintClaimState claim;
        internal TileGraphicPaint(GameMapTile tile, int file, int image, int light, PaintEntry[] registrations)
        { Tile = tile; File = file; Image = image; Light = light; claim = new PaintClaimState(registrations); }
        /// <summary>The painted tile.</summary>
        public GameMapTile Tile { get; }
        /// <summary>The requested sprite file (GM number).</summary>
        public int File { get; }
        /// <summary>The requested image within the file.</summary>
        public int Image { get; }
        /// <summary>The requested light value.</summary>
        public int Light { get; }
        /// <summary>True when a registration claimed the tile image.</summary>
        public bool IsClaimed => claim.IsClaimed;
        /// <summary>The owner GUID that claimed the tile image, or null.</summary>
        public string ClaimedBy => claim.ClaimedBy;
        /// <summary>Claims the tile image for your owner GUID if no registration claimed it.</summary>
        public bool TryClaim() => claim.TryClaim();
        /// <summary>Takes over a claim held by exactly <paramref name="currentOwnerGuid"/>.</summary>
        public bool ClaimOver(string currentOwnerGuid) => claim.ClaimOver(currentOwnerGuid);
        void IPaintNotification.Enter(int registration) => claim.Enter(registration);
        void IPaintNotification.Rollback() => claim.Rollback();
    }

    /// <summary>One completed <c>SpriteMapping.getChevronImage</c> call. The broker returns <see cref="Result"/>
    /// to the game's caller after the last registration.</summary>
    public struct ChevronPaint : IPaintNotification
    {
        private PaintClaimState claim;
        private Sprite result, replacedResult;
        internal ChevronPaint(GameMapTile tile, int file, int image, Sprite nativeResult, PaintEntry[] registrations)
        {
            Tile = tile; File = file; Image = image; NativeResult = result = nativeResult;
            claim = new PaintClaimState(registrations); replacedResult = null;
        }
        /// <summary>The tile whose chevron is requested.</summary>
        public GameMapTile Tile { get; }
        /// <summary>The requested sprite file (GM number).</summary>
        public int File { get; }
        /// <summary>The requested image within the file.</summary>
        public int Image { get; }
        /// <summary>The sprite returned beneath the broker.</summary>
        public Sprite NativeResult { get; }
        /// <summary>The sprite that will be returned: <see cref="NativeResult"/> unless a registration replaced it.</summary>
        public Sprite Result => result;
        /// <summary>True when a registration replaced the result.</summary>
        public bool IsClaimed => claim.IsClaimed;
        /// <summary>The owner GUID that replaced the result, or null.</summary>
        public string ClaimedBy => claim.ClaimedBy;
        /// <summary>Replaces the result if no registration has; false otherwise.</summary>
        public bool TryReplace(Sprite sprite)
        {
            if (!claim.TryClaim()) return false;
            replacedResult = result; result = sprite; return true;
        }
        /// <summary>Replaces a result already replaced by exactly <paramref name="currentOwnerGuid"/>.</summary>
        public bool ReplaceOver(string currentOwnerGuid, Sprite sprite)
        {
            if (!claim.ClaimOver(currentOwnerGuid)) return false;
            replacedResult = result; result = sprite; return true;
        }
        void IPaintNotification.Enter(int registration) => claim.Enter(registration);
        void IPaintNotification.Rollback() { if (claim.Rollback()) result = replacedResult; }
    }

    /// <summary>One completed <c>gameTile.setTileColour</c> call; the game (and every hook beneath the broker,
    /// including Script Extender's tile visualizers) has written the cell colour to <c>Tile.tilemapRef</c>.
    /// Registrations compose by reading and writing that colour in order. A registration that needs the
    /// colour exclusively (for example a debug overlay) claims it; others should then leave it alone.</summary>
    public struct TileColourPaint : IPaintNotification
    {
        private PaintClaimState claim;
        internal TileColourPaint(gameTile source, GameMapTile tile, Vector3Int location, int light, PaintEntry[] registrations)
        { Source = source; Tile = tile; Location = location; Light = light; claim = new PaintClaimState(registrations); }
        /// <summary>The tile asset whose <c>GetTileData</c> requested the colour.</summary>
        public gameTile Source { get; }
        /// <summary>The map tile.</summary>
        public GameMapTile Tile { get; }
        /// <summary>The tilemap cell.</summary>
        public Vector3Int Location { get; }
        /// <summary>The requested light value.</summary>
        public int Light { get; }
        /// <summary>True when a registration claimed the colour.</summary>
        public bool IsClaimed => claim.IsClaimed;
        /// <summary>The owner GUID that claimed the colour, or null.</summary>
        public string ClaimedBy => claim.ClaimedBy;
        /// <summary>Claims the colour for your owner GUID if no registration claimed it.</summary>
        public bool TryClaim() => claim.TryClaim();
        /// <summary>Takes over a claim held by exactly <paramref name="currentOwnerGuid"/>.</summary>
        public bool ClaimOver(string currentOwnerGuid) => claim.ClaimOver(currentOwnerGuid);
        void IPaintNotification.Enter(int registration) => claim.Enter(registration);
        void IPaintNotification.Rollback() => claim.Rollback();
    }

    /// <summary>One completed <c>GameMap.addUpdate*</c> call. Members that the layer's method does not take
    /// are 0 or false: only Pixie has <see cref="HiMode"/>; only Fly has <see cref="GameObjectType"/>,
    /// <see cref="InfoType"/> and <see cref="InfoData"/>; only BuildingAnim has <see cref="AnimLayer"/>,
    /// <see cref="HasSubSpecial"/>, <see cref="HalfPixelX"/> and <see cref="HalfPixelY"/>; Org has no tile
    /// coordinates or delay. To replace what the update drew, claim first as for <see cref="BodySpritePaint"/>.</summary>
    public struct LayerPaint : IPaintNotification
    {
        private PaintClaimState claim;
        internal LayerPaint(PaintLayer layer, GameMap map, int objectId, int x, int y, int tileX, int tileY, float height,
            int file, int image, int colour, int transparency, int delay, PaintEntry[] registrations)
        {
            Layer = layer; Map = map; ObjectId = objectId; X = x; Y = y; TileX = tileX; TileY = tileY; Height = height;
            File = file; Image = image; Colour = colour; Transparency = transparency; Delay = delay;
            HiMode = false; GameObjectType = InfoType = InfoData = AnimLayer = HalfPixelX = HalfPixelY = 0; HasSubSpecial = false;
            claim = new PaintClaimState(registrations);
        }
        /// <summary>The updated layer.</summary>
        public PaintLayer Layer { get; }
        /// <summary>The map that received the update.</summary>
        public GameMap Map { get; }
        /// <summary>The object ID of the updated draw-list entry.</summary>
        public int ObjectId { get; }
        /// <summary>The requested screen X.</summary>
        public int X { get; }
        /// <summary>The requested screen Y.</summary>
        public int Y { get; }
        /// <summary>The requested tile X; 0 for Org.</summary>
        public int TileX { get; }
        /// <summary>The requested tile Y; 0 for Org.</summary>
        public int TileY { get; }
        /// <summary>The requested height above ground; 0 for BuildingAnim.</summary>
        public float Height { get; }
        /// <summary>The requested sprite file (GM number).</summary>
        public int File { get; }
        /// <summary>The requested image within the file.</summary>
        public int Image { get; }
        /// <summary>The requested colour index.</summary>
        public int Colour { get; }
        /// <summary>The requested transparency.</summary>
        public int Transparency { get; }
        /// <summary>The requested layer delay; 0 for Org.</summary>
        public int Delay { get; }
        /// <summary>Pixie only: the high-detail flag.</summary>
        public bool HiMode { get; internal set; }
        /// <summary>Fly only: the game object type.</summary>
        public int GameObjectType { get; internal set; }
        /// <summary>Fly only: the info type.</summary>
        public int InfoType { get; internal set; }
        /// <summary>Fly only: the info data.</summary>
        public int InfoData { get; internal set; }
        /// <summary>BuildingAnim only: the animation layer.</summary>
        public int AnimLayer { get; internal set; }
        /// <summary>BuildingAnim only: the sub-special flag.</summary>
        public bool HasSubSpecial { get; internal set; }
        /// <summary>BuildingAnim only: the half-pixel X offset.</summary>
        public int HalfPixelX { get; internal set; }
        /// <summary>BuildingAnim only: the half-pixel Y offset.</summary>
        public int HalfPixelY { get; internal set; }
        /// <summary>True when a registration claimed what this update drew.</summary>
        public bool IsClaimed => claim.IsClaimed;
        /// <summary>The owner GUID that claimed this update, or null.</summary>
        public string ClaimedBy => claim.ClaimedBy;
        /// <summary>Claims this update's result for your owner GUID if no registration claimed it.</summary>
        public bool TryClaim() => claim.TryClaim();
        /// <summary>Takes over a claim held by exactly <paramref name="currentOwnerGuid"/>.</summary>
        public bool ClaimOver(string currentOwnerGuid) => claim.ClaimOver(currentOwnerGuid);
        void IPaintNotification.Enter(int registration) => claim.Enter(registration);
        void IPaintNotification.Rollback() => claim.Rollback();
    }
}
