using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using R3;
using RedBird.Abstractions.Hooks;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;

namespace APIShared
{
    // Foreign-patch detection for APIShared's patches in CrusaderDE.dll. The Script Extender takes its
    // load snapshot (CrusaderLibraryLoadContext.Region) before any plugin writes code, so a live byte
    // inside a span that differs from that snapshot belongs to an earlier patch: the Script Extender or
    // another mod. RedBird does not check this: an inline hook would relocate a foreign jump as game
    // code, and a detour would overwrite it.
    // - Install: RequireUnpatched compares every prepared span (hook TargetAddress plus its displaced
    //   or overwritten length) with the snapshot before the first byte is written. A deviation logs an
    //   Error with feature, RVA, expected/live bytes and a jump/module hint, then throws, so only that
    //   feature stays Vanilla. A foreign patch directly before or behind the span is allowed (Info).
    // - Later: Watch records the bytes a span holds after publication; Recheck compares them again at
    //   every mission and editor start and logs each new deviation once as an Error. Nothing is ever
    //   rewritten or undone.
    // Unbound (offline fixtures that never run LibraryLoaded) every call is a no-op. Install-time and
    // mission-start cost only: a few byte compares per span, no scans; no gameplay hot path calls this.
    internal static class NativeForeignPatchGuard
    {
        private static readonly object Sync = new object();
        private static Binding binding;
        private static IDisposable recheckSubscription;

        // LibraryLoaded, before APIShared's first native installation: the Script Extender's snapshot.
        // The region must hold the unpatched image at its live base address (fixtures use a copy).
        internal static void Bind(ScanRegion snapshot, ManualLogSource log)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            Volatile.Write(ref binding, new Binding(snapshot, log));
        }

        internal static bool IsBound => Volatile.Read(ref binding) != null;

        internal static int WatchedCount
        {
            get
            {
                Binding current = Volatile.Read(ref binding);
                if (current == null) return 0;
                lock (Sync) return current.Watched.Count;
            }
        }

        // After the mission lifecycle is published. Rooted for the process; a missing lifecycle leaves
        // the install-time check intact and only loses the later recheck (reported as an Error).
        internal static void SubscribeRecheck(ManualLogSource log)
        {
            lock (Sync)
            {
                if (recheckSubscription != null) return;
                recheckSubscription = Internal.MissionEvents.Started.Subscribe(_ => Recheck());
            }
            NativeApiLog.Info(log, "NATIVE_FOREIGN_PATCH_WATCH: published APIShared spans are rechecked at every mission and editor start.");
        }

        // Label for log lines: the owning class (from the validation callback or callback owner) and
        // RedBird's transaction name (the installer's member name).
        internal static string FeatureOf(HookTransaction transaction, object callbackOwner, Delegate validate)
        {
            Type owner = validate?.Method.DeclaringType ?? callbackOwner as Type;
            if (owner == null && callbackOwner != null && !(callbackOwner is HookTransaction) && !IsHandle(callbackOwner.GetType()))
                owner = callbackOwner.GetType();
            while (owner != null && owner.DeclaringType != null && owner.IsDefined(typeof(CompilerGeneratedAttribute), false))
                owner = owner.DeclaringType;
            string member = transaction?.Name;
            if (owner == null) return string.IsNullOrEmpty(member) ? "APIShared native hook" : member;
            return string.IsNullOrEmpty(member) ? owner.Name : owner.Name + "." + member;
        }

        // A RedBird handle names no feature; its owner is unknown here.
        private static bool IsHandle(Type type)
        {
            if (type.IsArray) type = type.GetElementType();
            for (; type != null; type = type.BaseType)
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RedBird.Abstractions.Hooks.Transaction.HookHandle<>))
                    return true;
            return false;
        }

        // Prepared, not yet enabled hooks: throws (after an Error) if any span already differs from the snapshot.
        internal static void RequireUnpatched(IReadOnlyList<IHook> hooks, string feature)
        {
            Binding current = Volatile.Read(ref binding);
            if (current == null || hooks == null) return;
            foreach (IHook hook in hooks) RequireUnpatched(current, hook, feature);
        }

        internal static void RequireUnpatched(IHook hook, string feature)
        {
            Binding current = Volatile.Read(ref binding);
            if (current != null) RequireUnpatched(current, hook, feature);
        }

        // A raw byte patch (for example a CALL-site rewrite) of length bytes at address.
        internal static void RequireUnpatched(ulong address, int length, string name, string feature)
        {
            Binding current = Volatile.Read(ref binding);
            if (current != null) RequireUnpatched(current, address, length, name, feature);
        }

        // After successful publication and validation. Never throws into the publisher.
        internal static void Watch(IReadOnlyList<IHook> hooks, string feature)
        {
            if (hooks == null || !IsBound) return;
            foreach (IHook hook in hooks) Watch(hook, feature);
        }

        internal static void Watch(IHook hook, string feature)
        {
            Binding current = Volatile.Read(ref binding);
            if (current == null || hook == null) return;
            try
            {
                if (!hook.IsInstalled) throw new InvalidOperationException(hook.Name + " is not enabled.");
                Watch(current, hook.TargetAddress, SpanLength(hook, feature), hook.Name, feature);
            }
            catch (Exception error) { WatchFailed(current, feature, hook.Name, error); }
        }

        internal static void Watch(ulong address, int length, string name, string feature)
        {
            Binding current = Volatile.Read(ref binding);
            if (current == null) return;
            try { Watch(current, address, length, name, feature); }
            catch (Exception error) { WatchFailed(current, feature, name, error); }
        }

        // Compares every published span with its recorded bytes. Logs each new deviation once as an Error
        // and returns how many were new. Mission/editor start; never throws into the lifecycle publisher.
        internal static int Recheck()
        {
            Binding current = Volatile.Read(ref binding);
            if (current == null) return 0;
            int reported = 0;
            try
            {
                WatchedSite[] sites;
                lock (Sync) sites = current.Watched.ToArray();
                foreach (WatchedSite site in sites)
                {
                    byte[] live;
                    try { live = ReadLive(site.Address, site.Published.Length); }
                    catch (Exception error)
                    {
                        Error(current, "NATIVE_FOREIGN_PATCH_LATE: " + site.Feature + " (" + site.Name + "): RVA 0x" + Hex(site.Rva) +
                            " could not be read: " + error.Message);
                        continue;
                    }
                    int difference = FirstDifference(site.Published, live);
                    if (difference < 0) { site.Reported = null; continue; }
                    if (site.Reported != null && FirstDifference(site.Reported, live) < 0) continue;
                    site.Reported = live;
                    reported++;
                    Error(current, "NATIVE_FOREIGN_PATCH_LATE: " + site.Feature + " (" + site.Name + ") was overwritten after APIShared " +
                        "published it; it can run wrongly or crash until the other patch is removed. Nothing is rewritten. RVA 0x" +
                        Hex(site.Rva) + " (" + site.Published.Length.ToString(CultureInfo.InvariantCulture) + " bytes, first difference at +" +
                        difference.ToString(CultureInfo.InvariantCulture) + "): published " + Hex(site.Published) + ", live " + Hex(live) +
                        ". " + DescribeJump(live, difference, site.Address));
                }
            }
            catch (Exception error) { Error(current, "NATIVE_FOREIGN_PATCH_LATE: recheck failed: " + error); }
            return reported;
        }

        private static void RequireUnpatched(Binding current, IHook hook, string feature)
        {
            if (hook == null) throw new ArgumentNullException(nameof(hook));
            if (hook.IsInstalled)
                throw new InvalidOperationException(feature + ": " + hook.Name + " is already enabled; check it before Enable.");
            RequireUnpatched(current, hook.TargetAddress, SpanLength(hook, feature), hook.Name, feature);
        }

        private static void RequireUnpatched(Binding current, ulong address, int length, string name, string feature)
        {
            ScanRegion image = current.Image;
            int offset = OffsetOf(current, address, length, name, feature);
            byte[] expected = image.Span.Slice(offset, length).ToArray();
            byte[] live = ReadLive(address, length);
            int difference = FirstDifference(expected, live);
            if (difference >= 0)
            {
                string message = "NATIVE_FOREIGN_PATCH: " + feature + " stays Vanilla; " + name +
                    " would overwrite a foreign patch at RVA 0x" + Hex(offset) + " (" + length.ToString(CultureInfo.InvariantCulture) +
                    " bytes, first difference at +" + difference.ToString(CultureInfo.InvariantCulture) + "): expected " + Hex(expected) +
                    ", live " + Hex(live) + ". " + DescribeJump(live, difference, address);
                Error(current, message);
                throw new InvalidOperationException(message);
            }
        }

        private static void Watch(Binding current, ulong address, int length, string name, string feature)
        {
            int offset = OffsetOf(current, address, length, name, feature);
            var site = new WatchedSite(feature, name, address, offset, ReadLive(address, length));
            lock (Sync) current.Watched.Add(site);
        }

        private static int SpanLength(IHook hook, string feature)
        {
            int length;
            if (hook is X64InlineHook inline) length = inline.DisplacedByteCount;
            else if (hook is X64AssemblyPatch patch) length = patch.OverwrittenByteCount;
            else
            {
                // NativeDetour<TFunction> is generic; its public DisplacedByteCount is read by name.
                PropertyInfo property = hook.GetType().GetProperty("DisplacedByteCount", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.PropertyType != typeof(int))
                    throw new NotSupportedException(feature + ": " + hook.Name + " (" + hook.GetType().Name +
                        ") has no known displaced span; its foreign-patch check cannot run.");
                length = (int)property.GetValue(hook, null);
            }
            if (hook.TargetAddress == 0 || length <= 0)
                throw new InvalidOperationException(feature + ": " + hook.Name + " has no prepared span.");
            return length;
        }

        // Every APIShared target is CrusaderDE.dll + RVA. A span outside it means the backend followed
        // a jump out of the module (a foreign entry hook) or a wrong address: refused like an overlap.
        private static int OffsetOf(Binding current, ulong address, int length, string name, string feature)
        {
            ScanRegion image = current.Image;
            if (length <= 0 || length > image.Length || address < image.BaseAddress ||
                address - image.BaseAddress > (ulong)(image.Length - length))
            {
                string message = "NATIVE_FOREIGN_PATCH: " + feature + " stays Vanilla; " + name + " span 0x" +
                    address.ToString("X", CultureInfo.InvariantCulture) + " (" + length.ToString(CultureInfo.InvariantCulture) +
                    " bytes) lies outside CrusaderDE.dll; a backend that followed a foreign jump would patch another module.";
                Error(current, message);
                throw new InvalidOperationException(message);
            }
            return (int)(address - image.BaseAddress);
        }

        // Callers bound address and length to the loaded module first.
        private static byte[] ReadLive(ulong address, int length)
        {
            var bytes = new byte[length];
            Marshal.Copy(new IntPtr(unchecked((long)address)), bytes, 0, length);
            return bytes;
        }

        private static int FirstDifference(byte[] expected, byte[] live)
        {
            int length = Math.Min(expected.Length, live.Length);
            for (int index = 0; index < length; index++)
                if (expected[index] != live[index]) return index;
            return expected.Length == live.Length ? -1 : length;
        }

        // Hint at the originator: a jmp rel32 or jmp [rip+disp32] that covers the first difference, and the
        // module its target lies in. Only a hint; a foreign patch need not be a jump.
        internal static string DescribeJump(byte[] live, int difference, ulong address)
        {
            for (int start = difference; start >= 0 && start >= difference - 13; start--)
            {
                ulong target;
                int length;
                if (start + 5 <= live.Length && live[start] == 0xE9)
                {
                    length = 5;
                    target = unchecked(address + (ulong)start + 5 + (ulong)(long)BitConverter.ToInt32(live, start + 1));
                }
                else if (start + 6 <= live.Length && live[start] == 0xFF && live[start + 1] == 0x25)
                {
                    length = 6;
                    ulong slot = unchecked(address + (ulong)start + 6 + (ulong)(long)BitConverter.ToInt32(live, start + 2));
                    if (!TryReadPointer(live, address, slot, out target))
                        return "Indirect jump at +" + start.ToString(CultureInfo.InvariantCulture) + "; its pointer is unreadable.";
                }
                else continue;
                if (start + length <= difference) continue;
                return "Jump at +" + start.ToString(CultureInfo.InvariantCulture) + " to 0x" +
                    target.ToString("X", CultureInfo.InvariantCulture) + " in " + ModuleOf(target) + ".";
            }
            return "No jump covers the first difference.";
        }

        private static bool TryReadPointer(byte[] live, ulong address, ulong slot, out ulong value)
        {
            value = 0;
            if (slot >= address && slot - address <= (ulong)live.Length && slot - address + 8 <= (ulong)live.Length)
            {
                value = BitConverter.ToUInt64(live, (int)(slot - address));
                return true;
            }
            try
            {
                byte[] bytes = Internal.NativeCodeReader.ReadBounded(new IntPtr(unchecked((long)slot)), 8);
                if (bytes.Length < 8) return false;
                value = BitConverter.ToUInt64(bytes, 0);
                return true;
            }
            catch { return false; }
        }

        private static string ModuleOf(ulong address)
        {
            try
            {
                if (!GetModuleHandleExW(ModuleFromAddress | ModuleUnchangedRefcount, new IntPtr(unchecked((long)address)), out IntPtr module) ||
                    module == IntPtr.Zero)
                    return "memory outside any module (a hook stub of another mod or the Script Extender)";
                var path = new StringBuilder(1024);
                return GetModuleFileNameW(module, path, (uint)path.Capacity) == 0 ? "an unnamed module" : path.ToString();
            }
            catch { return "an unknown module"; }
        }

        private static string Hex(int value) => value.ToString("X", CultureInfo.InvariantCulture);
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty);

        private static void WatchFailed(Binding current, string feature, string name, Exception error) =>
            Error(current, "NATIVE_FOREIGN_PATCH_WATCH: " + feature + " (" + name + ") is published but cannot be rechecked: " + error.Message);

        private static void Error(Binding current, string message)
        {
            try { NativeApiLog.Error(current.Log, message); } catch { }
        }


        private sealed class Binding
        {
            internal Binding(ScanRegion image, ManualLogSource log)
            {
                Image = image;
                Log = log;
            }

            internal ScanRegion Image { get; }
            internal ManualLogSource Log { get; }
            // Guarded by Sync; append-only for the process (published spans are never removed).
            internal List<WatchedSite> Watched { get; } = new List<WatchedSite>();
        }

        private sealed class WatchedSite
        {
            internal WatchedSite(string feature, string name, ulong address, int rva, byte[] published)
            {
                Feature = feature;
                Name = name;
                Address = address;
                Rva = rva;
                Published = published;
            }

            internal string Feature { get; }
            internal string Name { get; }
            internal ulong Address { get; }
            internal int Rva { get; }
            internal byte[] Published { get; }
            // Live bytes of the last reported deviation; Recheck runs from the mission publisher only.
            internal byte[] Reported { get; set; }
        }

        private const uint ModuleFromAddress = 0x4;
        private const uint ModuleUnchangedRefcount = 0x2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, uint size);
    }
}
