using System;
using System.Collections.Generic;

namespace APIShared.Economy
{
    // Closed receipt-source audit for FBCB9319. Every span ends in the final storage CALL;
    // production buffers and ingredient transfers are deliberately absent. QuarryGrunt
    // and QuarryOx are separate final delivery sources for the same economic role.
    internal static class ProductionGoodsReceivedNativeDefinition
    {
        internal const string SiteGroup = "Economy.ProductionGoodsReceived";
        internal sealed class Site
        {
            internal Site(string name, int rva, string bytes, string next)
            { Name=name; Rva=rva; Bytes=Internal.NativeInlineStub.Hex(bytes); Next=Internal.NativeInlineStub.Hex(next); }
            internal string Name { get; }
            internal int Rva { get; }
            internal byte[] Bytes { get; }
            internal byte[] Next { get; }
        }
        internal static readonly IReadOnlyList<Site> Sites = Array.AsReadOnly(new[] {
            new Site("Wood", 0x12CA01, "468B8421F8060000488BCDE8EF96F8FF", "486305AC388000"),
            new Site("Hunter", 0x130AF4, "468B8429F8060000488BCFE8FC55F8FF", "486305B9F77F00"),
            new Site("Pitch", 0x1338D5, "468B8421F8060000488BCDE81B28F8FF", "48631DD8C97F00"),
            new Site("Hops", 0x135998, "468B8421F8060000488BCBE85807F8FF", "48631515A97F00"),
            new Site("Apple", 0x1368E4, "430FBF94208E090000478B8420F8060000E806F8F7FF", "486305C3997F00"),
            new Site("Cattle", 0x137396, "430FBF94288E090000478B8428F8060000E854EDF7FF", "486305118F7F00"),
            new Site("Iron", 0x14E011, "468B8439F8060000488BCDE8DF80F6FF", "48631D9C227E00"),
            new Site("Wheat", 0x134A35, "468B8429F8060000488BCDE8BB16F8FF", "48631578B87F00"),
            new Site("Miller", 0x138272, "468B8439F8060000498BCCE87EDEF7FF", "48633D3B807F00"),
            new Site("Baker", 0x1393BC, "468B8431F8060000488BCBE834CDF7FF", "486305F16E7F00"),
            new Site("Ox", 0x132E3A, "430FBF94308E090000478B8430F8060000E8B032F8FF", "4863056DD47F00"),
            new Site("QuarryGrunt", 0x1324F1, "430FBF94388E090000478B8438F8060000E8F93BF8FF", "486305B6DD7F00"),
            new Site("Brewer", 0x13A514, "468B8439F8060000498BCDE8DCBBF7FF", "48631D995D7F00"),
            new Site("Tanner", 0x13EF38, "468B8421F8060000488BCEE8B871F7FF", "48630575137F00"),
        });
    }
}
