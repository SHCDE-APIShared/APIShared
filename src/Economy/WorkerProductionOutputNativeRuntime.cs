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
        private HookTransaction transaction;
        private bool claimed;
        private WorkerProductionOutputNativeRuntime(CrusaderLibraryLoadContext context,ManualLogSource log)
        { this.context=context;this.log=log;image=unchecked((ulong)context.ModuleHandle.ToInt64());
          handles=new HookHandle<X64InlineHook>[WorkerProductionOutputNativeDefinition.Sites.Count]; }
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
                candidate.publication.RollbackUnpublished(candidate.transaction,()=>{
                    if(candidate.claimed)ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership,WorkerProductionOutputNativeDefinition.SiteGroup);
                });
                throw;
            }
        }
        private void Install()
        {
            CheckSnapshot(0x28A7D,WorkerProductionBonusNativeDefinition.Hex("488D05C0C15408"));
            using(var sha=SHA256.Create()) foreach(var function in WorkerProductionOutputNativeDefinition.Functions)
            {
                byte[] digest=sha.ComputeHash(context.Memory.Slice(function.Rva,function.Length).ToArray());
                if(BitConverter.ToString(digest).Replace("-","")!=function.Sha256)
                    throw new InvalidOperationException("Actual-output full-function audit differs at "+function.Rva.ToString("X"));
            }
            foreach(var definition in WorkerProductionOutputNativeDefinition.Sites)
            {
                var site=definition.Site;CheckSnapshot(site.Rva,site.OriginalBytes);CheckLive(site.Rva,site.OriginalBytes);
                Boundary(site.Rva+site.Length,WorkerProductionBonusNativeDefinition.Hex(site.ContinuationHex));
                if(site.FooterRva!=0) Boundary(site.FooterRva,WorkerProductionBonusNativeDefinition.Hex(site.FooterHex));
                using(var probe=new X64InlineHook(image+(uint)site.Rva,site.Length))
                    if(probe.DisplacedByteCount!=site.Length)throw new InvalidOperationException("Actual-output backend span differs: "+site.Name);
                Expected(definition,image+0x20000000);
            }
            ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership,WorkerProductionOutputNativeDefinition.SiteGroup,
                Spans(),why=>new InvalidOperationException(why));claimed=true;
            transaction=new HookTransaction(context.Region,SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                new HookTransactionOptions { InstallOnCommit=false,FailureMode=TransactionFailureMode.RollbackAndThrow,OwnsHooks=true });
            for(int i=0;i<handles.Length;i++)
            {
                var definition=WorkerProductionOutputNativeDefinition.Sites[i];handles[i]=new HookHandle<X64InlineHook>();
                transaction.AddInline(handles[i],HookTarget.FromAddress(image+(uint)definition.Site.Rva),
                    (a,original,back)=>Generate(a,original,back,definition),hookSize:definition.Site.Length);
            }
            var result=transaction.Commit();if(!result.IsCompleteSuccess)throw new InvalidOperationException("Actual-output preparation incomplete.");
            for(int i=0;i<handles.Length;i++) Verify(i,false);
            NativeHookPublication.Enable(transaction,result,publication,this,()=> { for(int i=0;i<handles.Length;i++)Verify(i,true); });
            NativeApiLog.Info(log, "Actual-output guards prepared and published; world readiness still requires persistence and agreement.");
        }
        private void Generate(Iced.Intel.Assembler a,ReadOnlySpan<Instruction> original,ulong back,WorkerProductionOutputSite site) =>
            WorkerProductionOutputNativeAssembly.Generate(a,original,back,site,image,WorkerProductionBonusNativeRuntime.PercentAddress,
                WorkerProductionRuntime.HotAddresses(image));
        private byte[] Expected(WorkerProductionOutputSite definition,ulong stub)
        {
            var site=definition.Site;var a=new Assembler(64);
            Generate(a,WorkerProductionBonusNativeDefinition.Decode(site.OriginalBytes,image+(uint)site.Rva),image+(uint)(site.Rva+site.Length),definition);
            a.AddUnrestrictedJmp(image+(uint)(site.Rva+site.Length));return WorkerProductionBonusNativeAssembly.Assemble(a,stub);
        }
        private void Verify(int index,bool installed)
        {
            var definition=WorkerProductionOutputNativeDefinition.Sites[index];var site=definition.Site;var hook=handles[index].Hook;
            if(!handles[index].Success || hook.IsInstalled!=installed || hook.TargetAddress!=image+(uint)site.Rva ||
                hook.DisplacedByteCount!=site.Length || hook.StubAddress==IntPtr.Zero)
                throw new InvalidOperationException("Actual-output backend contract differs: "+site.Name);
            byte[] expected=Expected(definition,unchecked((ulong)hook.StubAddress.ToInt64()));var actual=new byte[expected.Length];
            Marshal.Copy(hook.StubAddress,actual,0,actual.Length);
            if(!actual.AsSpan().SequenceEqual(expected))throw new InvalidOperationException("Actual-output generated stub differs: "+site.Name);
            if(installed)
            {
                IntPtr target=(IntPtr)hook.TargetAddress;
                if(Marshal.ReadByte(target)!=0xFF || Marshal.ReadByte(target,1)!=0x25 || Marshal.ReadInt32(target,2)!=0 ||
                    Marshal.ReadIntPtr(target,6)!=hook.StubAddress)throw new InvalidOperationException("Actual-output installed jump differs: "+site.Name);
                for(int i=14;i<site.Length;i++)if(Marshal.ReadByte(target,i)!=0x90)throw new InvalidOperationException("Actual-output padding differs: "+site.Name);
            }
        }
        private void CheckSnapshot(int rva,byte[] bytes)
        {
            if(rva<0 || rva>context.Memory.Length-bytes.Length || !context.Memory.Slice(rva,bytes.Length).SequenceEqual(bytes))
                throw new InvalidOperationException("Actual-output snapshot differs at "+rva.ToString("X"));
        }
        private byte[] Live(int rva,int length) { var bytes=new byte[length];Marshal.Copy(context.ModuleHandle+rva,bytes,0,length);return bytes; }
        private void CheckLive(int rva,byte[] bytes)
        { if(!Live(rva,bytes.Length).AsSpan().SequenceEqual(bytes))throw new InvalidOperationException("Actual-output span occupied at "+rva.ToString("X")); }
        private void Boundary(int rva,byte[] bytes)
        {
            CheckSnapshot(rva,bytes);byte[] live=Live(rva,Math.Max(14,bytes.Length));
            if(live.AsSpan(0,bytes.Length).SequenceEqual(bytes) || live[0]==0xE9 || live[0]==0xFF && live[1]==0x25)return;
            throw new InvalidOperationException("Actual-output continuation changed inside instruction: "+rva.ToString("X"));
        }
    }
}
