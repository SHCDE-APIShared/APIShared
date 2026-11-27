using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.Presentation
{
    // Both bridges have the original static signature, preserving the IL stack
    // and all original branches. The broker consumes the tag before the original
    // or a callback can trigger another paint.
    internal static class TileGraphicPassRuntime
    {
        private static readonly object installation = new object();
        private static SafeIlHook hook;
        [ThreadStatic] private static PendingPaint pending;

        internal static bool TryEnable(out string reason)
        {
            lock (installation)
            {
                if (hook != null)
                {
                    reason = hook.IsApplied ? string.Empty : hook.Diagnostic;
                    return hook.IsApplied;
                }
                MethodInfo renderer = typeof(GameMap).GetMethod(nameof(GameMap.processTestMap),
                    new[] { typeof(short[]), typeof(int), typeof(EngineInterface.PlayState), typeof(byte[]) });
                if (renderer == null || renderer.IsStatic || renderer.ReturnType != typeof(void))
                {
                    reason = "The public GameMap renderer signature changed.";
                    return false;
                }
                return SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.Paint.TileGraphicPass",
                    renderer, Apply, PaintDiagnostics.Log, out hook, out reason);
            }
        }

        internal static TileGraphicPass Take(GameMapTile tile, int file, int image, int light)
        {
            TileGraphicPass pass = pending.Pass;
            if (pass == TileGraphicPass.Unknown)
                return pass;
            if (!ReferenceEquals(tile, pending.Tile) || file != pending.File || image != pending.Image || light != pending.Light)
                return TileGraphicPass.Unknown;
            pending = default;
            return pass;
        }

        private static void PaintReference(GameMapTile tile, int file, int image, int light) =>
            Invoke(tile, file, image, light, TileGraphicPass.ConstructionReference, original);

        private static void PaintVisible(GameMapTile tile, int file, int image, int light) =>
            Invoke(tile, file, image, light, TileGraphicPass.VisibleTile, original);

        private static readonly PaintCalls.TileGraphic original = SpriteMapping.setGenericBuildingTileGraphic;

        internal static void Invoke(GameMapTile tile, int file, int image, int light, TileGraphicPass pass,
            PaintCalls.TileGraphic paint)
        {
            PendingPaint previous = pending;
            pending = new PendingPaint(tile, file, image, light, pass);
            try { paint(tile, file, image, light); }
            finally { pending = previous; }
        }

        private readonly struct PendingPaint
        {
            internal readonly GameMapTile Tile;
            internal readonly int File, Image, Light;
            internal readonly TileGraphicPass Pass;
            internal PendingPaint(GameMapTile tile, int file, int image, int light, TileGraphicPass pass)
            { Tile = tile; File = file; Image = image; Light = light; Pass = pass; }
        }

        internal static void Apply(ILContext il)
        {
            var body = il.Body.Instructions;
            var calls = new List<int>();
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i].OpCode == OpCodes.Call && body[i].Operand is MethodReference method &&
                    method.DeclaringType.FullName == typeof(SpriteMapping).FullName &&
                    method.Name == nameof(SpriteMapping.setGenericBuildingTileGraphic))
                {
                    if (method.HasThis || method.ReturnType.MetadataType != MetadataType.Void || method.Parameters.Count != 4 ||
                        method.Parameters[0].ParameterType.FullName != typeof(GameMapTile).FullName)
                        throw Invalid("tile graphic signature");
                    for (int p = 1; p < 4; p++)
                        if (method.Parameters[p].ParameterType.MetadataType != MetadataType.Int32)
                            throw Invalid("tile graphic argument type");
                    calls.Add(i);
                }
            }
            if (calls.Count != 3) throw Invalid("expected three paint call sites");

            int capture = calls[0], visible = calls[1], overlay = calls[2];
            if (capture < 4 || capture + 4 >= visible || visible < 4 || overlay < 6 || overlay + 2 >= body.Count ||
                !body[capture - 4].MatchLdloc(out int tile) ||
                !body[capture - 3].MatchLdloc(out int referenceFile) ||
                !body[capture - 2].MatchLdloc(out int referenceImage) || !body[capture - 1].MatchLdcI4(0) ||
                !SameLocal(body[capture + 1], tile) || !SameLocal(body[capture + 2], tile) ||
                !TileField(body[capture + 3], OpCodes.Ldfld, "tileImage", typeof(UnityEngine.Sprite)) ||
                !TileField(body[capture + 4], OpCodes.Stfld, "constructionOrigImage", typeof(UnityEngine.Sprite)) ||
                !RecordWordStored(body, capture, referenceFile, 15) || !RecordWordStored(body, capture, referenceImage, 16))
                throw Invalid("construction reference capture");

            if (!SameLocal(body[visible - 4], tile) || !body[visible - 3].MatchLdloc(out int visibleFile) ||
                !body[visible - 2].MatchLdloc(out int visibleImage) || !body[visible - 1].MatchLdloc(out int light) ||
                !SameLocal(body[visible + 1], tile) || !TileField(body[visible + 2], OpCodes.Ldfld, "floatColour", typeof(int)) ||
                !RecordWordStored(body, visible, visibleFile, 3) || !RecordWordStored(body, visible, visibleImage, 4) ||
                !RecordWordStored(body, visible, light, 6))
                throw Invalid("visible tile");

            if (!SameLocal(body[overlay - 6], tile) || !body[overlay - 5].MatchLdloc(out int overlayFile) ||
                !body[overlay - 4].MatchLdloc(out _) || !body[overlay - 3].MatchLdcI4(9) ||
                body[overlay - 2].OpCode != OpCodes.Add || !SameLocal(body[overlay - 1], light) ||
                !SameLocal(body[overlay + 1], tile) || !TileField(body[overlay + 2], OpCodes.Ldfld, "lastChevronFile", typeof(int)) ||
                !RecordWordStored(body, overlay, overlayFile, 11))
                throw Invalid("visible overlay");

            const BindingFlags privateStatic = BindingFlags.NonPublic | BindingFlags.Static;
            var captureBridge = il.Import(typeof(TileGraphicPassRuntime).GetMethod(nameof(PaintReference), privateStatic));
            var visibleBridge = il.Import(typeof(TileGraphicPassRuntime).GetMethod(nameof(PaintVisible), privateStatic));
            body[capture].Operand = captureBridge;
            body[visible].Operand = visibleBridge;
            body[overlay].Operand = visibleBridge;
        }

        private static bool RecordWordStored(IList<Instruction> body, int before, int variable, int word)
        {
            for (int i = before - 1; i >= 7; i--)
            {
                if (!body[i].MatchStloc(out int stored) || variable != stored) continue;
                return body[i - 7].MatchLdarg(1) && body[i - 6].MatchLdloc(out _) && body[i - 5].MatchLdcI4(21) &&
                    body[i - 4].OpCode == OpCodes.Mul && body[i - 3].MatchLdcI4(word) &&
                    body[i - 2].OpCode == OpCodes.Add && body[i - 1].OpCode == OpCodes.Ldelem_I2;
            }
            return false;
        }

        private static bool SameLocal(Instruction instruction, int expected) =>
            instruction.MatchLdloc(out int actual) && actual == expected;

        private static bool TileField(Instruction instruction, OpCode opcode, string name, Type type) =>
            instruction.OpCode == opcode && instruction.Operand is FieldReference field &&
            field.DeclaringType.FullName == typeof(GameMapTile).FullName && field.Name == name &&
            field.FieldType.FullName == type.FullName;

        private static InvalidOperationException Invalid(string contract) =>
            new InvalidOperationException("GameMap tile graphic call-site contract changed: " + contract + ".");
    }
}
