using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Bootstrap;
using BepInEx.Logging;
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
        private static string diagnostic="Production receipt native sources have not initialized.";
        private static int externalTannerReady;
        private readonly CrusaderLibraryLoadContext context;
        private readonly ulong image;
        private readonly ManualLogSource log;
        private readonly bool externalTanner;
        private readonly ProductionGoodsReceivedNativeDefinition.Site[] sites;
        private readonly HookHandle<X64InlineHook>[] handles;
        private readonly NativeHookPublicationState publication=new NativeHookPublicationState();
        private readonly NativeInlineStubGroup group;
        private ProductionGoodsReceivedNativeCode adapter;
        private ProductionGoodsReceivedNativeRuntime(CrusaderLibraryLoadContext context, ManualLogSource log)
        {
            this.context=context;this.log=log;image=unchecked((ulong)context.ModuleHandle.ToInt64());
            externalTanner=Chainloader.PluginInfos.ContainsKey("ImprovedHunters_Serp");
            sites=ProductionGoodsReceivedNativeDefinition.Sites.Where(s=>s.Name!="Tanner" || !externalTanner).ToArray();
            handles=new HookHandle<X64InlineHook>[sites.Length];
            group=new NativeInlineStubGroup(ProductionGoodsReceivedNativeDefinition.SiteGroup,publication);
        }
        internal static ulong AdapterAddress => owner?.adapter?.IsPublished==true ? owner.PreparedAdapterAddress : 0;
        private ulong PreparedAdapterAddress => adapter==null ? 0 : unchecked((ulong)adapter.Address.ToInt64());
        internal static bool IsAvailable => owner!=null && owner.publication.IsReady &&
            (!owner.externalTanner || Volatile.Read(ref externalTannerReady)!=0) && WeaponsReady;
        private static bool WeaponsReady =>
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_FLETCHERS_WORKSHOP) &&
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_POLETURNERS_WORKSHOP) &&
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_BLACKSMITHS_WORKSHOP) &&
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_ARMOURERS_WORKSHOP) &&
            WeaponProductionNativeRuntime.IsAvailable(eStructs.STRUCT_TANNERS_WORKSHOP);
        internal static string Diagnostic => owner==null || !owner.publication.IsReady ? diagnostic :
            owner.externalTanner && Volatile.Read(ref externalTannerReady)==0 ? "ImprovedHunters Tanner receipt adapter is unavailable." :
            !WeaponsReady ? "A weapon-workshop production receipt source is unavailable." : string.Empty;
        internal static void ConfirmExternalTannerAdapter() => Volatile.Write(ref externalTannerReady,1);
        internal static NativeSiteSpan[] Spans() => ProductionGoodsReceivedNativeDefinition.Sites
            .Select(s=>new NativeSiteSpan(s.Rva,s.Bytes.Length,NativeSiteMode.Exclusive)).ToArray();
        internal static void Initialize(CrusaderLibraryLoadContext context,string hash,ManualLogSource log)
        {
            if(owner!=null) { if(owner.publication.IsReady)return;throw new InvalidOperationException(diagnostic); }
            if(context==null || context.ModuleHandle==IntPtr.Zero || context.Region==null ||
                !string.Equals(hash,WorkerProductionBonusNativeDefinition.ReferenceHash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Production receipts require the audited native image.");
            var candidate=new ProductionGoodsReceivedNativeRuntime(context,log);owner=candidate;
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
        }
        private void Install()
        {
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
            for(int i=0;i<sites.Length;i++) Verify(i,false);
            adapter.Publish();
            group.Publish(result,this,()=> { for(int i=0;i<sites.Length;i++)Verify(i,true); });
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
