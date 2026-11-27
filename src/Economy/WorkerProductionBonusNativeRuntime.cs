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
using APIShared.Internal;

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
        private readonly NativeInlineStubGroup group;

        internal static bool IsAvailable
        {
            get { var current=Volatile.Read(ref owner); return current!=null && current.publication.IsReady; }
        }
        internal static string Diagnostic => Volatile.Read(ref diagnostic);
        internal static ulong PercentAddress => owner==null ? 0 : unchecked((ulong)owner.coefficient.ToInt64());
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
                        candidate.group.RollbackUnpublished(()=>
                        {
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
            group=new NativeInlineStubGroup(WorkerProductionBonusNativeDefinition.SiteGroup,publication);
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
                if(NativeInlineStub.ProbeDisplacement(image+(uint)site.Rva,site.Length)!=site.Length)
                    throw new InvalidOperationException("Worker production backend span differs: "+site.Name);
                WorkerProductionBonusNativeAssembly.ExpectedStub(site,image,image+NativeInlineStub.PreflightStubOffset,
                    unchecked((ulong)coefficient.ToInt64()),image+WorkerProductionBonusNativeDefinition.ModeRva,
                    image+WorkerProductionBonusNativeDefinition.UnitManagerRva,image+WorkerProductionBonusNativeDefinition.CurrentUnitRva,
                    hot:WorkerProductionRuntime.HotAddresses(image));
                Info("Worker production hook resolved: name="+site.Name+"; method="+resolution.Method+
                    "; rva=0x"+site.Rva.ToString("X")+"; span="+site.Length+"; continuation=0x"+(site.Rva+site.Length).ToString("X")+".");
            }
            VerifyCompleteSnapshotFunctions();
            group.Claim(Spans());
            group.Begin(context.Region);
            for(int n=0;n<handles.Length;n++)
            {
                var site=WorkerProductionBonusNativeDefinition.Sites[n]; handles[n]=new HookHandle<X64InlineHook>();
                group.Add(handles[n],image+(uint)site.Rva,(a,original,back)=>Generate(a,original,back,site),site.Length);
            }
            var result=group.Commit("Worker production native preparation was incomplete.");
            for(int n=0;n<handles.Length;n++)
            {
                var site=WorkerProductionBonusNativeDefinition.Sites[n];
                if(!NativeInlineStub.IsPrepared(handles[n],false,image+(uint)site.Rva,site.Length))
                    throw new InvalidOperationException("Worker production prepared backend contract differs: "+site.Name);
                VerifyPreparedStub(site,handles[n].Hook);
            }
            group.Publish(result,this,ValidateInstalled);
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
                image+WorkerProductionBonusNativeDefinition.CurrentUnitRva,hot:WorkerProductionRuntime.HotAddresses(image));
        private void VerifyPreparedStub(WorkerProductionBonusNativeSite site,X64InlineHook hook)
        {
            byte[] expected=WorkerProductionBonusNativeAssembly.ExpectedStub(site,image,unchecked((ulong)hook.StubAddress.ToInt64()),
                unchecked((ulong)coefficient.ToInt64()),image+WorkerProductionBonusNativeDefinition.ModeRva,
                image+WorkerProductionBonusNativeDefinition.UnitManagerRva,image+WorkerProductionBonusNativeDefinition.CurrentUnitRva,
                hot:WorkerProductionRuntime.HotAddresses(image));
            if(!NativeInlineStub.StubMatches(hook.StubAddress,expected)) throw new InvalidOperationException("Worker production generated stub differs: "+site.Name);
        }
        private void ValidateInstalled()
        {
            for(int n=0;n<handles.Length;n++)
            {
                var site=WorkerProductionBonusNativeDefinition.Sites[n];var hook=handles[n].Hook;var address=(IntPtr)hook.TargetAddress;
                if(!hook.IsInstalled || hook.DisplacedByteCount!=site.Length || hook.TargetAddress!=image+(uint)site.Rva ||
                    !NativeInlineStub.JumpMatches(address,hook.StubAddress))
                    throw new InvalidOperationException("Worker production installed jump differs: "+site.Name);
                if(!NativeInlineStub.PaddingMatches(address,site.Length))
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
                    string hex=Sha256Hex.Format(digest);
                    if(!string.Equals(hex,function.Sha256,StringComparison.Ordinal))
                        throw new InvalidOperationException("Worker production complete control-flow snapshot differs at RVA 0x"+function.Rva.ToString("X")+".");
                }
        }
        private void VerifySnapshot(int rva,byte[] expected)
        {
            if(!NativeInlineStub.SnapshotMatches(context.Memory,rva,expected))
                throw new InvalidOperationException("Worker production snapshot bytes differ at RVA 0x"+rva.ToString("X")+".");
        }
        private void VerifyLive(int rva,byte[] expected)
        {
            var actual=NativeInlineStub.ReadLive(context.ModuleHandle,rva,expected.Length);
            if(!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Worker production span is occupied or changed at RVA 0x"+rva.ToString("X")+".");
        }
        private void CheckBoundary(int rva,byte[] expected)
        {
            VerifySnapshot(rva,expected);
            // A foreign hook may begin exactly at a proved continuation. Our span neither
            // replaces nor decodes it, and the native jump enters that hook unchanged.
            if(NativeInlineStub.AcceptsLiveBoundary(NativeInlineStub.ReadLive(context.ModuleHandle,rva,Math.Max(expected.Length,14)),expected)) return;
            throw new InvalidOperationException("Worker production continuation changed inside an instruction at RVA 0x"+rva.ToString("X")+".");
        }
        private byte[] FirstInstruction(int rva,string hex)
        {
            byte[] bytes=WorkerProductionBonusNativeDefinition.Hex(hex); VerifySnapshot(rva,bytes);
            var decoded=WorkerProductionBonusNativeDefinition.Decode(bytes,image+(uint)rva);
            return bytes.AsSpan(0,decoded[0].Length).ToArray();
        }
        private static string Pattern(byte[] bytes) => BitConverter.ToString(bytes).Replace('-',' ');
        private void Info(string text) => NativeApiLog.Info(log, text);
    }
}
