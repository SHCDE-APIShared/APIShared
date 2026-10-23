using APIShared.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public sealed class FixesWheatContractTests
{
    // The machine contract is derived from the actual code; see APISharedTests.FixesWheatAnalysisTests.
    [TestMethod]
    public void LateMissingOrReorderedStartupFramesAreRejected()
    {
        Assert.IsTrue(FixesWheatContract.IsStartup(true, false, true, 2, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(false, false, true, 2, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, true, true, 2, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, false, 2, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, -1, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, -1, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, 5, -1, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, 6, 5, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, 5, 6, false, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, 5, 6, true, false));
    }
}
