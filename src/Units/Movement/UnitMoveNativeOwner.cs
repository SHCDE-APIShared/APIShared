using System;
using System.Reflection;
using System.Threading;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace APIShared.Units
{
    // SE owns this native patch. Read its installed contract; never patch, probe-execute,
    // register another native owner or automatically reactivate a lost source.
    internal sealed unsafe class UnitMoveNativeOwner<T> where T:Delegate
    {
        private readonly NativeDetour<T> hook;
        private readonly ulong target;
        private readonly byte[] expected;
        private readonly IntPtr pointerSlot,hookEntry;
        private readonly Action<string> lost;
        private int failed;
        private UnitMoveNativeOwner(NativeDetour<T> hook,ulong target,byte[] expected,Action<string> lost)
        {this.hook=hook;this.target=target;this.expected=expected;pointerSlot=hook.PointerSlot;hookEntry=hook.HookEntryPointAddress;this.lost=lost;}
        internal static bool TryCreate(bool succeeded,IDetour<T> candidate,ulong target,Action<string> lost,
            out UnitMoveNativeOwner<T> owner,out string reason)
        {
            owner=null;
            if(!UnitMoveRuntime.NativeOwnerReady(succeeded,candidate,target))
            {reason="SE unit-move native hook is absent, disabled or targets another entry.";return false;}
            var native=(NativeDetour<T>)candidate;
            try
            {
                // Verified installed RedBird contract: the sealed backend generates this
                // readonly array before installation, then pads it to DisplacedByteCount.
                // Do NOT copy the current entry as its own approval: it may already be foreign.
                var field=typeof(NativeDetour<T>).GetField("_patchBytes",BindingFlags.Instance|BindingFlags.NonPublic);
                if(field==null || field.FieldType!=typeof(byte[]) || !field.IsInitOnly)
                {reason="SE backend expected-patch contract changed.";return false;}
                var bytes=field.GetValue(native) as byte[];
                if(bytes==null || bytes.Length!=native.DisplacedByteCount || bytes.Length<5 || bytes.Length>64 || native.HookEntryPointAddress==IntPtr.Zero)
                {reason="SE backend installed-patch span changed.";return false;}
                var proposed=new UnitMoveNativeOwner<T>(native,target,(byte[])bytes.Clone(),lost);
                if(!proposed.CheckCore(succeeded,candidate,out reason))return false;
                owner=proposed;return true;
            }
            catch{reason="SE backend expected-patch contract could not be read.";return false;}
        }
        internal bool IsCurrent(bool succeeded,IDetour<T> current)
        {
            if(Volatile.Read(ref failed)!=0)return false;
            if(CheckCore(succeeded,current,out string reason))return true;
            if(Interlocked.Exchange(ref failed,1)==0){try{lost?.Invoke(reason);}catch{}}
            return false;
        }
        private bool CheckCore(bool succeeded,IDetour<T> current,out string reason)
        {
            if(!ReferenceEquals(hook,current) || !UnitMoveRuntime.NativeOwnerReady(succeeded,current,target))
            {reason="SE unit-move native owner was replaced or disabled.";return false;}
            byte* live=(byte*)target;
            for(int index=0;index<expected.Length;index++)
                if(live[index]!=expected[index]){reason="Foreign patch overwrote the SE unit-move entry.";return false;}
            if(pointerSlot!=IntPtr.Zero && *(IntPtr*)pointerSlot!=hookEntry)
            {reason="Foreign patch overwrote the SE unit-move jump pointer.";return false;}
            reason=string.Empty;return true;
        }
    }
}
