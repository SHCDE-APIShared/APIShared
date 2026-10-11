using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public class SavegameParticipationTests
{
    [TestMethod]
    public void ExplicitExclusionAppliesToPluginAndDerivedClasses()
    {
        Assert.IsTrue(SavegameSettingsParticipation.IsExcluded(typeof(IndependentPersistencePlugin)));
        Assert.IsTrue(SavegameSettingsParticipation.IsExcluded(typeof(DerivedPlugin)));
    }

    [TestMethod]
    public void OrdinaryPluginsParticipateWithoutDependingOnModSpecificMarkers()
    {
        Assert.IsFalse(SavegameSettingsParticipation.IsExcluded(typeof(OrdinaryPlugin)));
        Assert.IsFalse(SavegameSettingsParticipation.IsExcluded(typeof(OtherProtocolPlugin)));
        Assert.IsFalse(SavegameSettingsParticipation.IsExcluded(null));
    }

    [ExcludeFromSavegameModSettings]
    private class IndependentPersistencePlugin { }
    private sealed class DerivedPlugin : IndependentPersistencePlugin { }
    private sealed class OrdinaryPlugin { }
    private class OptionalApiPlugin { }
    private sealed class OptionalApiDerivedPlugin : OptionalApiPlugin { }

    [TestMethod]
    public void ExplicitRegistrationRetainsExclusionWithoutAnApiAttribute()
    {
        Assert.IsFalse(SavegameSettingsParticipation.IsExcluded(typeof(OptionalApiPlugin)));
        SavegameSettingsParticipation.Exclude(typeof(OptionalApiPlugin));
        SavegameSettingsParticipation.Exclude(typeof(OptionalApiPlugin));
        Assert.IsTrue(SavegameSettingsParticipation.IsExcluded(typeof(OptionalApiPlugin)));
        Assert.IsTrue(SavegameSettingsParticipation.IsExcluded(typeof(OptionalApiDerivedPlugin)));
        Assert.ThrowsExactly<ArgumentNullException>(() => SavegameSettingsParticipation.Exclude(null));
    }
    private sealed class OtherProtocolPlugin
    {
        public const bool ExtendedDataModSettingsOptOut = true;
    }
}
