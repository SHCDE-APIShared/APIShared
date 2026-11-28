using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using APIShared;
using System.Runtime.InteropServices;
using APIShared.Internal;
using APIShared.Units;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.X64.Hooks;
using SHCDESE.Interop.Enums;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class UnitRelocationTests
    {
        [TestMethod]
        public void CanonicalImageMatchesEntryCallerAndHasNoIncomingEdgeInsidePublishedSpan()
        {
            byte[] file=File.ReadAllBytes(Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data","Plugins","x86_64","CrusaderDE.dll"));
            using(var sha=SHA256.Create()) Assert.AreEqual(ApiSharedRuntime.SupportedHash,BitConverter.ToString(sha.ComputeHash(file)).Replace("-",""));
            CollectionAssert.AreEqual(UnitRelocationNative.Entry,file.Skip(Raw(file,UnitRelocationNative.EntryRva)).Take(16).ToArray());
            var entry=Decoder.Create(64,new ByteArrayCodeReader(UnitRelocationNative.Entry));entry.IP=0x180000000UL+UnitRelocationNative.EntryRva;
            int count=0;while(entry.IP<0x180000000UL+UnitRelocationNative.EntryRva+16){Assert.IsFalse(entry.Decode().IsInvalid);count++;}
            Assert.AreEqual(6,count);Assert.AreEqual(0x180000000UL+UnitRelocationNative.EntryRva+16,entry.IP);
            var caller=Decoder.Create(64,new ByteArrayCodeReader(file.Skip(Raw(file,UnitRelocationNative.StandingReturnRva-5)).Take(5).ToArray()));
            caller.IP=0x180000000UL+UnitRelocationNative.StandingReturnRva-5;
            var call=caller.Decode();Assert.AreEqual(Mnemonic.Call,call.Mnemonic);Assert.AreEqual(0x180000000UL+UnitRelocationNative.EntryRva,call.NearBranchTarget);
            Assert.AreEqual(0x180000000UL+UnitRelocationNative.StandingReturnRva,call.NextIP);
            int pe=BitConverter.ToInt32(file,0x3C),optional=pe+24;
            int pdata=Raw(file,BitConverter.ToInt32(file,optional+112+3*8)),length=BitConverter.ToInt32(file,optional+116+3*8),functions=0;
            for(int index=0;index+12<=length;index+=12)
            {
                int begin=BitConverter.ToInt32(file,pdata+index),end=BitConverter.ToInt32(file,pdata+index+4);
                if(begin==0||end<=begin)continue;
                var decoder=Decoder.Create(64,new ByteArrayCodeReader(file.Skip(Raw(file,begin)).Take(end-begin).ToArray()));decoder.IP=0x180000000UL+(uint)begin;
                while(decoder.IP<0x180000000UL+(uint)end)
                {
                    var instruction=decoder.Decode();
                    if(instruction.Op0Kind==OpKind.NearBranch64)
                        Assert.IsFalse(instruction.NearBranchTarget>0x180000000UL+UnitRelocationNative.EntryRva&&
                            instruction.NearBranchTarget<0x180000000UL+UnitRelocationNative.EntryRva+16,"incoming branch into relocation displaced instructions");
                }
                functions++;
            }
            Assert.IsTrue(functions>0,"canonical unwind function map examined");
        }
        private static int Raw(byte[] file,int rva)
        {
            int pe=BitConverter.ToInt32(file,0x3C),section=pe+24+BitConverter.ToUInt16(file,pe+20);
            for(int i=0;i<BitConverter.ToUInt16(file,pe+6);i++)
            {
                int at=section+i*40,start=BitConverter.ToInt32(file,at+12),size=BitConverter.ToInt32(file,at+16);
                if(rva>=start&&rva<start+size)return BitConverter.ToInt32(file,at+20)+rva-start;
            }
            throw new InvalidOperationException("No raw RVA in canonical image.");
        }

        [TestMethod]
        public void ExactTypeDemandControlsNativeCallbackAndPreservesOriginalAbi()
        {
            Assert.AreEqual(8,IntPtr.Size,"native fixture requires x64 host");
            using (var fixture = new NativeFixture())
            {
                foreach (bool veto in new[] { false, true })
                {
                    fixture.Prepare(1, (ushort)eChimps.CHIMP_TYPE_LORD, true, veto);
                    fixture.Run(false); var reference = fixture.Snapshot();
                    fixture.ClearResults(); fixture.Run(true); var actual = fixture.Snapshot();
                    Assert.AreEqual(1, fixture.Read(156), "callback exactly once for interested type");
                    Assert.AreEqual(veto ? 0 : 1, fixture.Read(152), "one original continuation or a complete veto");
                    Assert.AreEqual(fixture.Manager, fixture.Read64(168), "callback manager");
                    Assert.AreEqual(1UL, fixture.Read64(176), "callback ID");
                    Assert.AreEqual(37UL, fixture.Read64(184), "callback X");
                    Assert.AreEqual(41UL, fixture.Read64(192), "callback Y");
                    Assert.AreEqual(0xFEDCUL, fixture.Read64(200), "fifth argument retains all sixteen height bits");
                    Assert.AreEqual(fixture.ExpectedCaller, fixture.Read64(208), "original caller IP, not callback return");
                    Assert.AreEqual(8UL, fixture.Read64(248) & 15, "Win64 callback entry stack alignment");
                    fixture.AssertRegistersAndXmm(); fixture.AssertBalancedStack();
                    if (!veto)
                    {
                        Assert.AreEqual(0xFEDC, fixture.Read(160), "fifth argument also reaches original body");
                        Assert.AreEqual(BitConverter.ToUInt64(reference, 128) & 0x8C5UL,
                            BitConverter.ToUInt64(actual, 128) & 0x8C5UL, "pass-through flags match pristine original");
                    }
                    else Assert.AreEqual(0x45UL, fixture.Read64(128) & 0x8C5UL, "veto restores caller flags");
                }
                foreach (var sample in new[] {
                    new Sample(1, (ushort)eChimps.CHIMP_TYPE_LORD, false, false),
                    new Sample(1, (ushort)eChimps.CHIMP_TYPE_SWORDSMAN, false, false),
                    new Sample(0, (ushort)eChimps.CHIMP_TYPE_LORD, true, false),
                    new Sample(-1, (ushort)eChimps.CHIMP_TYPE_LORD, true, false),
                    new Sample(10000, (ushort)eChimps.CHIMP_TYPE_LORD, true, false),
                    new Sample(1, (ushort)eChimps.CHIMP_TYPE_LORD, true, true) })
                {
                    fixture.Prepare(sample.Id, sample.Type, sample.Interested, true, sample.ForeignManager);
                    if(sample.Type!=(ushort)eChimps.CHIMP_TYPE_LORD) fixture.SetInterest((ushort)eChimps.CHIMP_TYPE_LORD,true);
                    fixture.Run(false); ulong flags = fixture.Read64(128);
                    fixture.ClearResults(); fixture.Run(true);
                    Assert.AreEqual(0, fixture.Read(156), "irrelevant/invalid input never crosses callback boundary");
                    Assert.AreEqual(1, fixture.Read(152), "irrelevant/invalid input keeps original path");
                    fixture.AssertRegistersAndXmm(); fixture.AssertBalancedStack();
                    Assert.AreEqual(flags & 0x8C5UL, fixture.Read64(128) & 0x8C5UL);
                }
                foreach (ushort type in new[] { (ushort)0, ushort.MaxValue })
                {
                    fixture.Prepare(9999, type, true, false); fixture.Run(true);
                    Assert.AreEqual(1, fixture.Read(156), "full UInt16 type demand and last live ID");
                    Assert.AreEqual(1, fixture.Read(152)); fixture.AssertRegistersAndXmm(); fixture.AssertBalancedStack();
                }
            }
        }

        private readonly struct Sample
        {
            internal Sample(int id, ushort type, bool interested, bool foreignManager)
            { Id=id; Type=type; Interested=interested; ForeignManager=foreignManager; }
            internal int Id { get; } internal ushort Type { get; }
            internal bool Interested { get; } internal bool ForeignManager { get; }
        }

        [TestMethod]
        public void PolicyRegistrationsAreInitiallyDormantOrderedAndOrVetoWithIndependentFaults()
        {
            IntPtr interest=Marshal.AllocHGlobal(65536);
            try
            {
                Marshal.Copy(new byte[65536],0,interest,65536);
                var log=new List<string>();int errors=0;
                var registry=new UnitRelocationPolicies(interest,_=>errors++);
                Assert.IsTrue(registry.Register("z","veto",eChimps.CHIMP_TYPE_LORD,c=>{log.Add("veto");return true;},10,out var veto,out _));
                Assert.IsTrue(registry.Register("a","fault",eChimps.CHIMP_TYPE_LORD,c=>{log.Add("fault");throw new Exception("isolated");},0,out var fault,out _));
                Assert.IsTrue(registry.Register("b","all",null,c=>{log.Add("all");return false;},10,out var all,out _));
                Assert.IsTrue(registry.Register("zz","after",eChimps.CHIMP_TYPE_LORD,c=>{log.Add("after");return false;},20,out var after,out _));
                Assert.IsFalse(registry.Register("z","veto",null,c=>false,0,out _,out _),"owner-local duplicate refused");
                var lord=Context(eChimps.CHIMP_TYPE_LORD);var sword=Context(eChimps.CHIMP_TYPE_SWORDSMAN);
                Assert.IsFalse(registry.Cancel(lord));Assert.AreEqual(0,log.Count);Assert.AreEqual(0,Marshal.ReadByte(interest,(int)eChimps.CHIMP_TYPE_LORD));
                veto.SetEnabled(true); fault.SetEnabled(true); all.SetEnabled(true); after.SetEnabled(true);
                Assert.AreEqual(1,Marshal.ReadByte(interest,0));Assert.AreEqual(1,Marshal.ReadByte(interest,ushort.MaxValue));
                Assert.IsTrue(registry.Cancel(lord));CollectionAssert.AreEqual(new[]{"fault","all","veto","after"},log.ToArray());
                Assert.AreEqual(1,errors);log.Clear();
                Assert.IsTrue(registry.Cancel(lord));Assert.AreEqual(1,errors,"same faulty registration reported once");
                log.Clear();Assert.IsFalse(registry.Cancel(sword));CollectionAssert.AreEqual(new[]{"all"},log.ToArray());
                after.SetEnabled(false);all.SetEnabled(false);
                Assert.AreEqual(0,Marshal.ReadByte(interest,0));Assert.AreEqual(0,Marshal.ReadByte(interest,ushort.MaxValue));
                Assert.AreEqual(0,Marshal.ReadByte(interest,(int)eChimps.CHIMP_TYPE_SWORDSMAN));
                Assert.AreEqual(1,Marshal.ReadByte(interest,(int)eChimps.CHIMP_TYPE_LORD),"disabling wildcard retains another owner's exact demand");
                veto.SetEnabled(false);Assert.AreEqual(1,Marshal.ReadByte(interest,(int)eChimps.CHIMP_TYPE_LORD),"second exact policy retains demand");
                fault.SetEnabled(false);Assert.AreEqual(0,Marshal.ReadByte(interest,(int)eChimps.CHIMP_TYPE_LORD));
                for(int i=0;i<20;i++)
                {
                    veto.SetEnabled(true);Assert.AreEqual(1,Marshal.ReadByte(interest,(int)eChimps.CHIMP_TYPE_LORD));
                    Assert.IsTrue(registry.Cancel(lord));veto.SetEnabled(false);Assert.IsFalse(registry.Cancel(lord));
                    Assert.AreEqual(0,Marshal.ReadByte(interest,(int)eChimps.CHIMP_TYPE_LORD));
                }
            }
            finally { Marshal.FreeHGlobal(interest); }
        }
        private static UnitRelocationContext Context(eChimps type)=>new UnitRelocationContext(1,123,type,1,37,41,70,UnitRelocationOrigin.Native);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RunCode();
        private sealed class NativeFixture : IDisposable
        {
            private readonly IntPtr code,manager,interest,result;
            private readonly X64InlineHook hook;
            private readonly RunCode run;
            private int id; private bool foreign;
            internal ulong ExpectedCaller {get; private set;}
            internal ulong Manager=>Address(manager)+(foreign?4UL:0UL);
            private ulong Code=>Address(code); private ulong Result=>Address(result);
            private const ulong Pattern=0x123456789ABCDEF0UL;
            internal NativeFixture()
            {
                try
                {
                code=VirtualAlloc(IntPtr.Zero,(UIntPtr)65536,0x3000,0x40);
                manager=VirtualAlloc(IntPtr.Zero,(UIntPtr)(0x490*10001+0xB00),0x3000,0x04);
                interest=Marshal.AllocHGlobal(65536);result=Marshal.AllocHGlobal(2048);
                Assert.AreNotEqual(IntPtr.Zero,code);Assert.AreNotEqual(IntPtr.Zero,manager);
                Marshal.Copy(new byte[65536],0,interest,65536);Marshal.Copy(new byte[2048],0,result,2048);
                for(int i=0;i<12;i++)Marshal.WriteInt64(result,1024+i*8,unchecked((long)(Pattern+(ulong)i)));
                WriteOriginal(Code+0x1000);WriteOriginal(Code+0x2000);WriteCallback();
                hook=new X64InlineHook(Code+0x2000,14,name:"Private UnitRelocation ABI fixture");
                Assert.AreEqual(16,hook.DisplacedByteCount);Assert.IsFalse(hook.IsInstalled);
                hook.Generate((a,original,resume)=>UnitRelocationNative.Emit(a,original,resume,Address(manager),Address(interest),Code+0x3000));
                hook.Enable();Assert.IsTrue(hook.IsInstalled);
                run=Marshal.GetDelegateForFunctionPointer<RunCode>(code);
                }
                catch { Dispose();throw; }
            }
            internal void Prepare(int unitId,ushort type,bool interested,bool veto,bool foreignManager=false)
            {
                id=unitId;foreign=foreignManager;ClearResults();Marshal.Copy(new byte[65536],0,interest,65536);
                if(unitId>=0&&unitId<=10000)Marshal.WriteInt16(manager,unitId*0x490+0x6E6,unchecked((short)type));
                Marshal.WriteByte(interest,type,interested?(byte)1:(byte)0);Marshal.WriteInt32(result,240,veto?1:0);
            }
            internal void SetInterest(ushort type,bool enabled)=>Marshal.WriteByte(interest,type,enabled?(byte)1:(byte)0);
            internal void ClearResults(){int veto=Marshal.ReadInt32(result,240);Marshal.Copy(new byte[512],0,result,512);Marshal.WriteInt32(result,240,veto);}
            internal int Read(int offset)=>Marshal.ReadInt32(result,offset);
            internal ulong Read64(int offset)=>unchecked((ulong)Marshal.ReadInt64(result,offset));
            internal byte[] Snapshot(){var bytes=new byte[512];Marshal.Copy(result,bytes,0,bytes.Length);return bytes;}
            internal void Run(bool hooked)
            {
                BuildWrapper(hooked?Code+0x2000:Code+0x1000);
                Assert.IsTrue(FlushInstructionCache(GetCurrentProcess(),code,(UIntPtr)65536));run();
            }
            internal void AssertBalancedStack()=>Assert.AreEqual(Read64(136),Read64(144),"balanced wrapper stack");
            internal void AssertRegistersAndXmm()
            {
                ulong[] expected={0x789UL,Manager,unchecked((ulong)(long)id),37,41,0x567,0x678,0x111,0x222,0x333,0x777,0x444,0x555,0x666,0x888};
                for(int i=0;i<expected.Length;i++)Assert.AreEqual(expected[i],Read64(i*8),"GPR "+i);
                for(int i=0;i<12;i++)Assert.AreEqual(Pattern+(ulong)i,Read64(256+i*8),"XMM lane "+i);
            }
            private void BuildWrapper(ulong target)
            {
                var a=new Assembler(64);a.push(rbx);a.push(rbp);a.push(rsi);a.push(rdi);a.push(r12);a.push(r13);a.push(r14);a.push(r15);a.sub(rsp,0x78);
                a.mov(rax,Result);a.mov(__qword_ptr[rax+136],rsp);
                a.mov(rbx,0x111UL);a.mov(rbp,0x222UL);a.mov(rsi,0x333UL);a.mov(rdi,0x777UL);a.mov(r12,0x444UL);a.mov(r13,0x555UL);a.mov(r14,0x666UL);a.mov(r15,0x888UL);
                a.movdqu(xmm0,__xmmword_ptr[rax+1024]);a.movdqu(xmm1,__xmmword_ptr[rax+1040]);a.movdqu(xmm2,__xmmword_ptr[rax+1056]);
                a.movdqu(xmm3,__xmmword_ptr[rax+1072]);a.movdqu(xmm4,__xmmword_ptr[rax+1088]);a.movdqu(xmm5,__xmmword_ptr[rax+1104]);
                a.mov(rcx,Manager);a.mov(rdx,unchecked((ulong)(long)id));a.mov(r8d,37);a.mov(r9d,41);a.mov(r10,0x567UL);a.mov(r11,0x678UL);a.mov(__dword_ptr[rsp+32],0xFEDC);
                a.xor(eax,eax);a.stc();a.mov(rax,0x789UL);a.call(target);
                // MOV/LEA/PUSH preserve flags until their snapshot below.
                a.push(rax);a.mov(rax,Result);a.mov(__qword_ptr[rax+8],rcx);a.mov(__qword_ptr[rax+16],rdx);a.mov(__qword_ptr[rax+24],r8);a.mov(__qword_ptr[rax+32],r9);
                a.mov(__qword_ptr[rax+40],r10);a.mov(__qword_ptr[rax+48],r11);a.mov(__qword_ptr[rax+56],rbx);a.mov(__qword_ptr[rax+64],rbp);a.mov(__qword_ptr[rax+72],rsi);a.mov(__qword_ptr[rax+80],rdi);
                a.mov(__qword_ptr[rax+88],r12);a.mov(__qword_ptr[rax+96],r13);a.mov(__qword_ptr[rax+104],r14);a.mov(__qword_ptr[rax+112],r15);
                a.lea(r10,__[rsp+8]);a.mov(__qword_ptr[rax+144],r10);a.pushfq();a.pop(r10);a.mov(__qword_ptr[rax+128],r10);
                a.movdqu(__xmmword_ptr[rax+256],xmm0);a.movdqu(__xmmword_ptr[rax+272],xmm1);a.movdqu(__xmmword_ptr[rax+288],xmm2);
                a.movdqu(__xmmword_ptr[rax+304],xmm3);a.movdqu(__xmmword_ptr[rax+320],xmm4);a.movdqu(__xmmword_ptr[rax+336],xmm5);
                a.pop(r10);a.mov(__qword_ptr[rax],r10);a.add(rsp,0x78);a.pop(r15);a.pop(r14);a.pop(r13);a.pop(r12);a.pop(rdi);a.pop(rsi);a.pop(rbp);a.pop(rbx);a.ret();
                byte[] encoded=Write(Code,a);
                ExpectedCaller=0;var decoder=Decoder.Create(64,new ByteArrayCodeReader(encoded));decoder.IP=Code;
                while(decoder.IP<Code+(uint)encoded.Length) {var instruction=decoder.Decode();if(instruction.FlowControl==FlowControl.Call&&instruction.NearBranchTarget==target){ExpectedCaller=instruction.NextIP;break;}}
                Assert.AreNotEqual(0UL,ExpectedCaller,"fixture original CALL continuation captured");
            }
            private void WriteOriginal(ulong address)
            {
                var a=new Assembler(64);a.db(UnitRelocationNative.Entry);
                a.pushfq();a.push(rax);a.push(r11);a.mov(r11,Result);a.inc(__dword_ptr[r11+152]);
                a.movzx(eax,__word_ptr[rsp+120]);a.mov(__dword_ptr[r11+160],eax);
                a.mov(rax,__qword_ptr[rsp+80]);a.mov(__qword_ptr[r11+216],rax);
                a.pop(r11);a.pop(rax);a.popfq();a.add(rsp,32);a.pop(r14);a.pop(rdi);a.pop(rsi);a.mov(rbp,__qword_ptr[rsp+24]);a.ret();Write(address,a);
            }
            private void WriteCallback()
            {
                var a=new Assembler(64);a.mov(rax,Result);a.inc(__dword_ptr[rax+156]);
                a.mov(__qword_ptr[rax+168],rcx);a.mov(__qword_ptr[rax+176],rdx);a.mov(__qword_ptr[rax+184],r8);a.mov(__qword_ptr[rax+192],r9);
                a.movzx(r10d,__word_ptr[rsp+40]);a.mov(__qword_ptr[rax+200],r10);a.mov(r10,__qword_ptr[rsp+48]);a.mov(__qword_ptr[rax+208],r10);a.mov(__qword_ptr[rax+248],rsp);
                a.mov(rcx,0xDEADUL);a.mov(rdx,0xDEADUL);a.mov(r8,0xDEADUL);a.mov(r9,0xDEADUL);a.mov(r10,0xDEADUL);a.mov(r11,0xDEADUL);
                a.pxor(xmm0,xmm0);a.pxor(xmm1,xmm1);a.pxor(xmm2,xmm2);a.pxor(xmm3,xmm3);a.pxor(xmm4,xmm4);a.pxor(xmm5,xmm5);
                a.mov(eax,__dword_ptr[rax+240]);a.add(r10,1);a.ret();Write(Code+0x3000,a);
            }
            private static byte[] Write(ulong address,Assembler a){byte[] bytes=NativeInlineStub.Assemble(a,address);Marshal.Copy(bytes,0,new IntPtr(unchecked((long)address)),bytes.Length);return bytes;}
            private static ulong Address(IntPtr p)=>unchecked((ulong)p.ToInt64());
            public void Dispose()
            {
                // Only private pages: all synchronous fixture calls have returned before unpublishing/freeing.
                hook?.Dispose();if(code!=IntPtr.Zero)VirtualFree(code,UIntPtr.Zero,0x8000);if(manager!=IntPtr.Zero)VirtualFree(manager,UIntPtr.Zero,0x8000);
                if(interest!=IntPtr.Zero)Marshal.FreeHGlobal(interest);if(result!=IntPtr.Zero)Marshal.FreeHGlobal(result);
            }
        }
        [DllImport("kernel32",SetLastError=true)]private static extern IntPtr VirtualAlloc(IntPtr at,UIntPtr size,uint allocation,uint protection);
        [DllImport("kernel32",SetLastError=true)]private static extern bool VirtualFree(IntPtr at,UIntPtr size,uint free);
        [DllImport("kernel32")]private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32",SetLastError=true)]private static extern bool FlushInstructionCache(IntPtr process,IntPtr at,UIntPtr size);
    }
}
