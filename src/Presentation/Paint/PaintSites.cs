using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared.Events;
using UnityEngine;

namespace APIShared.Presentation
{
    // Where each hooked draw method is found and how its call becomes a paint value. Every site follows the
    // same shape: run the original chain beneath the broker, look up the registrations for the call's sprite
    // file, and only when there are any build the paint value. That happens in a separate, never-inlined
    // method: inlined, its struct (which holds references) would be zeroed on every unobserved paint.
    //
    // Sites are static classes closed over a slot marker. Static hook methods avoid the per-call target
    // lookup MonoMod adds for instance hooks (DynamicMethodHelper.GetReference) and keep state in plain static
    // fields. Each closed type has its own statics: GameSlot binds the game's methods, tests bind other slots
    // to stand-in methods with the same parameters and run the same code.
    internal struct GameSlot { }

    internal static class PaintCalls
    {
        internal delegate void Body(SpriteRenderer renderer, int file, int image, int colour, bool altFrame, int chopFeet, int transparency);
        internal delegate void TileGraphic(GameMapTile tile, int file, int image, int light);
        internal delegate Sprite Chevron(GameMapTile tile, int file, int image);
        internal delegate void TileColour(gameTile self, GameMapTile tile, Vector3Int location, int light);
        internal delegate void Pixie(GameMap map, int objectId, int x, int y, int tileX, int tileY, float height, int file, int image, bool hiMode, int transparency, int delay, int colour);
        internal delegate void Fly(GameMap map, int objectId, int x, int y, int tileX, int tileY, float height, int file, int image, int colour, int transparency, int delay, int gameObjectType, int infoType, int infoData);
        internal delegate void Org(GameMap map, int objectId, int x, int y, float height, int file, int image, int colour, int transparency);
        internal delegate void BuildingAnim(GameMap map, int objectId, int x, int y, int tileX, int tileY, int animLayer, int file, int image, int colour, int transparency, int delay, bool hasSubSpecial, int halfPixelX, int halfPixelY);

        // Parameters including the receiver of an instance method.
        internal static readonly Type[] BodySignature = { typeof(SpriteRenderer), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(int), typeof(int) };
        internal static readonly Type[] TileGraphicSignature = { typeof(GameMapTile), typeof(int), typeof(int), typeof(int) };
        internal static readonly Type[] ChevronSignature = { typeof(GameMapTile), typeof(int), typeof(int) };
        internal static readonly Type[] TileColourSignature = { typeof(gameTile), typeof(GameMapTile), typeof(Vector3Int), typeof(int) };
        internal static readonly Type[] PixieSignature = { typeof(GameMap), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(float), typeof(int), typeof(int), typeof(bool), typeof(int), typeof(int), typeof(int) };
        internal static readonly Type[] FlySignature = { typeof(GameMap), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(float), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) };
        internal static readonly Type[] OrgSignature = { typeof(GameMap), typeof(int), typeof(int), typeof(int), typeof(float), typeof(int), typeof(int), typeof(int), typeof(int) };
        internal static readonly Type[] BuildingAnimSignature = { typeof(GameMap), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(int), typeof(int) };
    }

    // The method a slot hooks and the contract it must have. Production bindings name the game's declaring
    // type; test bindings leave it null so a static stand-in with the same parameters is accepted.
    internal sealed class PaintBinding
    {
        private readonly Func<MethodInfo> resolve;
        private readonly Type declaringType;
        private readonly string name;
        private readonly bool isStatic;
        private readonly Type returnType;
        private readonly Type[] signature;
        private PaintBinding(Func<MethodInfo> resolve, Type declaringType, string name, bool isStatic, Type returnType, Type[] signature)
        { this.resolve = resolve; this.declaringType = declaringType; this.name = name; this.isStatic = isStatic; this.returnType = returnType; this.signature = signature; }

        internal static PaintBinding Game(Type type, string name, bool isStatic, Type returnType, Type[] signature)
        {
            Type[] parameters = signature;
            if (!isStatic)
            {
                parameters = new Type[signature.Length - 1];
                Array.Copy(signature, 1, parameters, 0, parameters.Length);
            }
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            return new PaintBinding(() => type.GetMethod(name, any, null, parameters, null), type, name, isStatic, returnType, signature);
        }
        internal static PaintBinding StandIn(Func<MethodInfo> resolve, Type returnType, Type[] signature) =>
            new PaintBinding(resolve, null, null, true, returnType, signature);

        // Returns null once the hook is published, otherwise the refusal. publish receives the trampoline and
        // beforeApply runs after the checks; both before the hook can be entered.
        internal string Install<T>(string site, ManagedInterceptionHook<T> hook, T callback, Action<T> publish, Action beforeApply = null) where T : Delegate
        {
            MethodInfo method = resolve();
            string refusal = PaintSiteGuard.Check(method, declaringType, name, isStatic, returnType, signature, out string entry);
            if (refusal == null)
            {
                try { beforeApply?.Invoke(); hook.Install(method, callback, "Paint." + site, publish); }
                catch (Exception ex) { refusal = "hook installation failed: " + ex.GetType().Name + ": " + ex.Message; }
            }
            if (refusal != null) { PaintDiagnostics.Refused(site, refusal); return refusal; }
            PaintDiagnostics.Ready(site, method, entry);
            return null;
        }
    }

    internal static class BodySpriteSite<TSlot> where TSlot : struct
    {
        private static readonly ManagedInterceptionHook<PaintCalls.Body> hook = new ManagedInterceptionHook<PaintCalls.Body>();
        private static PaintCalls.Body original;
        private static PaintBinding binding;
        private static PaintCalls.Body direct;
        internal static readonly PaintRegistry<BodySpritePaint> Registry = new PaintRegistry<BodySpritePaint>("BodySprite", Install);

        // Before the first registration. direct is the ordinary call used by CallOriginal while unhooked.
        internal static void Bind(PaintBinding target, PaintCalls.Body directCall) { binding = target; direct = directCall; }

        private static string Install() =>
            binding == null ? "site not bound" : binding.Install("BodySprite", hook, Paint, trampoline => original = trampoline);

        private static void Paint(SpriteRenderer renderer, int file, int image, int colour, bool altFrame, int chopFeet, int transparency)
        {
            original(renderer, file, image, colour, altFrame, chopFeet, transparency);
            PaintEntry<BodySpritePaint>[] entries = Registry.ForFile(file);
            if (entries.Length != 0) Notify(entries, renderer, file, image, colour, altFrame, chopFeet, transparency);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Notify(PaintEntry<BodySpritePaint>[] entries, SpriteRenderer renderer, int file, int image,
            int colour, bool altFrame, int chopFeet, int transparency)
        {
            var paint = new BodySpritePaint(renderer, file, image, colour, altFrame, chopFeet, transparency, entries);
            Registry.Notify(entries, ref paint);
        }

        // Beneath the broker: the original and every hook installed before it, without notifications.
        internal static void CallOriginal(SpriteRenderer renderer, int file, int image, int colour, bool altFrame, int chopFeet, int transparency) =>
            (original ?? direct)(renderer, file, image, colour, altFrame, chopFeet, transparency);
    }

    internal static class TileGraphicSite<TSlot> where TSlot : struct
    {
        private static readonly ManagedInterceptionHook<PaintCalls.TileGraphic> hook = new ManagedInterceptionHook<PaintCalls.TileGraphic>();
        private static PaintCalls.TileGraphic original;
        private static PaintBinding binding;
        internal static readonly PaintRegistry<TileGraphicPaint> Registry = new PaintRegistry<TileGraphicPaint>("TileGraphic", Install);
        internal static void Bind(PaintBinding target) => binding = target;

        private static string Install() =>
            binding == null ? "site not bound" : binding.Install("TileGraphic", hook, Paint, trampoline => original = trampoline);

        private static void Paint(GameMapTile tile, int file, int image, int light)
        {
            original(tile, file, image, light);
            PaintEntry<TileGraphicPaint>[] entries = Registry.ForFile(file);
            if (entries.Length != 0) Notify(entries, tile, file, image, light);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Notify(PaintEntry<TileGraphicPaint>[] entries, GameMapTile tile, int file, int image, int light)
        {
            var paint = new TileGraphicPaint(tile, file, image, light, entries);
            Registry.Notify(entries, ref paint);
        }
    }

    internal static class ChevronSite<TSlot> where TSlot : struct
    {
        private static readonly ManagedInterceptionHook<PaintCalls.Chevron> hook = new ManagedInterceptionHook<PaintCalls.Chevron>();
        private static PaintCalls.Chevron original;
        private static PaintBinding binding;
        internal static readonly PaintRegistry<ChevronPaint> Registry = new PaintRegistry<ChevronPaint>("Chevron", Install);
        internal static void Bind(PaintBinding target) => binding = target;

        private static string Install() =>
            binding == null ? "site not bound" : binding.Install("Chevron", hook, Paint, trampoline => original = trampoline);

        private static Sprite Paint(GameMapTile tile, int file, int image)
        {
            Sprite result = original(tile, file, image);
            PaintEntry<ChevronPaint>[] entries = Registry.ForFile(file);
            return entries.Length == 0 ? result : Notify(entries, tile, file, image, result);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Sprite Notify(PaintEntry<ChevronPaint>[] entries, GameMapTile tile, int file, int image, Sprite result)
        {
            var paint = new ChevronPaint(tile, file, image, result, entries);
            Registry.Notify(entries, ref paint);
            return paint.Result;
        }
    }

    internal static class TileColourSite<TSlot> where TSlot : struct
    {
        private static readonly ManagedInterceptionHook<PaintCalls.TileColour> hook = new ManagedInterceptionHook<PaintCalls.TileColour>();
        private static PaintCalls.TileColour original;
        private static PaintBinding binding;
        internal static readonly PaintRegistry<TileColourPaint> Registry = new PaintRegistry<TileColourPaint>("TileColour", Install);
        internal static void Bind(PaintBinding target) => binding = target;

        private static string Install() =>
            binding == null ? "site not bound" : binding.Install("TileColour", hook, Paint, trampoline => original = trampoline);

        private static void Paint(gameTile self, GameMapTile tile, Vector3Int location, int light)
        {
            original(self, tile, location, light);
            PaintEntry<TileColourPaint>[] entries = Registry.ForAll();
            if (entries.Length != 0) Notify(entries, self, tile, location, light);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Notify(PaintEntry<TileColourPaint>[] entries, gameTile self, GameMapTile tile, Vector3Int location, int light)
        {
            var paint = new TileColourPaint(self, tile, location, light, entries);
            Registry.Notify(entries, ref paint);
        }
    }

    // The enclosing layer update of nested body paints. Per thread, saved and restored around each tracked
    // update so nesting and exceptions unwind correctly. Tracked turns true when the first layer site installs.
    internal static class PaintScope
    {
        [ThreadStatic] private static PaintLayerScope current;
        private static volatile bool tracked;
        internal static bool Tracked => tracked;
        internal static PaintLayerScope Current => current;
        internal static void Track() => tracked = true;
        internal static PaintLayerScope Enter(PaintLayer layer, int objectId, int file, int image)
        {
            PaintLayerScope outer = current;
            current = new PaintLayerScope(layer, objectId, file, image);
            return outer;
        }
        internal static void Exit(PaintLayerScope outer) => current = outer;
    }

    // The four GameMap draw-list updates: each has its own hook and registry; they share LayerPaint. The scope
    // is published before the hook so the first tracked update is visible to nested body paints.
    internal static class PixieSite<TSlot> where TSlot : struct
    {
        private static readonly ManagedInterceptionHook<PaintCalls.Pixie> hook = new ManagedInterceptionHook<PaintCalls.Pixie>();
        private static PaintCalls.Pixie original;
        private static PaintBinding binding;
        internal static readonly PaintRegistry<LayerPaint> Registry = new PaintRegistry<LayerPaint>("PixieLayer", Install);
        internal static void Bind(PaintBinding target) => binding = target;

        private static string Install() =>
            binding == null ? "site not bound" : binding.Install("PixieLayer", hook, Update, trampoline => original = trampoline, PaintScope.Track);

        private static void Update(GameMap map, int objectId, int x, int y, int tileX, int tileY, float height, int file, int image, bool hiMode, int transparency, int delay, int colour)
        {
            PaintLayerScope outer = PaintScope.Enter(PaintLayer.Pixie, objectId, file, image);
            try { original(map, objectId, x, y, tileX, tileY, height, file, image, hiMode, transparency, delay, colour); }
            finally { PaintScope.Exit(outer); }
            PaintEntry<LayerPaint>[] entries = Registry.ForFile(file);
            if (entries.Length != 0) Notify(entries, map, objectId, x, y, tileX, tileY, height, file, image, hiMode, transparency, delay, colour);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Notify(PaintEntry<LayerPaint>[] entries, GameMap map, int objectId, int x, int y, int tileX, int tileY, float height, int file, int image, bool hiMode, int transparency, int delay, int colour)
        {
            var paint = new LayerPaint(PaintLayer.Pixie, map, objectId, x, y, tileX, tileY, height, file, image, colour, transparency, delay, entries);
            paint.HiMode = hiMode;
            Registry.Notify(entries, ref paint);
        }
    }

    internal static class FlySite<TSlot> where TSlot : struct
    {
        private static readonly ManagedInterceptionHook<PaintCalls.Fly> hook = new ManagedInterceptionHook<PaintCalls.Fly>();
        private static PaintCalls.Fly original;
        private static PaintBinding binding;
        internal static readonly PaintRegistry<LayerPaint> Registry = new PaintRegistry<LayerPaint>("FlyLayer", Install);
        internal static void Bind(PaintBinding target) => binding = target;

        private static string Install() =>
            binding == null ? "site not bound" : binding.Install("FlyLayer", hook, Update, trampoline => original = trampoline, PaintScope.Track);

        private static void Update(GameMap map, int objectId, int x, int y, int tileX, int tileY, float height, int file, int image, int colour, int transparency, int delay, int gameObjectType, int infoType, int infoData)
        {
            PaintLayerScope outer = PaintScope.Enter(PaintLayer.Fly, objectId, file, image);
            try { original(map, objectId, x, y, tileX, tileY, height, file, image, colour, transparency, delay, gameObjectType, infoType, infoData); }
            finally { PaintScope.Exit(outer); }
            PaintEntry<LayerPaint>[] entries = Registry.ForFile(file);
            if (entries.Length != 0) Notify(entries, map, objectId, x, y, tileX, tileY, height, file, image, colour, transparency, delay, gameObjectType, infoType, infoData);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Notify(PaintEntry<LayerPaint>[] entries, GameMap map, int objectId, int x, int y, int tileX, int tileY, float height, int file, int image, int colour, int transparency, int delay, int gameObjectType, int infoType, int infoData)
        {
            var paint = new LayerPaint(PaintLayer.Fly, map, objectId, x, y, tileX, tileY, height, file, image, colour, transparency, delay, entries);
            paint.GameObjectType = gameObjectType; paint.InfoType = infoType; paint.InfoData = infoData;
            Registry.Notify(entries, ref paint);
        }
    }

    internal static class OrgSite<TSlot> where TSlot : struct
    {
        private static readonly ManagedInterceptionHook<PaintCalls.Org> hook = new ManagedInterceptionHook<PaintCalls.Org>();
        private static PaintCalls.Org original;
        private static PaintBinding binding;
        internal static readonly PaintRegistry<LayerPaint> Registry = new PaintRegistry<LayerPaint>("OrgLayer", Install);
        internal static void Bind(PaintBinding target) => binding = target;

        private static string Install() =>
            binding == null ? "site not bound" : binding.Install("OrgLayer", hook, Update, trampoline => original = trampoline, PaintScope.Track);

        private static void Update(GameMap map, int objectId, int x, int y, float height, int file, int image, int colour, int transparency)
        {
            PaintLayerScope outer = PaintScope.Enter(PaintLayer.Org, objectId, file, image);
            try { original(map, objectId, x, y, height, file, image, colour, transparency); }
            finally { PaintScope.Exit(outer); }
            PaintEntry<LayerPaint>[] entries = Registry.ForFile(file);
            if (entries.Length != 0) Notify(entries, map, objectId, x, y, height, file, image, colour, transparency);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Notify(PaintEntry<LayerPaint>[] entries, GameMap map, int objectId, int x, int y, float height, int file, int image, int colour, int transparency)
        {
            var paint = new LayerPaint(PaintLayer.Org, map, objectId, x, y, 0, 0, height, file, image, colour, transparency, 0, entries);
            Registry.Notify(entries, ref paint);
        }
    }

    internal static class BuildingAnimSite<TSlot> where TSlot : struct
    {
        private static readonly ManagedInterceptionHook<PaintCalls.BuildingAnim> hook = new ManagedInterceptionHook<PaintCalls.BuildingAnim>();
        private static PaintCalls.BuildingAnim original;
        private static PaintBinding binding;
        internal static readonly PaintRegistry<LayerPaint> Registry = new PaintRegistry<LayerPaint>("BuildingAnimLayer", Install);
        internal static void Bind(PaintBinding target) => binding = target;

        private static string Install() =>
            binding == null ? "site not bound" : binding.Install("BuildingAnimLayer", hook, Update, trampoline => original = trampoline, PaintScope.Track);

        private static void Update(GameMap map, int objectId, int x, int y, int tileX, int tileY, int animLayer, int file, int image, int colour, int transparency, int delay, bool hasSubSpecial, int halfPixelX, int halfPixelY)
        {
            PaintLayerScope outer = PaintScope.Enter(PaintLayer.BuildingAnim, objectId, file, image);
            try { original(map, objectId, x, y, tileX, tileY, animLayer, file, image, colour, transparency, delay, hasSubSpecial, halfPixelX, halfPixelY); }
            finally { PaintScope.Exit(outer); }
            PaintEntry<LayerPaint>[] entries = Registry.ForFile(file);
            if (entries.Length != 0) Notify(entries, map, objectId, x, y, tileX, tileY, animLayer, file, image, colour, transparency, delay, hasSubSpecial, halfPixelX, halfPixelY);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Notify(PaintEntry<LayerPaint>[] entries, GameMap map, int objectId, int x, int y, int tileX, int tileY, int animLayer, int file, int image, int colour, int transparency, int delay, bool hasSubSpecial, int halfPixelX, int halfPixelY)
        {
            var paint = new LayerPaint(PaintLayer.BuildingAnim, map, objectId, x, y, tileX, tileY, 0f, file, image, colour, transparency, delay, entries);
            paint.AnimLayer = animLayer; paint.HasSubSpecial = hasSubSpecial; paint.HalfPixelX = halfPixelX; paint.HalfPixelY = halfPixelY;
            Registry.Notify(entries, ref paint);
        }
    }
}
