using System;
using System.Runtime.InteropServices;

namespace APIShared.Units
{
    // A preparation transaction. No instance can escape until every native-demand dependency
    // exists. Cleanup is confined to failed, unpublished preparation; published owners never dispose.
    internal sealed class UnitRelocationMetadata
    {
        internal readonly object Monitor;
        internal readonly IntPtr Interest;
        internal readonly UnitRelocationPolicies Policies;
        internal readonly IDisposable Initialization,Started;
        private UnitRelocationMetadata(object monitor,IntPtr interest,UnitRelocationPolicies policies,IDisposable initialization,IDisposable started)
        {Monitor=monitor;Interest=interest;Policies=policies;Initialization=initialization;Started=started;}
        internal static UnitRelocationMetadata Create(Func<object> readMonitor,Func<IntPtr> allocate,
            Func<IDisposable> subscribeInitialization,Func<IDisposable> subscribeStarted,Action<Exception> report)
        {
            object monitor=readMonitor();
            if(monitor==null)throw new InvalidOperationException("Relocation engine monitor is unavailable.");
            IntPtr table=IntPtr.Zero;IDisposable initialization=null,started=null;
            try
            {
                table=allocate();if(table==IntPtr.Zero)throw new InvalidOperationException("Relocation demand allocation failed.");
                for(int i=0;i<65536;i++)Marshal.WriteByte(table,i,0);
                var policies=new UnitRelocationPolicies(table,report);
                initialization=subscribeInitialization()??throw new InvalidOperationException("Relocation initialization observer is unavailable.");
                started=subscribeStarted()??throw new InvalidOperationException("Relocation mission observer is unavailable.");
                return new UnitRelocationMetadata(monitor,table,policies,initialization,started);
            }
            catch
            {
                try{started?.Dispose();}finally{try{initialization?.Dispose();}finally{if(table!=IntPtr.Zero)Marshal.FreeHGlobal(table);}}
                throw;
            }
        }
    }
}
