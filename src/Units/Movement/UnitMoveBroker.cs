using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Units
{
    internal readonly struct UnitMoveWorld
    {
        internal readonly long Session,Generation;
        internal UnitMoveWorld(long session,long generation){Session=session;Generation=generation;}
        internal bool Identified=>Session!=0 && Generation!=0;
        internal bool Same(UnitMoveWorld other)=>Identified && Session==other.Session && Generation==other.Generation;
    }
    // This kernel owns no game pointers, detours or UI objects. The existing final Original
    // owner supplies typed calls; the wrapper scope distinguishes independent nested orders.
    internal sealed class UnitMoveBroker
    {
        internal sealed class Registration
        {
            internal string Owner,Id;
            internal int Order,Reported;
            internal IUnitMoveObserver Observer;
            internal Func<bool> Enabled;
        }
        internal sealed class Frame
        {
            internal long Id;
            internal Frame Parent;
            internal Run Run;
            internal UnitMoveWorld World;
        }
        internal sealed class Run
        {
            internal string Owner;
            internal int Unit,X,Y,Extra,Wrappers,Originals;
            internal bool Redirected,IdentityFailed;
            internal UnitMoveWorld World;
            internal Func<bool> Identity;
            internal Frame Parent;
            internal Run Previous;
            internal readonly UnitMoveRunResult Result=new UnitMoveRunResult();
        }
        internal sealed class Active {internal Registration Registration;internal IUnitMoveInvocation Receipt;}
        internal sealed class Invocation
        {
            internal Frame Frame;
            internal UnitMoveWorld World;
            internal UnitMoveContext Context;
            internal UnitMoveCompletion Completion;
            internal readonly List<Active> active=new List<Active>();
            internal void Add(Registration registration,IUnitMoveInvocation receipt)=>active.Add(new Active{Registration=registration,Receipt=receipt});
        }
        private sealed class ThreadState {internal Frame Frame;internal Run Run;}
        [ThreadStatic] private static Dictionary<UnitMoveBroker,ThreadState> threads;
        private readonly object sync=new object();
        private readonly Func<UnitMoveWorld> readWorld;
        private readonly Action<Exception> report;
        private readonly Func<bool> adapterAvailable;
        private Registration[] registrations=Array.Empty<Registration>();
        private int available;
        private long sequence;
        internal UnitMoveBroker(Func<UnitMoveWorld> readWorld,Action<Exception> report,Func<bool> adapterAvailable=null)
        {this.readWorld=readWorld??throw new ArgumentNullException(nameof(readWorld));this.report=report;this.adapterAvailable=adapterAvailable;}
        internal bool IsAvailable=>Volatile.Read(ref available)!=0 && (adapterAvailable==null || adapterAvailable());
        internal void SetAvailable(bool value)=>Volatile.Write(ref available,value?1:0);
        private void Report(Exception error){try{report?.Invoke(error);}catch{}}
        private void Report(Registration registration,Exception error)
        {if(Interlocked.Exchange(ref registration.Reported,1)==0)Report(error);}
        private UnitMoveWorld World(){try{return readWorld();}catch(Exception error){Report(error);return default;}}
        internal bool Register(string owner,string id,IUnitMoveObserver observer,Func<bool> enabled,int order,out string reason)
        {
            if(!IsAvailable){reason="The effective unit-move adapter is unavailable.";return false;}
            if(string.IsNullOrWhiteSpace(owner)||string.IsNullOrWhiteSpace(id)||observer==null)
            {reason="A nonempty owner, registration ID and observer are required.";return false;}
            lock(sync)
            {
                foreach(var item in registrations)if(item.Owner==owner && item.Id==id){reason="Duplicate unit-move registration.";return false;}
                var next=new Registration[registrations.Length+1];Array.Copy(registrations,next,registrations.Length);
                next[next.Length-1]=new Registration{Owner=owner,Id=id,Observer=observer,Enabled=enabled,Order=order};
                Array.Sort(next,(a,b)=>{int c=a.Order.CompareTo(b.Order);if(c==0)c=StringComparer.Ordinal.Compare(a.Owner,b.Owner);return c!=0?c:StringComparer.Ordinal.Compare(a.Id,b.Id);});
                Volatile.Write(ref registrations,next);
            }
            reason=string.Empty;return true;
        }
        private bool Enabled(Registration registration)
        {try{return registration.Enabled==null||registration.Enabled();}catch(Exception error){Report(registration,error);return false;}}
        private bool Demand()
        {if(Volatile.Read(ref available)==0)return false;foreach(var item in Volatile.Read(ref registrations))if(Enabled(item))return IsAvailable;return false;}
        private ThreadState Existing()
        {return threads!=null && threads.TryGetValue(this,out var state)?state:null;}
        private ThreadState Thread()
        {if(threads==null)threads=new Dictionary<UnitMoveBroker,ThreadState>();if(!threads.TryGetValue(this,out var state))threads.Add(this,state=new ThreadState());return state;}
        internal Frame EnterWrapper()
        {
            var state=Existing();
            if(!Demand() && state?.Run==null && state?.Frame==null)return null;
            if(state==null)state=Thread();
            var frame=new Frame{Id=Interlocked.Increment(ref sequence),Parent=state.Frame,World=World()};
            // A root invocation of this scope is bound before Pre. Calls nested in its Pre
            // or Post belong to another wrapper and must retain ordinary Vanilla behavior.
            var run=state.Run;
            if(run!=null && ReferenceEquals(run.Parent,state.Frame)){frame.Run=run;run.Wrappers++;}
            state.Frame=frame;return frame;
        }
        internal void ExitWrapper(Frame frame)
        {if(frame==null)return;var state=Existing();if(state!=null && ReferenceEquals(state.Frame,frame))state.Frame=frame.Parent;}
        private bool Identity(Run run)
        {
            if(run==null)return true;
            try{if(run.Identity())return true;}
            catch(Exception error){run.Result.Failure=error;Report(error);}
            run.IdentityFailed=true;return false;
        }
        private bool Matching(Run run,int unit,int x,int y,int extra)
        {
            if(run==null)return true;
            if(run.Unit!=unit||run.X!=x||run.Y!=y||run.Extra!=extra){run.Redirected=true;return false;}
            return run.Wrappers==1 && run.Originals==0 && Identity(run) && run.World.Same(World());
        }
        internal void OwnerRejected(int unit,int x,int y,int extra)
        {var run=Existing()?.Frame?.Run;if(run!=null && (run.Unit!=unit||run.X!=x||run.Y!=y||run.Extra!=extra))run.Redirected=true;}
        // A false admission suppresses only the guarded root original or an original whose
        // new observer replaced its world. Existing AI admission remains a separate prerequisite.
        internal bool BeforeOriginal(int unit,int x,int y,int extra,out Invocation invocation)
        {
            invocation=null;var frame=Existing()?.Frame;
            if(frame==null)return true;
            if(frame.World.Identified && !frame.World.Same(World()))return false;
            if(!Matching(frame.Run,unit,x,y,extra))return false;
            var world=World();
            if(!world.Identified)return frame.Run==null;
            invocation=new Invocation{Frame=frame,World=world};
            invocation.Context=new UnitMoveContext(frame.Id,frame.Parent?.Id??0,world.Session,world.Generation,unit,x,y,extra,frame.Run?.Owner);
            invocation.Completion=new UnitMoveCompletion(invocation.Context);
            foreach(var registration in Volatile.Read(ref registrations))
            {
                if(!Enabled(registration))continue;
                try{var receipt=registration.Observer.TryBegin(invocation.Context);if(receipt!=null)invocation.Add(registration,receipt);}
                catch(Exception error){Report(registration,error);}
            }
            if(!world.Same(World()) || !Matching(frame.Run,unit,x,y,extra))return false;
            if(frame.Run!=null)frame.Run.Originals++;
            return true;
        }
        internal void Complete(Invocation invocation,bool invoked,bool returned,long result,Exception failure)
        {
            if(invocation==null)return;
            var run=invocation.Frame.Run;
            if(run!=null && invoked)
            {run.Result.NativeInvoked=true;run.Result.NativeReturned=returned;run.Result.NativeResult=result;run.Result.Failure=failure;}
            invocation.Completion.Set(invoked,returned,result,failure,invocation.World.Same(World()));
            for(int index=invocation.active.Count-1;index>=0;index--)
            {
                var active=invocation.active[index];
                try{active.Receipt.Complete(invocation.Completion);}catch(Exception error){Report(active.Registration,error);}
            }
        }
        internal bool TryRun(string owner,int unit,int x,int y,int extra,Func<bool> identity,Func<long> invoke,
            out UnitMoveRunResult result,out string reason)
        {
            result=new UnitMoveRunResult();
            if(!IsAvailable){result.Status=UnitMoveRunStatus.Unavailable;reason="The effective unit-move adapter is unavailable.";return false;}
            if(string.IsNullOrWhiteSpace(owner)||unit<1||unit>=AiMilitary.AiMilitaryService.UnitIdLimit||(uint)x>=800||(uint)y>=800||identity==null||invoke==null)
            {result.Status=UnitMoveRunStatus.InvalidRequest;reason="A valid owner, unit, destination, identity guard and synchronous invocation are required.";return false;}
            var world=World();
            if(!world.Identified){result.Status=UnitMoveRunStatus.UnidentifiedWorld;reason="No identified native world is available.";return false;}
            var state=Thread();var run=new Run{Owner=owner,Unit=unit,X=x,Y=y,Extra=extra,Identity=identity,World=world,Parent=state.Frame,Previous=state.Run};
            result=run.Result;
            if(Identity(run) && world.Same(World()))
            {
                state.Run=run;
                try{result.WrapperResult=invoke();result.WrapperReturned=true;}
                catch(Exception error){result.WrapperFailure=error;}
                finally{state.Run=run.Previous;}
            }
            bool currentIdentity=Identity(run);
            result.IsCurrentWorld=world.Same(World());
            result.Status=!result.IsCurrentWorld?UnitMoveRunStatus.WorldChanged:
                !currentIdentity||run.IdentityFailed?UnitMoveRunStatus.IdentityChanged:
                run.Redirected?UnitMoveRunStatus.Redirected:
                run.Wrappers>1||run.Originals>1?UnitMoveRunStatus.MultipleCalls:
                !result.NativeInvoked?UnitMoveRunStatus.NoOriginal:
                !result.NativeReturned?UnitMoveRunStatus.NativeFailed:
                result.NativeResult==1?UnitMoveRunStatus.NativeSucceeded:
                result.NativeResult==0?UnitMoveRunStatus.NativeRefused:UnitMoveRunStatus.NativeFailed;
            reason=result.Status==UnitMoveRunStatus.NativeSucceeded?string.Empty:"Guarded unit move: "+result.Status+".";
            return result.Status==UnitMoveRunStatus.NativeSucceeded;
        }
    }
}
