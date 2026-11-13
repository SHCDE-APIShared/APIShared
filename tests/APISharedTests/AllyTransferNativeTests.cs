using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using APIShared;
using APIShared.Economy;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using R3;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using SHCDESE.EventAPI;
using SHCDESE.Interop;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class AllyTransferNativeTests
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);

        [TestMethod]
        public void CanonicalBodiesAndInstalledSeOriginalCallsiteAreValidated()
        {
            using (var sha = SHA256.Create())
            {
                Assert.AreEqual("206BDE68C11D963B744BF671B185935EC32EF05CB1EBB3A479E308095CC03612", BitConverter.ToString(sha.ComputeHash(AllyTransfersNative.TransferBytes)).Replace("-", ""));
                Assert.AreEqual("978F663FC2E2F1FAF8CF89FE73BFD17ABD9FA728F13A57AF1325828DB9E1DDA9", BitConverter.ToString(sha.ComputeHash(AllyTransfersNative.CapacityBytes)).Replace("-", ""));
            }
            byte[] image = new byte[AllyTransfersNative.TransferRva + AllyTransfersNative.TransferBytes.Length];
            Array.Copy(AllyTransfersNative.TransferBytes, 0, image, AllyTransfersNative.TransferRva, AllyTransfersNative.TransferBytes.Length);
            Array.Copy(AllyTransfersNative.CapacityBytes, 0, image, AllyTransfersNative.CapacityRva, AllyTransfersNative.CapacityBytes.Length);
            AllyTransfersNative.ValidatePristine(image);
            image[AllyTransfersNative.CapacityRva + 70] ^= 1;
            Assert.ThrowsExactly<InvalidOperationException>(() => AllyTransfersNative.ValidatePristine(image));
            MethodInfo method = AllyTransfersSeContract.Resolve();
            Assert.IsFalse(method.DeclaringType.IsPublic, "The real loaded internal type must be accessed through the validated bridge.");
            AllyTransfersSeContract.Validate(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var il = new ILContext(definition.Definition))
            {
                var call = AllyTransfersSeContract.Find(il);
                il.Body.Instructions.Remove(call);
                Assert.ThrowsExactly<InvalidOperationException>(() => AllyTransfersSeContract.Find(il));
            }
        }

        [TestMethod]
        public void ActualSeIlAndItsSoleNativeOwnerPreserveEffectiveMutationPostAndOpaqueReturn()
        {
            using (var fixture = new SeFixture())
            {
                int apiAmount = -1, sePostAmount = -1, apiPosts = 0; bool? sePostReturn = null;
                AllyTransferResult observed = null;
                fixture.Events.TryRegister("test", "effective", pre => apiAmount = pre.Amount,
                    post => { apiPosts++; observed = post.Result; }, out _);
                using (PlayerR3EventHooks.OnPlayerAIRequestReceivedGoods.Observable.Subscribe(args => {
                    if (args.Phase == EventHookPhase.Pre) args.Amount = 4;
                    else { sePostAmount = args.Amount; sePostReturn = args.ReturnValue; }
                }))
                {
                    int wrapperReturn = fixture.CallNative(2, eGoods.STORED_WOOD_PLANKS, 2, 1);
                    Assert.AreEqual(0, wrapperReturn, "SE preserves its legacy ==1 normalization of the opaque native int.");
                    Assert.AreEqual(4, apiAmount); Assert.AreEqual(2, sePostAmount, "SE Post keeps its original input contract.");
                    Assert.AreEqual(false, sePostReturn); Assert.AreEqual(4, observed.TransferredAmount);
                    Assert.AreEqual(1, apiPosts); Assert.AreEqual(96, fixture.Source); Assert.AreEqual(4, fixture.Target);
                    Assert.AreEqual(0x76543210, fixture.LastOpaqueReturn);
                    var service=fixture.Bridge.Execute(new AllyTransferRequest(1,2,eGoods.STORED_WOOD_PLANKS,4),4);
                    Assert.AreEqual(AllyTransferStatus.Completed,service.Status); Assert.AreEqual(4,service.TransferredAmount);
                    Assert.AreEqual(92,fixture.Source); Assert.AreEqual(8,fixture.Target);
                }
                AllyTransfersSeContract.ValidateNativeOwner(fixture.SeMethod, fixture.Code);
            }
        }

        [TestMethod]
        public void ServiceHonorsSeVetoAndRejectsSeArgumentDriftBeforeNativeMutation()
        {
            using (var fixture = new SeFixture())
            {
                int apiPre = 0, sePost = 0;
                fixture.Events.TryRegister("test", "observe", pre => apiPre++, null, out _);
                var request = new AllyTransferRequest(1, 2, eGoods.STORED_WOOD_PLANKS, 10, 90, 10);
                using (PlayerR3EventHooks.OnPlayerAIRequestReceivedGoods.Observable.Subscribe(args => {
                    if (args.Phase == EventHookPhase.Pre) { args.SkipOriginalFunction = true; args.ReturnValue = true; }
                    else sePost++;
                }))
                {
                    var result = fixture.Bridge.Execute(request, 10);
                    Assert.AreEqual(AllyTransferStatus.Vetoed, result.Status); Assert.IsFalse(result.OriginalWasCalled);
                    Assert.AreEqual(0, apiPre); Assert.AreEqual(0, sePost); Assert.AreEqual(100, fixture.Source);
                }
                using (PlayerR3EventHooks.OnPlayerAIRequestReceivedGoods.Observable.Subscribe(args => {
                    if (args.Phase == EventHookPhase.Pre) args.Amount = 11;
                }))
                {
                    var result = fixture.Bridge.Execute(request, 10);
                    Assert.AreEqual(AllyTransferStatus.Rejected, result.Status); Assert.IsFalse(result.OriginalWasCalled);
                    Assert.AreEqual(0, apiPre); Assert.AreEqual(100, fixture.Source); Assert.AreEqual(0, fixture.Target);
                }
                Assert.IsFalse(AllyTransfersSeBridge.HasServiceFrame);
            }
        }

        [TestMethod]
        public void ServiceRevalidatesAfterBothPreChainsAndSharedVetoLeavesSePostIntact()
        {
            using (var fixture = new SeFixture())
            {
                int sePost = 0;
                fixture.Admit = false;
                var request = new AllyTransferRequest(1, 2, eGoods.STORED_WOOD_PLANKS, 10);
                using (PlayerR3EventHooks.OnPlayerAIRequestReceivedGoods.Observable.Subscribe(args => { if (args.Phase == EventHookPhase.Post) sePost++; }))
                {
                    var rejected = fixture.Bridge.Execute(request, 10);
                    Assert.AreEqual(AllyTransferStatus.Rejected, rejected.Status); Assert.IsFalse(rejected.OriginalWasCalled);
                    Assert.AreEqual(1, sePost); Assert.AreEqual(100, fixture.Source);
                    fixture.Admit = true;
                    fixture.Events.TryRegister("test", "veto", pre => pre.SkipOriginalFunction = true, null, out _);
                    var veto = fixture.Bridge.Execute(request, 10);
                    Assert.AreEqual(AllyTransferStatus.Vetoed, veto.Status); Assert.AreEqual(2, sePost);
                    Assert.AreEqual(100, fixture.Source); Assert.AreEqual(0, fixture.Target);
                }
            }
        }

        [TestMethod]
        public void FaultAfterActualPrimitiveKeepsDeltasAndRestoresServiceFrame()
        {
            int source = 100, target = 0;
            AllyTransfersSeBridge bridge = null;
            bool Read(int player, eGoods good, out int amount) { amount = player == 1 ? source : target; return true; }
            var events = new InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs>();
            AllyTransferSeEntry entry = (r,g,a,s) => { bridge.EnterWrapper(); try { bridge.Invoke(new Action(() => {}), r,g,a,s); throw new Exception("later SE Post failed"); } finally { bridge.LeaveWrapper(); } };
            bridge = new AllyTransfersSeBridge(entry, (original,r,g,a,s) => { source -= a; target += a; return 73; }, Read, (request,amount) => true, events);
            var result = bridge.Execute(new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, 10), 10);
            Assert.AreEqual(AllyTransferStatus.Faulted, result.Status); Assert.IsTrue(result.OriginalWasCalled);
            Assert.AreEqual(10L, result.SenderDebit); Assert.AreEqual(10L, result.RecipientCredit);
            Assert.IsFalse(AllyTransfersSeBridge.HasServiceFrame); Assert.AreEqual(90, source); Assert.AreEqual(10, target);
            Assert.IsFalse(AllyTransfersSeBridge.IsInsideWrapper);
        }

        [TestMethod]
        public void GiftNestedInActualSePreDoesNotConsumeTheRootServiceInvocation()
        {
            using (var fixture = new SeFixture())
            {
                bool nesting = false;
                using (PlayerR3EventHooks.OnPlayerAIRequestReceivedGoods.Observable.Subscribe(args => {
                    if (args.Phase == EventHookPhase.Pre && !nesting)
                    { nesting = true; try { fixture.CallNative(2, eGoods.STORED_WOOD_PLANKS, 1, 1); } finally { nesting = false; } }
                }))
                {
                    var result = fixture.Bridge.Execute(new AllyTransferRequest(1,2,eGoods.STORED_WOOD_PLANKS,10),10);
                    Assert.AreEqual(89,fixture.Source); Assert.AreEqual(11,fixture.Target);
                    Assert.AreEqual(AllyTransferStatus.Uncertain,result.Status); Assert.IsTrue(result.HasNestedTransfer);
                    Assert.IsTrue(result.OriginalWasCalled); Assert.IsNull(result.TransferredAmount);
                }
            }
            Assert.IsFalse(AllyTransfersSeBridge.IsInsideWrapper); Assert.IsFalse(AllyTransfersSeBridge.HasServiceFrame);
        }

        [TestMethod]
        public void GiftNestedInsideOriginalRunsExactlyOnceAndOuterAttributionIsUncertain()
        {
            int source=100,target=0,calls=0; bool nested=false;
            AllyTransfersSeBridge bridge=null; AllyTransferSeEntry entry=null;
            bool Read(int player,eGoods good,out int amount) { amount=player==1?source:target; return true; }
            entry=(r,g,a,s) => { bridge.EnterWrapper(); try { return bridge.Invoke(new Action(()=>{}),r,g,a,s); } finally { bridge.LeaveWrapper(); } };
            bridge=new AllyTransfersSeBridge(entry,(original,r,g,a,s) => {
                calls++;
                if (!nested) { nested=true; try { entry(r,g,1,s); } finally { nested=false; } }
                source-=a; target+=a; return 73;
            },Read,(request,amount)=>true,new InterceptionEvent<AllyTransferPreEventArgs,AllyTransferPostEventArgs>());
            var result=bridge.Execute(new AllyTransferRequest(1,2,eGoods.STORED_GOLD,10),10);
            Assert.AreEqual(2,calls); Assert.AreEqual(89,source); Assert.AreEqual(11,target);
            Assert.AreEqual(AllyTransferStatus.Uncertain,result.Status); Assert.IsTrue(result.HasNestedTransfer);
            Assert.IsNull(result.TransferredAmount); Assert.IsFalse(AllyTransfersSeBridge.IsInsideWrapper);
            Assert.IsFalse(AllyTransfersSeBridge.HasServiceFrame);
        }

        [TestMethod]
        public void OriginalExceptionPublishesFaultedDeltasAndSkipsLegacySePostForVanillaAndService()
        {
            using (var fixture = new SeFixture { ThrowInOriginal = true })
            {
                int sePosts = 0; AllyTransferResult observed = null;
                fixture.Events.TryRegister("test", "fault", null, post => observed = post.Result, out _);
                using (PlayerR3EventHooks.OnPlayerAIRequestReceivedGoods.Observable.Subscribe(args => {
                    if (args.Phase == EventHookPhase.Post) sePosts++;
                }))
                {
                    Assert.ThrowsExactly<InvalidOperationException>(() => fixture.CallManaged(2,eGoods.STORED_WOOD_PLANKS,10,1));
                    Assert.AreEqual(0,sePosts); Assert.AreEqual(AllyTransferStatus.Faulted,observed.Status);
                    Assert.IsTrue(observed.OriginalWasCalled); Assert.AreEqual(0L,observed.SenderDebit); Assert.AreEqual(10L,observed.RecipientCredit);
                    Assert.IsFalse(AllyTransfersSeBridge.IsInsideWrapper);
                    Marshal.WriteInt32(fixture.Receipt,20,0);
                    var result=fixture.Bridge.Execute(new AllyTransferRequest(1,2,eGoods.STORED_WOOD_PLANKS,10),10);
                    Assert.AreEqual(0,sePosts); Assert.AreEqual(AllyTransferStatus.Faulted,result.Status);
                    Assert.IsTrue(result.OriginalWasCalled); Assert.AreEqual(0L,result.SenderDebit); Assert.AreEqual(10L,result.RecipientCredit);
                    Assert.IsFalse(AllyTransfersSeBridge.IsInsideWrapper); Assert.IsFalse(AllyTransfersSeBridge.HasServiceFrame);
                }
            }
        }

        private sealed class SeFixture : IDisposable
        {
            internal readonly InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs> Events = new InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs>();
            internal readonly MethodInfo SeMethod = AllyTransfersSeContract.Resolve();
            internal readonly IntPtr Code, Receipt;
            internal readonly AllyTransfersSeBridge Bridge;
            internal bool Admit = true;
            internal bool ThrowInOriginal;
            internal int LastOpaqueReturn;
            private readonly FieldInfo handleField;
            private readonly object previousHandle;
            private readonly object nativeHook;
            private readonly Delegate callback;
            private readonly AllyTransferSeEntry entry;
            private readonly ILContext copiedContext;
            internal int Source => Marshal.ReadInt32(Receipt, 16);
            internal int Target => Marshal.ReadInt32(Receipt, 20);
            internal SeFixture()
            {
                Code = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40); Receipt = Marshal.AllocHGlobal(32);
                Assert.AreNotEqual(IntPtr.Zero, Code);
                for (int i=0;i<32;i++) Marshal.WriteByte(Receipt,i,0); Marshal.WriteInt32(Receipt,16,100);
                var bytes = new List<byte> { 0x40,0x55,0x56,0x41,0x54,0x48,0x83,0xEC,0x40,0x48,0xB8 };
                bytes.AddRange(BitConverter.GetBytes(Receipt.ToInt64()));
                bytes.AddRange(new byte[] { 0x89,0x08,0x89,0x50,0x04,0x44,0x89,0x40,0x08,0x44,0x89,0x48,0x0C,
                    0x44,0x29,0x40,0x10,0x44,0x01,0x40,0x14,0xB8,0x10,0x32,0x54,0x76,0x48,0x83,0xC4,0x40,0x41,0x5C,0x5E,0x5D,0xC3 });
                Marshal.Copy(bytes.ToArray(),0,Code,bytes.Count);
                Type delegateType = AllyTransfersSeContract.OriginalDelegateType(SeMethod);
                var forward = AllyTransfersSeContract.CreateForwarder(delegateType);
                AllyTransferSeEntry compiled = null;
                Bridge = new AllyTransfersSeBridge((r,g,a,s) => compiled(r,g,a,s),
                    (d,r,g,a,s) => {
                        if (ThrowInOriginal) { Marshal.WriteInt32(Receipt,20,Target+a); throw new InvalidOperationException("private original failure after partial credit"); }
                        LastOpaqueReturn=forward(d,r,g,a,s); return LastOpaqueReturn;
                    }, Read, (request,amount) => Admit, Events);
                using (var definition = new DynamicMethodDefinition(SeMethod))
                {
                    // EmitDelegate references belong to ILContext, not the generated delegate.
                    // Match the real ILHook's lifetime instead of clearing them before first call.
                    var il = copiedContext = new ILContext(definition.Definition);
                    il.ReferenceBag = RuntimeILReferenceBag.Instance;
                    il.Invoke(context => AllyTransfersSeContract.Apply(context, Bridge.Invoke, Bridge.EnterWrapper, Bridge.LeaveWrapper));
                    MethodInfo copy = definition.Generate();
                    compiled = (AllyTransferSeEntry)BindCopy(copy, typeof(AllyTransferSeEntry));
                    callback = BindCopy(copy, delegateType);
                }
                entry=compiled;
                handleField=SeMethod.DeclaringType.GetField(AllyTransfersSeContract.HandleName,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
                previousHandle=handleField.GetValue(null);
                var requestType=typeof(DetourRequest<>).MakeGenericType(delegateType);
                object detourRequest=Activator.CreateInstance(requestType);
                requestType.GetProperty("Name").SetValue(detourRequest,"Private SE-owned ally fixture",null);
                requestType.GetProperty("TargetAddress").SetValue(detourRequest,unchecked((ulong)Code.ToInt64()),null);
                requestType.GetProperty("Callback").SetValue(detourRequest,callback,null);
                var backend = new NativeDetourBackend(new NativeDetourOptions { AllowedSchemes=DetourScheme.Indirect,FollowJumps=false,SuspendThreadsDuringPatch=false });
                nativeHook=typeof(NativeDetourBackend).GetMethod("CreateDetour").MakeGenericMethod(delegateType).Invoke(backend,new[]{detourRequest});
                object handle=Activator.CreateInstance(handleField.FieldType);
                handleField.FieldType.GetMethod("Attach",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(handle,new[]{nativeHook,(object)unchecked((ulong)Code.ToInt64())});
                handleField.SetValue(null,handle);
                ((IHook)nativeHook).Enable();
            }
            private bool Read(int player,eGoods good,out int amount) { amount=player==1?Source:Target; return true; }
            private static Delegate BindCopy(MethodInfo method, Type type) =>
                method is System.Reflection.Emit.DynamicMethod dynamic ? dynamic.CreateDelegate(type) : Delegate.CreateDelegate(type, method);
            internal int CallNative(int recipient,eGoods good,int amount,int sender)
            {
                Type type=AllyTransfersSeContract.OriginalDelegateType(SeMethod);
                var native=Marshal.GetDelegateForFunctionPointer(Code,type);
                return AllyTransfersSeContract.CreateForwarder(type)(native,recipient,good,amount,sender);
            }
            internal int CallManaged(int recipient,eGoods good,int amount,int sender) => entry(recipient,good,amount,sender);
            public void Dispose()
            {
                // Isolated CLI fixture, no other thread can enter its private executable buffer.
                handleField?.SetValue(null,previousHandle); (nativeHook as IDisposable)?.Dispose(); copiedContext?.Dispose();
                VirtualFree(Code,UIntPtr.Zero,0x8000); Marshal.FreeHGlobal(Receipt); GC.KeepAlive(callback); GC.KeepAlive(entry);
            }
        }
    }
}
