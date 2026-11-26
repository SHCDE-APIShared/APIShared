using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using R3;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal sealed class ProductionGoodsReceivedNativeRuntime
    {
        private static ProductionGoodsReceivedNativeRuntime owner;
        private static ProductionGoodsReceivedNativeRuntime tannerOwner;
        private static string diagnostic="Production receipt native sources have not initialized.";
        private static int externalTannerReady;
        // The Tanner source is decided at the first mission initialization, when every plugin has
        // loaded: ImprovedHunters' confirmed adapter owns it, otherwise APIShared installs it. During
        // APIShared's own startup later plugins are not yet in Chainloader.PluginInfos.
        private const int TannerPending=0, TannerExternal=1, TannerOwned=2, TannerFailed=3;
        private static int tannerState;
        private static int tannerDecided;
        private static string tannerDiagnostic=string.Empty;
        internal static IDisposable TannerDecisionSubscription;
        private readonly CrusaderLibraryLoadContext context;
        private readonly ulong image;
        private readonly ManualLogSource log;
        private readonly ProductionGoodsReceivedNativeRuntime adapterOwner;
        private readonly ProductionGoodsReceivedNativeDefinition.Site[] sites;
        private readonly HookHandle<X64InlineHook>[] handles;
        private readonly NativeHookPublicationState publication=new NativeHookPublicationState();
        private readonly NativeInlineStubGroup group;
        private ProductionGoodsReceivedNativeCode adapter;
        private int tannerIndex = -1;
        private IntPtr tannerStub, tannerRelay;
        private byte[] tannerStubBytes, tannerContinuationBytes, tannerRelayBytes;
        private ProductionGoodsReceivedNativeRuntime(CrusaderLibraryLoadContext context, ManualLogSource log,
            bool tanner, ProductionGoodsReceivedNativeRuntime adapterOwner)
        {
            this.context=context;this.log=log;image=unchecked((ulong)context.ModuleHandle.ToInt64());
            this.adapterOwner=adapterOwner ?? this;
            sites=ProductionGoodsReceivedNativeDefinition.Sites
                .Where(s=>(s.Name==ProductionGoodsReceivedNativeDefinition.TannerSiteName)==tanner).ToArray();
            handles=new HookHandle<X64InlineHook>[sites.Length];
            group=new NativeInlineStubGroup(tanner ? ProductionGoodsReceivedNativeDefinition.TannerSiteGroup :
                ProductionGoodsReceivedNativeDefinition.SiteGroup,publication);
        }
        internal static ulong AdapterAddress => owner?.adapter?.IsPublished==true ? owner.PreparedAdapterAddress : 0;
        private ulong PreparedAdapterAddress => adapterOwner.adapter==null ? 0 : unchecked((ulong)adapterOwner.adapter.Address.ToInt64());
        // A pending Tanner decision counts as available: it resolves before any map simulates.
        internal static bool IsAvailable => owner!=null && owner.publication.IsReady &&
            Volatile.Read(ref tannerState)!=TannerFailed && WeaponsReady;
        private static bool WeaponsReady =>
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_FLETCHERS_WORKSHOP) &&
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_POLETURNERS_WORKSHOP) &&
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_BLACKSMITHS_WORKSHOP) &&
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_ARMOURERS_WORKSHOP) &&
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_TANNERS_WORKSHOP);
        // This is a route attestation, independent of receipt demand and weapon readiness.
        // All metadata is frozen before publication; a frequent capability/delivery query
        // only reads this process-lifetime owner's exact source, stub, continuation and relay.
        // It neither installs code nor treats an arbitrary jump as the original Tanner route.
        internal static bool HasPublishedTannerSource(IntPtr module)
        {
            var root = owner;
            var current = Volatile.Read(ref tannerOwner);
            if (Volatile.Read(ref tannerState) != TannerOwned || root == null || current == null ||
                module == IntPtr.Zero || current.image != unchecked((ulong)module.ToInt64()) ||
                !ReferenceEquals(current.adapterOwner, root) || !root.publication.IsReady ||
                root.adapter == null || !root.adapter.IsPublished || !current.publication.IsReady)
                return false;
            return current.TannerSourceMatches() && ReferenceEquals(root, owner) &&
                ReferenceEquals(current, Volatile.Read(ref tannerOwner)) &&
                Volatile.Read(ref tannerState) == TannerOwned && root.publication.IsReady && current.publication.IsReady;
        }

        private unsafe bool TannerSourceMatches()
        {
            var relay = adapterOwner.adapter;
            if (tannerIndex < 0 || handles == null || sites == null || tannerIndex >= handles.Length ||
                tannerIndex >= sites.Length || tannerStub == IntPtr.Zero || tannerRelay == IntPtr.Zero ||
                tannerStubBytes == null || tannerStubBytes.Length == 0 || tannerContinuationBytes == null ||
                tannerContinuationBytes.Length < NativeInlineStub.JumpLength || tannerRelayBytes == null ||
                tannerRelayBytes.Length == 0 || relay == null || !relay.IsPublished || relay.Address != tannerRelay)
                return false;
            var site = sites[tannerIndex];
            var handle = handles[tannerIndex];
            if (handle == null || !handle.Success ||
                !NativeInlineStub.IsPrepared(handle, true, image + (uint)site.Rva, site.Bytes.Length) ||
                handle.Hook.StubAddress != tannerStub)
                return false;
            IntPtr entry = (IntPtr)(image + (uint)site.Rva);
            return NativeInlineStub.JumpMatches(entry, tannerStub) &&
                NativeInlineStub.PaddingMatches(entry, site.Bytes.Length) &&
                new ReadOnlySpan<byte>((void*)tannerStub, tannerStubBytes.Length).SequenceEqual(tannerStubBytes) &&
                new ReadOnlySpan<byte>((void*)(entry + site.Bytes.Length), tannerContinuationBytes.Length).SequenceEqual(tannerContinuationBytes) &&
                new ReadOnlySpan<byte>((void*)tannerRelay, tannerRelayBytes.Length).SequenceEqual(tannerRelayBytes);
        }

        private void PrepareTannerSource(int index)
        {
            var site = sites[index];
            if (site.Rva != WeaponProductionNativeDefinition.TannerStoredRouteBoundaryRva ||
                !site.Bytes.AsSpan().SequenceEqual(WeaponProductionNativeDefinition.TannerStoredRouteBoundaryBytes))
                throw new InvalidOperationException("The Tanner receipt source is not the qualified original storage call.");
            tannerStub = handles[index].Hook.StubAddress;
            tannerStubBytes = Expected(site, unchecked((ulong)tannerStub.ToInt64()));
            // Freeze the complete permitted boundary, including an existing indirect
            // jump's address; checking only its first opcode bytes would accept retargeting.
            tannerContinuationBytes = NativeInlineStub.ReadLive((IntPtr)image,
                site.Rva + site.Bytes.Length, Math.Max(NativeInlineStub.JumpLength, site.Next.Length));
            if (!NativeInlineStub.AcceptsLiveBoundary(tannerContinuationBytes, site.Next))
                throw new InvalidOperationException("The prepared Tanner receipt continuation differs.");
            // A non-inline indirect target cell is not covered by these frozen bytes.
            // Preserve the receipt owner's existing boundary support, but do not attest
            // that opaque continuation as a qualified custom stored-output route.
            if (tannerContinuationBytes[0] == 0xFF && tannerContinuationBytes[1] == 0x25 &&
                BitConverter.ToInt32(tannerContinuationBytes, 2) != 0)
                return;
            tannerRelay = adapterOwner.adapter.Address;
            tannerRelayBytes = (byte[])adapterOwner.adapter.Bytes.Clone();
            tannerIndex = index;
        }

        internal static string Diagnostic => owner==null || !owner.publication.IsReady ? diagnostic :
            Volatile.Read(ref tannerState)==TannerFailed ? "Tanner production receipt source is unavailable: "+tannerDiagnostic :
            !WeaponsReady ? "A weapon-workshop production receipt source is unavailable." : string.Empty;
        internal static void ConfirmExternalTannerAdapter() => Volatile.Write(ref externalTannerReady,1);
        internal static NativeSiteSpan[] Spans() => ProductionGoodsReceivedNativeDefinition.Sites
            .Where(s=>s.Name!=ProductionGoodsReceivedNativeDefinition.TannerSiteName)
            .Select(s=>new NativeSiteSpan(s.Rva,s.Bytes.Length,NativeSiteMode.Exclusive)).ToArray();
        internal static NativeSiteSpan[] TannerSpans() => ProductionGoodsReceivedNativeDefinition.Sites
            .Where(s=>s.Name==ProductionGoodsReceivedNativeDefinition.TannerSiteName)
            .Select(s=>new NativeSiteSpan(s.Rva,s.Bytes.Length,NativeSiteMode.Exclusive)).ToArray();
        internal static void Initialize(CrusaderLibraryLoadContext context,string hash,ManualLogSource log)
        {
            if(owner!=null) { if(owner.publication.IsReady)return;throw new InvalidOperationException(diagnostic); }
            if(context==null || context.ModuleHandle==IntPtr.Zero || context.Region==null ||
                !string.Equals(hash,WorkerProductionBonusNativeDefinition.ReferenceHash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Production receipts require the audited native image.");
            var candidate=new ProductionGoodsReceivedNativeRuntime(context,log,false,null);owner=candidate;
            try { candidate.Install();diagnostic=string.Empty; }
            catch(Exception error)
            {
                diagnostic=error.Message;
                candidate.group.RollbackUnpublished(()=>
                {
                    // Helper lifetime is published before the source transaction. A
                    // pre-Begin source failure may still roll back its own stubs, but
                    // may neither free this published entry nor mask the real error.
                    if (candidate.adapter != null && !candidate.adapter.IsPublished)
                    { candidate.adapter.RollbackUnpublished(); candidate.adapter=null; }
                });
                throw;
            }
            try { TannerDecisionSubscription=MissionEvents.Initialization.Subscribe(n=>DecideTanner(context,log,n)); }
            catch(Exception error) { FailTanner(log,"no mission notification: "+error.Message); }
        }
        // Rooted for the process; the first mission initialization decides once. Tanner code runs only
        // inside a map's simulation, so before the first map's native load no thread executes the span.
        private static void DecideTanner(CrusaderLibraryLoadContext context,ManualLogSource log,MissionLifecycleNotification notification)
        {
            if(Interlocked.Exchange(ref tannerDecided,1)!=0) return;
            if(Volatile.Read(ref externalTannerReady)!=0)
            {
                Volatile.Write(ref tannerState,TannerExternal);
                NativeApiLog.Info(log,"Production receipt source Tanner: owned by ImprovedHunters' confirmed adapter.");
                return;
            }
            if(notification.Phase!=MissionInitializationPhase.BeforeLoad && notification.Phase!=MissionInitializationPhase.BeforeNativeStart)
            { FailTanner(log,"the first mission notification ("+notification.Phase+") came after native loading began"); return; }
            var tanner=new ProductionGoodsReceivedNativeRuntime(context,log,true,owner);
            // Retain the exact source owner, including a failed/partial publication. Its
            // source embeds the startup owner's process-lifetime shared relay.
            Volatile.Write(ref tannerOwner,tanner);
            try { tanner.Install(); Volatile.Write(ref tannerState,TannerOwned); }
            catch(Exception error)
            {
                tanner.group.RollbackUnpublished(()=>{});
                FailTanner(log,error.Message);
            }
        }
        private static void FailTanner(ManualLogSource log,string reason)
        {
            tannerDiagnostic=reason;Volatile.Write(ref tannerState,TannerFailed);
            NativeApiLog.Error(log,"Production receipt source Tanner unavailable; Tanner deliveries are not reported: "+reason);
        }
        private void Install()
        {
            if(adapterOwner==this)
                adapter=new ProductionGoodsReceivedNativeCode(a=>ProductionGoodsReceivedNativeAssembly.Relay(a,
                    ProductionGoodsReceived.DemandAddress,ProductionGoodsReceived.DeliveryCallbackAddress,
                    image+WeaponProductionNativeDefinition.AddGoodRva));
            // The address can be embedded by later permanent workshop/IH sources. Publish
            // its lifetime before any executable source is published; never free it later.
            foreach(var site in sites)
            {
                if(!NativeInlineStub.SnapshotMatches(context.Memory,site.Rva,site.Bytes) ||
                    !NativeInlineStub.ReadLive(context.ModuleHandle,site.Rva,site.Bytes.Length).AsSpan().SequenceEqual(site.Bytes))
                    throw new InvalidOperationException("Production receipt source differs/occupied: "+site.Name);
                int next=site.Rva+site.Bytes.Length;
                if(!NativeInlineStub.SnapshotMatches(context.Memory,next,site.Next) ||
                    !NativeInlineStub.AcceptsLiveBoundary(NativeInlineStub.ReadLive(context.ModuleHandle,next,Math.Max(14,site.Next.Length)),site.Next))
                    throw new InvalidOperationException("Production receipt continuation differs: "+site.Name);
                if(NativeInlineStub.ProbeDisplacement(image+(uint)site.Rva,site.Bytes.Length)!=site.Bytes.Length)
                    throw new InvalidOperationException("Production receipt backend span differs: "+site.Name);
                Expected(site,image+NativeInlineStub.PreflightStubOffset);
            }
            group.Claim(sites.Select(s=>new NativeSiteSpan(s.Rva,s.Bytes.Length,NativeSiteMode.Exclusive)).ToArray());
            group.Begin(context.Region);
            for(int i=0;i<sites.Length;i++)
            {
                var site=sites[i];handles[i]=new HookHandle<X64InlineHook>();
                group.Add(handles[i],image+(uint)site.Rva,
                    (a,original,back)=>ProductionGoodsReceivedNativeAssembly.Source(a,original,back,image,PreparedAdapterAddress),site.Bytes.Length);
            }
            var result=group.Commit("Production receipt source preparation incomplete.");
            for(int i=0;i<sites.Length;i++)
            {
                Verify(i,false);
                if (sites[i].Name == ProductionGoodsReceivedNativeDefinition.TannerSiteName) PrepareTannerSource(i);
            }
            if(adapterOwner==this) adapter.Publish();
            group.Publish(result,this,()=> {
                for(int i=0;i<sites.Length;i++) Verify(i,true);
                if (tannerIndex >= 0 && !TannerSourceMatches())
                    throw new InvalidOperationException("The prepared Tanner receipt route changed during publication.");
            });
            foreach(var site in sites)
                NativeApiLog.Info(log,$"Production receipt source {site.Name}: reference-rva=0x{site.Rva:X}, length={site.Bytes.Length}.");
        }
        private byte[] Expected(ProductionGoodsReceivedNativeDefinition.Site site,ulong stub) =>
            NativeInlineStub.ExpectedStub(image+(uint)site.Rva,site.Bytes,site.Bytes.Length,
                (a,original,back)=>ProductionGoodsReceivedNativeAssembly.Source(a,original,back,image,PreparedAdapterAddress),stub,
                "Production receipt source boundary differs.");
        private void Verify(int index,bool installed)
        {
            var site=sites[index];
            NativeInlineStub.Verify(handles[index],image+(uint)site.Rva,site.Bytes.Length,installed,stub=>Expected(site,stub),
                check=>"Production receipt "+check+" differs: "+site.Name);
        }
    }
}
