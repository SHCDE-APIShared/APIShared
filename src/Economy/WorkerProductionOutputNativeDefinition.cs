using System;
using System.Collections.Generic;

namespace APIShared.Economy
{
    internal enum WorkerProductionOutputSiteKind { Zero, MillerPrepare, MillerPickup, MillerDeposit, MillerFinish }
    internal sealed class WorkerProductionOutputSite
    {
        internal WorkerProductionOutputSite(WorkerProductionOutputSiteKind kind, WorkerProductionBonusNativeSite site)
        { Kind=kind; Site=site; }
        internal WorkerProductionOutputSiteKind Kind { get; }
        internal WorkerProductionBonusNativeSite Site { get; }
    }

    // Closed FBCB9319 output-role audit: wheat133DD0, miller1377C0, baker138850.
    // Every span is complete instructions and has no incoming edge to its interior.
    internal static class WorkerProductionOutputNativeDefinition
    {
        internal const string SiteGroup="Economy.WorkerProductionOutputs";
        internal static readonly IReadOnlyList<WorkerProductionOutputSite> Sites=Array.AsReadOnly(new[] {
            S("Wheat",WorkerProductionOutputSiteKind.Zero,0x133DD0,0x1349B7,16,0x134B52,
                "488D2DF2813906460FBF842B8E090000","488BCD","4863C2"),
            S("BakerStart",WorkerProductionOutputSiteKind.Zero,0x138850,0x1391F5,17,0x13953E,
                "458BC7460FBF8C33EE060000BA0A000000","488D1DA3393906","4963C0"),
            S("BakerArrival",WorkerProductionOutputSiteKind.Zero,0x138850,0x139337,18,0x13953E,
                "8BD7460FBF84338E090000488D1D67383906","488BCB","4963C0"),
            S("MillerPrepare",WorkerProductionOutputSiteKind.MillerPrepare,0x1377C0,0x1380A1,17,0x1383AF,
                "458BC5460FBF8C3BEE060000BA10000000","498BCC","4863C7"),
            S("MillerPickup",WorkerProductionOutputSiteKind.MillerPickup,0x1377C0,0x138162,17,0,
                "664689AC39E00900004288B439A60A0000","664289B43920090000",""),
            S("MillerDeposit",WorkerProductionOutputSiteKind.MillerDeposit,0x1377C0,0x138290,17,0,
                "664289B439E00900004238B439A60A0000","0F8508010000",""),
            S("MillerFinish",WorkerProductionOutputSiteKind.MillerFinish,0x1377C0,0x1383AF,19,0,
                "4863C74869C890040000664239B439E0090000","0F8F5B040000","") });
        internal static readonly IReadOnlyList<WorkerProductionBonusNativeFunction> Functions=Array.AsReadOnly(new[] {
            new WorkerProductionBonusNativeFunction(0x133DD0,4394,"36175069A5A1B2C06F81C9139B30AAD1A52616D6680B716EB0C5B1BEBF8B3C4F"),
            new WorkerProductionBonusNativeFunction(0x1377C0,4226,"BCD2E5EE940AF3F17A93BA1EBC5AA0D263056B98FC9909B8D7506D6700FDC424"),
            new WorkerProductionBonusNativeFunction(0x138850,4351,"9147865051044C84A0DC70D8BFAB8D896CC6FA97AAD8433FF4271309EF2BC789") });
        private static WorkerProductionOutputSite S(string name,WorkerProductionOutputSiteKind kind,int function,int rva,int length,int footer,
            string bytes,string next,string footerBytes) => new WorkerProductionOutputSite(kind,new WorkerProductionBonusNativeSite(name,
                WorkerProductionBonusNativeKind.ZeroCompletion,function,rva,length,footer,bytes,bytes,next,footerBytes));
    }
}
