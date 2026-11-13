using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using RedBird.X64.Extensions;
using SHCDESE.API.LowLevel;
using APIShared.Internal;

namespace APIShared.Economy
{
    internal sealed class WorkerProductionOutputNativeRuntime
    {
        private static WorkerProductionOutputNativeRuntime owner;
        private static string diagnostic="Actual-output protection has not initialized.";
        private readonly CrusaderLibraryLoadContext context;
        private readonly ulong image;
        private readonly ManualLogSource log;
        private readonly NativeHookPublicationState publication=new NativeHookPublicationState();
        private readonly HookHandle<X64InlineHook>[] handles;
        private readonly NativeInlineStubGroup group;
        private WorkerProductionOutputNativeRuntime(CrusaderLibraryLoadContext context,ManualLogSource log)
        { this.context=context;this.log=log;image=unchecked((ulong)context.ModuleHandle.ToInt64());
          handles=new HookHandle<X64InlineHook>[WorkerProductionOutputNativeDefinition.Sites.Count];
          group=new NativeInlineStubGroup(WorkerProductionOutputNativeDefinition.SiteGroup,publication); }
        internal static bool IsAvailable => owner!=null && owner.publication.IsReady;
        internal static string Diagnostic => diagnostic;
        internal static NativeSites.NativeSiteSpan[] Spans()
        {
            var spans=new List<NativeSites.NativeSiteSpan>();
            foreach(var definition in WorkerProductionOutputNativeDefinition.Sites)
                spans.Add(new NativeSites.NativeSiteSpan(definition.Site.Rva,definition.Site.Length,NativeSites.NativeSiteMode.Exclusive));
            spans.Add(new NativeSites.NativeSiteSpan(0x28A7D,7,NativeSites.NativeSiteMode.Observe));
            return spans.ToArray();
        }
        internal static void Initialize(CrusaderLibraryLoadContext context,string hash,ManualLogSource log)
        {
            if(owner!=null) { if(IsAvailable)return;throw new InvalidOperationException(diagnostic); }
            if(!WorkerProductionBonusNativeRuntime.IsAvailable || WorkerProductionBonusNativeRuntime.PercentAddress==0)
                throw new InvalidOperationException("Actual-output guards require the complete fixed-bonus coefficient/readiness owner.");
            if(!string.Equals(hash,WorkerProductionBonusNativeDefinition.ReferenceHash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Actual output requires the audited native image.");
            var candidate=new WorkerProductionOutputNativeRuntime(context,log);owner=candidate;
            try { candidate.Install();diagnostic=string.Empty; }
            catch(Exception error)
            {
                diagnostic=error.Message;
                candidate.group.RollbackUnpublished(null);
                throw;
            }
        }
        private void Install()
        {
            CheckSnapshot(0x28A7D,WorkerProductionBonusNativeDefinition.Hex("488D05C0C15408"));
            using(var sha=SHA256.Create()) foreach(var function in WorkerProductionOutputNativeDefinition.Functions)
            {
                byte[] digest=sha.ComputeHash(context.Memory.Slice(function.Rva,function.Length).ToArray());
                if(Sha256Hex.Format(digest)!=function.Sha256)
                    throw new InvalidOperationException("Actual-output full-function audit differs at "+function.Rva.ToString("X"));
            }
            foreach(var definition in WorkerProductionOutputNativeDefinition.Sites)
            {
                var site=definition.Site;CheckSnapshot(site.Rva,site.OriginalBytes);CheckLive(site.Rva,site.OriginalBytes);
                Boundary(site.Rva+site.Length,WorkerProductionBonusNativeDefinition.Hex(site.ContinuationHex));
                if(site.FooterRva!=0) Boundary(site.FooterRva,WorkerProductionBonusNativeDefinition.Hex(site.FooterHex));
                if(NativeInlineStub.ProbeDisplacement(image+(uint)site.Rva,site.Length)!=site.Length)
                    throw new InvalidOperationException("Actual-output backend span differs: "+site.Name);
                Expected(definition,image+NativeInlineStub.PreflightStubOffset);
            }
            group.Claim(Spans());
            group.Begin(context.Region);
            for(int i=0;i<handles.Length;i++)
            {
                var definition=WorkerProductionOutputNativeDefinition.Sites[i];handles[i]=new HookHandle<X64InlineHook>();
                group.Add(handles[i],image+(uint)definition.Site.Rva,(a,original,back)=>Generate(a,original,back,definition),definition.Site.Length);
            }
            var result=group.Commit("Actual-output preparation incomplete.");
            for(int i=0;i<handles.Length;i++) Verify(i,false);
            group.Publish(result,this,()=> { for(int i=0;i<handles.Length;i++)Verify(i,true); });
            NativeApiLog.Info(log, "Actual-output guards prepared and published; world readiness still requires persistence and agreement.");
        }
        private void Generate(Iced.Intel.Assembler a,ReadOnlySpan<Instruction> original,ulong back,WorkerProductionOutputSite site) =>
            WorkerProductionOutputNativeAssembly.Generate(a,original,back,site,image,WorkerProductionBonusNativeRuntime.PercentAddress,
                WorkerProductionRuntime.HotAddresses(image));
        private byte[] Expected(WorkerProductionOutputSite definition,ulong stub)
        {
            var site=definition.Site;
            return NativeInlineStub.ExpectedStub(image+(uint)site.Rva,site.OriginalBytes,site.Length,
                (a,original,back)=>Generate(a,original,back,definition),stub,WorkerProductionBonusNativeDefinition.BoundaryError);
        }
        private void Verify(int index,bool installed)
        {
            var definition=WorkerProductionOutputNativeDefinition.Sites[index];var site=definition.Site;
            NativeInlineStub.Verify(handles[index],image+(uint)site.Rva,site.Length,installed,stub=>Expected(definition,stub),check=>
            {
                switch(check)
                {
                    case NativeInlineStub.Check.Backend: return "Actual-output backend contract differs: "+site.Name;
                    case NativeInlineStub.Check.Stub: return "Actual-output generated stub differs: "+site.Name;
                    case NativeInlineStub.Check.Jump: return "Actual-output installed jump differs: "+site.Name;
                    default: return "Actual-output padding differs: "+site.Name;
                }
            });
        }
        private void CheckSnapshot(int rva,byte[] bytes)
        {
            if(!NativeInlineStub.SnapshotMatches(context.Memory,rva,bytes))
                throw new InvalidOperationException("Actual-output snapshot differs at "+rva.ToString("X"));
        }
        private void CheckLive(int rva,byte[] bytes)
        {
            if(!NativeInlineStub.ReadLive(context.ModuleHandle,rva,bytes.Length).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("Actual-output span occupied at "+rva.ToString("X"));
        }
        private void Boundary(int rva,byte[] bytes)
        {
            CheckSnapshot(rva,bytes);
            if(NativeInlineStub.AcceptsLiveBoundary(NativeInlineStub.ReadLive(context.ModuleHandle,rva,Math.Max(14,bytes.Length)),bytes))return;
            throw new InvalidOperationException("Actual-output continuation changed inside instruction: "+rva.ToString("X"));
        }
    }
}
