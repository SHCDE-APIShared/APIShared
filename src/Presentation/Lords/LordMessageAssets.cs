using System;
using System.Reflection;
using System.Threading;
using SHCDESE.API;

namespace APIShared.Presentation
{
    internal static class LordMessageAssets
    {
        private delegate bool Resolve(GameAssetManagerAPI self, string path, out string result);
        private static Resolve audio, video;
        private static Func<string, string, string> qualify;
        private static int qualifierBound;
        private static string unavailable;
        // Fixtures replace only the external resource-index boundary, not target validation/composition.
        internal static Func<string, bool, bool> ResourceAvailable;
        // Only mod assets need these private Extender members. When they change, Vanilla and text
        // replacement stay available and mod asset references simply report IsReady = false.
        internal static string Initialize()
        {
            try { audio = Bind("TryResolveAudioPath"); video = Bind("TryResolveVideoPath"); }
            catch (Exception error) { audio = video = null; unavailable = error.Message; }
            Qualifier();
            return unavailable;
        }
        private static Func<string, string, string> Qualifier()
        {
            if (Volatile.Read(ref qualifierBound) != 0) return qualify;
            try { qualify = BindReference(); }
            catch (Exception error) { qualify = null; unavailable = error.Message; }
            Volatile.Write(ref qualifierBound, 1);
            return qualify;
        }
        private static Func<string, string, string> BindReference()
        {
            MethodInfo method = typeof(GameAssetManagerAPI).GetMethod("CreateModAssetReference", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(string), typeof(string) }, null);
            if (method == null || method.ReturnType != typeof(string)) throw new MissingMethodException("Extender private asset reference contract changed.");
            return (Func<string, string, string>)Delegate.CreateDelegate(typeof(Func<string, string, string>), method);
        }
        private static Resolve Bind(string name)
        {
            MethodInfo method = typeof(GameAssetManagerAPI).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(string).MakeByRefType() }, null);
            if (method == null || method.IsStatic || method.ReturnType != typeof(bool) || !method.GetParameters()[1].IsOut)
                throw new MissingMethodException("Extender Lord message resource resolver changed: " + name);
            return (Resolve)Delegate.CreateDelegate(typeof(Resolve), method);
        }
        // Null when the Extender's private reference helper is unavailable; argument errors still throw.
        internal static string Reference(string owner, string path)
        {
            if (string.IsNullOrWhiteSpace(owner) || owner.IndexOf('\0') >= 0 || string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
                throw new ArgumentException("A provider GUID and relative resource path are required.");
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.IndexOf(':') >= 0)
                throw new ArgumentException("Use an owner-private relative resource path.");
            foreach (string part in normalized.Split('/'))
                if (part.Length == 0 || part == "." || part == "..") throw new ArgumentException("Invalid resource path segment.");
            var qualifier = Qualifier();
            return qualifier == null ? null : qualifier(owner, normalized);
        }
        internal static bool CanUse(LordMessageSourceKind kind, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return false;
            return kind == LordMessageSourceKind.Vanilla || kind == LordMessageSourceKind.ModAsset;
        }
        internal static bool Prepare(string path, bool isVideo)
        {
            if (LordMessageEvents.InCallback) throw new InvalidOperationException("Prepare Lord message assets before Pre/Post callbacks.");
            if (ResourceAvailable == null && !Internal.UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Prepare Lord message mod assets on Unity after APIShared initialization.");
            try {
                var fixture = ResourceAvailable;
                if (fixture != null) return fixture(path, isVideo);
                Resolve resolver = isVideo ? video : audio;
                return resolver != null && resolver(GameAssetManagerAPI.Instance, path, out _);
            } catch { return false; }
        }
    }
}
