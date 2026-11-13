using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Economy;
using APIShared.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class WorkerProductionWorldSafetyTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic|BindingFlags.Static;
        private static FieldInfo SafetyField(string name) => typeof(WorkerProductionWorldSafety).GetField(name,PrivateStatic);
        private static FieldInfo DispatchField(string name) => typeof(UnityMainThreadDispatch).GetField(name,PrivateStatic);

        // Isolate only external engine/dispatch state. All admission, fault, notification,
        // retry and stale-termination behavior below executes the production implementation.
        private sealed class World : IDisposable
        {
            internal readonly object Gate = new object();
            private readonly byte[] native = new byte[32];
            private readonly string[] fields = { "engineGate", "session", "reason", "notificationQueued", "faultReported", "log" };
            private readonly object[] before;
            private readonly object context;
            internal World()
            {
                before = new object[fields.Length];
                for (int i=0;i<fields.Length;i++) before[i]=SafetyField(fields[i]).GetValue(null);
                Marshal.Copy(WorkerProductionWorldSafety.NativeFaultAddress,native,0,native.Length);
                context=DispatchField("unityContext").GetValue(null);
                // Failed enqueue must not defer or duplicate the pure fault notification.
                DispatchField("unityContext").SetValue(null,null);
                SafetyField("engineGate").SetValue(null,Gate); SafetyField("log").SetValue(null,null);
                WorkerProductionWorldSafety.Admit(301,true);
            }
            public void Dispose()
            {
                Marshal.Copy(native,0,WorkerProductionWorldSafety.NativeFaultAddress,native.Length);
                for (int i=0;i<fields.Length;i++) SafetyField(fields[i]).SetValue(null,before[i]);
                DispatchField("unityContext").SetValue(null,context);
            }
        }

        [TestMethod]
        public void FaultIsPublishedBeforeRetirementCanInvalidateDeferredTermination()
        {
            using (var world = new World())
            {
                int reports=0;string message=null;bool underMonitor=true;
                Action<string> observer = detail => { underMonitor&=Monitor.IsEntered(world.Gate);reports++;message=detail; };
                WorkerProductionWorldSafety.Faulted+=observer;
                try
                {
                    WorkerProductionWorldSafety.Fault("native admission rejected");
                    long generation=WorkerProductionWorldSafety.Generation;
                    Assert.AreEqual(1,reports,"Status must already be observable before the Unity dispatcher runs.");
                    Assert.IsTrue(underMonitor);
                    Assert.AreEqual("native admission rejected",message);
                    WorkerProductionWorldSafety.Retire();
                    typeof(WorkerProductionWorldSafety).GetMethod("Terminate",PrivateStatic).Invoke(null,new object[]{generation,301L,message});
                    Assert.AreEqual(1,reports,"A stale stop is suppressed without losing or repeating status.");
                }
                finally { WorkerProductionWorldSafety.Faulted-=observer; }
            }
        }

        [TestMethod]
        public void NativeFaultRetriesAndReentrantObservationPublishOnlyOncePerAdmission()
        {
            using (var world = new World())
            {
                int reports=0;bool underMonitor=true,correctDetails=true;
                Action<string> observer = detail => {
                    reports++;underMonitor&=Monitor.IsEntered(world.Gate);
                    correctDetails&=detail.Contains("code=3") && detail.Contains("unit=72");
                    WorkerProductionWorldSafety.ObserveNativeFault();
                };
                WorkerProductionWorldSafety.Faulted+=observer;
                try
                {
                    IntPtr native=WorkerProductionWorldSafety.NativeFaultAddress;
                    Marshal.WriteInt32(native,0,3);Marshal.WriteInt32(native,4,72);
                    Marshal.WriteInt32(native,8,101);Marshal.WriteInt32(native,12,150);
                    WorkerProductionWorldSafety.ObserveNativeFault();
                    WorkerProductionWorldSafety.ObserveNativeFault();
                    Assert.AreEqual(1,reports,"Enqueue failure and reentrant observation must not repeat status.");
                    Assert.IsTrue(underMonitor);Assert.IsTrue(correctDetails);
                    Assert.AreEqual(0,(int)SafetyField("notificationQueued").GetValue(null),"The unavailable dispatcher leaves stop delivery retryable.");
                    WorkerProductionWorldSafety.Admit(302,true);
                    Marshal.WriteInt32(native,0,3);Marshal.WriteInt32(native,4,72);
                    WorkerProductionWorldSafety.ObserveNativeFault();Assert.AreEqual(2,reports);
                }
                finally { WorkerProductionWorldSafety.Faulted-=observer; }
            }
        }

        [TestMethod]
        public void ThrowingStatusSubscriberCannotLoseOtherSubscribersOrRetryTheFault()
        {
            using (var world = new World())
            {
                int reports=0;
                Action<string> broken = detail => throw new InvalidOperationException("subscriber failure");
                Action<string> healthy = detail => reports++;
                WorkerProductionWorldSafety.Faulted+=broken;WorkerProductionWorldSafety.Faulted+=healthy;
                try
                {
                    WorkerProductionWorldSafety.Fault("first");WorkerProductionWorldSafety.Fault("second");
                    WorkerProductionWorldSafety.ObserveNativeFault();Assert.AreEqual(1,reports);
                }
                finally { WorkerProductionWorldSafety.Faulted-=broken;WorkerProductionWorldSafety.Faulted-=healthy; }
            }
        }
    }
}
