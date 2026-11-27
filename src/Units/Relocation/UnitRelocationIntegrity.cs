using System;
using System.Threading;

namespace APIShared.Units
{
    // Health is an irreversible capability decision, independent of deduplicated diagnostic counts.
    internal sealed class UnitRelocationIntegrity
    {
        private readonly Func<bool> read;
        private readonly Action<Exception> lost;
        private int failed;
        internal UnitRelocationIntegrity(Func<bool> read,Action<Exception> lost){this.read=read;this.lost=lost;}
        internal bool Check()
        {
            if(Volatile.Read(ref failed)!=0)return false;
            try{if(read())return true;throw new InvalidOperationException("Relocation native contract changed.");}
            catch(Exception error)
            {
                if(Interlocked.Exchange(ref failed,1)==0){try{lost?.Invoke(error);}catch{}}
                return false;
            }
        }
    }
}
