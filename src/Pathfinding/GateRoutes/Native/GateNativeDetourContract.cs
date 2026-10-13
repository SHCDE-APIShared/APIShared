using System;
using System.Runtime.InteropServices;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
namespace APIShared.GateRouting.Native
{
    internal static class GateNativeDetourContract
    {
        internal static readonly NativeDetourBackend Backend = new NativeDetourBackend(new NativeDetourOptions
        { AllowedSchemes=DetourScheme.Indirect, FollowJumps=false, FollowCalls=false, Chaining=HookChainingPolicy.Refuse });
        internal static int Length(int rva)
        {
            switch(rva) { case 0x1232E0:case 0xDC3C0:return 6;case 0x113BC0:return 10; default:throw new InvalidOperationException("Unaudited gate detour"); }
        }
        internal static void Validate<T>(IDetour<T> detour,ulong address,int length,bool installed) where T:Delegate
        {
            var hook=detour as NativeDetour<T>;
            if(hook==null||hook.Scheme!=DetourScheme.Indirect||hook.DisplacedByteCount!=length||hook.TargetAddress!=address||
                hook.PointerSlot==IntPtr.Zero||hook.HookEntryPointAddress==IntPtr.Zero||hook.TrampolineAddress==IntPtr.Zero||hook.IsInstalled!=installed)
                throw new InvalidOperationException("Gate NativeX64 Indirect contract mismatch");
            if(!installed)return;
            IntPtr target=new IntPtr(unchecked((long)address));
            if(Marshal.ReadByte(target)!=0xFF||Marshal.ReadByte(target,1)!=0x25||
                target+6+Marshal.ReadInt32(target,2)!=hook.PointerSlot||Marshal.ReadIntPtr(hook.PointerSlot)!=hook.HookEntryPointAddress)
                throw new InvalidOperationException("Gate native pointer-slot/entry mismatch");
            for(int i=6;i<length;i++)if(Marshal.ReadByte(target,i)!=0x90)throw new InvalidOperationException("Gate native patch padding mismatch");
        }
        internal static void Probe<T>(ulong address,int rva,T callback) where T:Delegate
        {
            // Preparation only, same concrete backend; never enable a probe against the game image.
            var request=new DetourRequest<T>{Name="gate contract preparation",TargetAddress=address,Callback=callback};
            using(var hook=Backend.CreateDetour(in request)) Validate(hook,address,Length(rva),false);
        }
    }
}
