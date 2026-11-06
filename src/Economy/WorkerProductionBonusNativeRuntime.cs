using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.Economy
{
    // One startup transaction owns every production-dependent span. Its coefficient stays
    // 50 and its separate history-ready DWORD stays zero until complete validation. Thus
    // even old negative save state follows Vanilla during a partial publication failure.
    // Published allocations/hooks are never torn down.
    internal sealed unsafe class WorkerProductionBonusNativeRuntime
    {
        private static readonly object Sync=new object();
        private static WorkerProductionBonusNativeRuntime owner;
        private static bool attempted;
        private static string diagnostic="Worker production bonus has not initialized.";
        private readonly CrusaderLibraryLoadContext context;
        private readonly ManualLogSource log;
        private readonly ulong image;
        private readonly IntPtr coefficient;
        private readonly HookHandle<X64InlineHook>[] handles;
        private readonly NativeHookPublicationState publication=new NativeHookPublicationState();
        private HookTransaction transaction;
        private bool claimed;

        internal static bool IsAvailable
        {
            get { var current=Volatile.Read(ref owner); return current!=null && current.publication.IsReady; }
        }
        internal static string Diagnostic => Volatile.Read(ref diagnostic);
        internal static void Initialize(CrusaderLibraryLoadContext context,string hash,ManualLogSource log)
        {
            lock(Sync)
            {
                if(attempted) { if(IsAvailable) return; throw new InvalidOperationException(Diagnostic); }
                attempted=true;
                try
                {
                    if(context==null || context.ModuleHandle==IntPtr.Zero || context.Memory.Length==0 || context.Region==null)
                        throw new InvalidOperationException("Worker production bonus requires the native LibraryLoaded context.");
                    if(!string.Equals(hash,WorkerProductionBonusNativeDefinition.ReferenceHash,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Worker production worker/ox/footer layouts require the audited native hash.");
                    var candidate=new WorkerProductionBonusNativeRuntime(context,log);
                    Volatile.Write(ref owner,candidate);
                    try { candidate.Install(); }
                    catch
                    {
                        candidate.publication.RollbackUnpublished(candidate.transaction,()=>
                        {
                            if(candidate.claimed) ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership,WorkerProductionBonusNativeDefinition.SiteGroup);
                            Marshal.FreeHGlobal(candidate.coefficient); Volatile.Write(ref owner,null);
                        });
                        throw;
                    }
                    Volatile.Write(ref diagnostic,"Worker production bonus native contracts are ready.");
                }
                catch(Exception error)
                {
                    Volatile.Write(ref diagnostic,error.Message);
                    throw;
                }
            }
        }

        internal static void PublishPercent(int percent)
        {
            if(percent < -100 || percent > 1000) throw new ArgumentOutOfRangeException(nameof(percent));
            var current=Volatile.Read(ref owner);
            if(current==null || !current.publication.IsReady) throw new InvalidOperationException(Diagnostic);
            // Marshal.AllocHGlobal is naturally aligned; all native readers use one DWORD.
            Volatile.Write(ref *(int*)current.coefficient,percent);
        }

        private WorkerProductionBonusNativeRuntime(CrusaderLibraryLoadContext context,ManualLogSource log)
        {
            this.context=context; this.log=log; image=unchecked((ulong)context.ModuleHandle.ToInt64());
            coefficient=Marshal.AllocHGlobal(WorkerProductionBonusNativeDefinition.StateSize);
            Marshal.WriteInt32(coefficient,50); Marshal.WriteInt32(coefficient,WorkerProductionBonusNativeDefinition.ReadyOffset,0);
            handles=new HookHandle<X64InlineHook>[WorkerProductionBonusNativeDefinition.Sites.Count];
        }

        private void Install()
        {
            // Resolve each own span first, retaining the required semantic pattern fallback if
            // reference bytes differ. Layouts/continuations remain deliberately hash bound.
            foreach(var site in WorkerProductionBonusNativeDefinition.Sites)
            {
                byte[] signature=WorkerProductionBonusNativeDefinition.Hex(site.SignatureHex);
                var resolution=Internal.NativePatternResolver.ResolveUnique(context.Memory,Pattern(signature),site.Rva,true,
                    "worker production "+site.Name,log);
                if(resolution.Rva!=site.Rva) throw new InvalidOperationException("Worker production fixed-layout site moved: "+site.Name);
                VerifySnapshot(site.Rva,site.OriginalBytes);
                VerifyLive(site.Rva,site.OriginalBytes);
                CheckBoundary(site.Rva+site.Length,WorkerProductionBonusNativeDefinition.Hex(site.ContinuationHex));
                if(site.FooterRva!=0) CheckBoundary(site.FooterRva,FirstInstruction(site.FooterRva,site.FooterHex));
                using(var probe=new X64InlineHook(image+(uint)site.Rva,site.Length))
                    if(probe.DisplacedByteCount!=site.Length)
                        throw new InvalidOperationException("Worker production backend span differs: "+site.Name);
                WorkerProductionBonusNativeAssembly.ExpectedStub(site,image,image+0x20000000,
                    unchecked((ulong)coefficient.ToInt64()),image+WorkerProductionBonusNativeDefinition.ModeRva,
                    image+WorkerProductionBonusNativeDefinition.UnitManagerRva,image+WorkerProductionBonusNativeDefinition.CurrentUnitRva);
                Info("Worker production hook resolved: name="+site.Name+"; method="+resolution.Method+
                    "; rva=0x"+site.Rva.ToString("X")+"; span="+site.Length+"; continuation=0x"+(site.Rva+site.Length).ToString("X")+".");
            }
            VerifyCompleteSnapshotFunctions();
            var spans=Spans();
            ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership,WorkerProductionBonusNativeDefinition.SiteGroup,
                spans,reason=>new InvalidOperationException(reason)); claimed=true;
            transaction=new HookTransaction(context.Region,SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                new HookTransactionOptions { InstallOnCommit=false,FailureMode=TransactionFailureMode.RollbackAndThrow,OwnsHooks=true });
            for(int n=0;n<handles.Length;n++)
            {
                var site=WorkerProductionBonusNativeDefinition.Sites[n]; handles[n]=new HookHandle<X64InlineHook>();
                transaction.AddInline(handles[n],HookTarget.FromAddress(image+(uint)site.Rva),
                    (a,original,back)=>Generate(a,original,back,site),hookSize:site.Length);
            }
            var result=transaction.Commit();
            if(!result.IsCompleteSuccess) throw new InvalidOperationException("Worker production native preparation was incomplete.");
            for(int n=0;n<handles.Length;n++)
            {
                var site=WorkerProductionBonusNativeDefinition.Sites[n]; var hook=handles[n].Hook;
                if(!handles[n].Success || hook.IsInstalled || hook.DisplacedByteCount!=site.Length ||
                    hook.TargetAddress!=image+(uint)site.Rva || hook.StubAddress==IntPtr.Zero)
                    throw new InvalidOperationException("Worker production prepared backend contract differs: "+site.Name);
                VerifyPreparedStub(site,hook);
            }
            NativeHookPublication.Enable(transaction,result,publication,this,ValidateInstalled);
            Info("Worker production native service ready: all spans verified; logical publication remains at coefficient 50 until a complete service registration; native stubs; process lifetime.");
        }

        internal static NativeSites.NativeSiteSpan[] Spans()
        {
            var spans=new List<NativeSites.NativeSiteSpan>();
            foreach(var site in WorkerProductionBonusNativeDefinition.Sites)
                spans.Add(new NativeSites.NativeSiteSpan(site.Rva,site.Length,NativeSites.NativeSiteMode.Exclusive));
            return spans.ToArray();
        }
        private void Generate(Assembler a,ReadOnlySpan<Instruction> original,ulong back,WorkerProductionBonusNativeSite site) =>
            WorkerProductionBonusNativeAssembly.Generate(a,original,back,site,image,unchecked((ulong)coefficient.ToInt64()),
                image+WorkerProductionBonusNativeDefinition.ModeRva,image+WorkerProductionBonusNativeDefinition.UnitManagerRva,
                image+WorkerProductionBonusNativeDefinition.CurrentUnitRva);
        private void VerifyPreparedStub(WorkerProductionBonusNativeSite site,X64InlineHook hook)
        {
            byte[] expected=WorkerProductionBonusNativeAssembly.ExpectedStub(site,image,unchecked((ulong)hook.StubAddress.ToInt64()),
                unchecked((ulong)coefficient.ToInt64()),image+WorkerProductionBonusNativeDefinition.ModeRva,
                image+WorkerProductionBonusNativeDefinition.UnitManagerRva,image+WorkerProductionBonusNativeDefinition.CurrentUnitRva);
            byte[] actual=new byte[expected.Length]; Marshal.Copy(hook.StubAddress,actual,0,actual.Length);
            if(!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Worker production generated stub differs: "+site.Name);
        }
        private void ValidateInstalled()
        {
            for(int n=0;n<handles.Length;n++)
            {
                var site=WorkerProductionBonusNativeDefinition.Sites[n];var hook=handles[n].Hook;var address=(IntPtr)hook.TargetAddress;
                if(!hook.IsInstalled || hook.DisplacedByteCount!=site.Length || hook.TargetAddress!=image+(uint)site.Rva ||
                    Marshal.ReadByte(address)!=0xFF || Marshal.ReadByte(address,1)!=0x25 || Marshal.ReadInt32(address,2)!=0 ||
                    Marshal.ReadIntPtr(address,6)!=hook.StubAddress)
                    throw new InvalidOperationException("Worker production installed jump differs: "+site.Name);
                for(int k=14;k<site.Length;k++) if(Marshal.ReadByte(address,k)!=0x90)
                    throw new InvalidOperationException("Worker production installed padding differs: "+site.Name);
                VerifyPreparedStub(site,hook);
            }
            // This is the last validation action, before NativeHookPublication marks the
            // public service ready. Every span is installed and checked at this point.
            // A failed/partial publication never enables historical negative save state.
            Volatile.Write(ref *(int*)((byte*)coefficient+WorkerProductionBonusNativeDefinition.ReadyOffset),1);
        }
        private void VerifyCompleteSnapshotFunctions()
        {
            using(var sha=SHA256.Create())
                foreach(var function in WorkerProductionBonusNativeDefinition.Functions)
                {
                    if(function.Rva<0 || function.Rva>context.Memory.Length-function.Length)
                        throw new InvalidOperationException("Worker production snapshot function is out of image.");
                    byte[] digest=sha.ComputeHash(context.Memory.Slice(function.Rva,function.Length).ToArray());
                    string hex=BitConverter.ToString(digest).Replace("-",string.Empty);
                    if(!string.Equals(hex,function.Sha256,StringComparison.Ordinal))
                        throw new InvalidOperationException("Worker production complete control-flow snapshot differs at RVA 0x"+function.Rva.ToString("X")+".");
                }
        }
        private void VerifySnapshot(int rva,byte[] expected)
        {
            if(rva<0 || rva>context.Memory.Length-expected.Length || !context.Memory.Slice(rva,expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException("Worker production snapshot bytes differ at RVA 0x"+rva.ToString("X")+".");
        }
        private void VerifyLive(int rva,byte[] expected)
        {
            var actual=Live(rva,expected.Length);
            if(!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Worker production span is occupied or changed at RVA 0x"+rva.ToString("X")+".");
        }
        private void CheckBoundary(int rva,byte[] expected)
        {
            VerifySnapshot(rva,expected);
            var actual=Live(rva,Math.Max(expected.Length,14));
            if(actual.AsSpan(0,expected.Length).SequenceEqual(expected)) return;
            // A foreign hook may begin exactly at a proved continuation. Our span neither
            // replaces nor decodes it, and the native jump enters that hook unchanged.
            if(actual[0]==0xE9 || (actual[0]==0xFF && actual[1]==0x25)) return;
            throw new InvalidOperationException("Worker production continuation changed inside an instruction at RVA 0x"+rva.ToString("X")+".");
        }
        private byte[] Live(int rva,int length)
        { var result=new byte[length]; Marshal.Copy(context.ModuleHandle+rva,result,0,length); return result; }
        private byte[] FirstInstruction(int rva,string hex)
        {
            byte[] bytes=WorkerProductionBonusNativeDefinition.Hex(hex); VerifySnapshot(rva,bytes);
            var decoded=WorkerProductionBonusNativeDefinition.Decode(bytes,image+(uint)rva);
            return bytes.AsSpan(0,decoded[0].Length).ToArray();
        }
        private static string Pattern(byte[] bytes) => BitConverter.ToString(bytes).Replace('-',' ');
        private void Info(string text) { try { log?.LogInfo($"{DateTime.Now:HH:mm:ss.fff} {text}"); } catch { } }
    }
}
