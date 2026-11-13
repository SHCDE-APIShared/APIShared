using System;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Internal;
using Iced.Intel;
using RedBird.X64.Hooks;
using static Iced.Intel.AssemblerRegisters;

internal static partial class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long ReceiptCall(IntPtr manager,int building,int globalId,int good,int amount,int capacity,int add);
    private static void TestProductionReceiptNative()
    {
        IntPtr data=Marshal.AllocHGlobal(80);
        try
        {
            Marshal.Copy(new byte[80],0,data,80);
            ulong target=unchecked((ulong)data.ToInt64());
            Code Recorder(int counter,long result) => new Code(a=> {
                a.mov(r11,target); a.inc(__dword_ptr[r11+counter]);
                a.mov(__qword_ptr[r11+16],rcx); a.mov(__dword_ptr[r11+24],edx);
                a.mov(__dword_ptr[r11+28],r8d); a.mov(__dword_ptr[r11+32],r9d);
                a.mov(eax,__dword_ptr[rsp+40]); a.mov(__dword_ptr[r11+36],eax);
                a.mov(eax,__dword_ptr[rsp+48]); a.mov(__dword_ptr[r11+40],eax);
                a.mov(eax,__dword_ptr[rsp+56]); a.mov(__dword_ptr[r11+44],eax);
                a.mov(rax,unchecked((ulong)result)); a.ret();
            });
            using var vanilla=Recorder(4,0x1020304050607080L);
            using var observer=Recorder(8,0x1122334455667788L);
            using var relay=new Code(a=>ProductionGoodsReceivedNativeAssembly.Relay(a,target,
                unchecked((ulong)observer.Address.ToInt64()),unchecked((ulong)vanilla.Address.ToInt64())));
            var invoke=Marshal.GetDelegateForFunctionPointer<ReceiptCall>(relay.Address);
            foreach(int demand in new[]{0,1,0,1})
            {
                Marshal.WriteInt32(data,demand);Marshal.WriteInt32(data+4,0);Marshal.WriteInt32(data+8,0);
                long result=invoke(new IntPtr(0x12345678),73,-981,17,9,50,1);
                Check(result==(demand==0?0x1020304050607080L:0x1122334455667788L),"Receipt relay preserves full native result.");
                Check(Marshal.ReadInt32(data+4)==(demand==0?1:0) && Marshal.ReadInt32(data+8)==demand,"Demand zero bypasses observer entirely; one target executes exactly once.");
                Check(Marshal.ReadInt64(data+16)==0x12345678 && Marshal.ReadInt32(data+24)==73 &&
                    Marshal.ReadInt32(data+28)==-981 && Marshal.ReadInt32(data+32)==17 &&
                    Marshal.ReadInt32(data+36)==9 && Marshal.ReadInt32(data+40)==50 && Marshal.ReadInt32(data+44)==1,
                    "Receipt tail relay preserves all register and stack arguments.");
            }
            foreach(var site in ProductionGoodsReceivedNativeDefinition.Sites)
            {
                Check(At(site.Rva,site.Bytes.Length).SequenceEqual(site.Bytes),"Receipt source bytes match audited canonical image.");
                Check(At(site.Rva+site.Bytes.Length,site.Next.Length).SequenceEqual(site.Next),"Receipt continuation bytes match canonical image.");
                using var hook=new X64InlineHook(Address(site.Rva),site.Bytes.Length);
                Check(hook.DisplacedByteCount==site.Bytes.Length,"Receipt source actual backend displacement matches full audited instructions.");
                hook.Generate((a,original,back)=>ProductionGoodsReceivedNativeAssembly.Source(a,original,back,Address(0),unchecked((ulong)relay.Address.ToInt64())));
                byte[] expected=NativeInlineStub.ExpectedStub(Address(site.Rva),site.Bytes,site.Bytes.Length,
                    (a,original,back)=>ProductionGoodsReceivedNativeAssembly.Source(a,original,back,Address(0),unchecked((ulong)relay.Address.ToInt64())),
                    unchecked((ulong)hook.StubAddress.ToInt64()),"Receipt test boundary differs.");
                byte[] actual=new byte[expected.Length];Marshal.Copy(hook.StubAddress,actual,0,actual.Length);
                Check(expected.SequenceEqual(actual),"Receipt source backend stub matches production generator exactly.");
            }
        }
        finally { Marshal.FreeHGlobal(data); }
    }
}
