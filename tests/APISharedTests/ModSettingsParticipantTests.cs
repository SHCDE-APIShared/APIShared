using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public class ModSettingsParticipantTests
    {
        [AttributeUsage(AttributeTargets.Property)]
        private sealed class SyncHostOnlyAttribute : Attribute { }
        [AttributeUsage(AttributeTargets.Property)]
        private sealed class PresetLocalAttribute : Attribute { }
        [AttributeUsage(AttributeTargets.Property)]
        private sealed class ExplodingAttribute : Attribute
        {
            public ExplodingAttribute() { throw new InvalidOperationException("metadata failure"); }
        }
        private class Endpoint : IModSettingsPresetEndpoint
        {
            internal int Calls;
            internal Func<Dictionary<string, byte[]>> Factory;
            public bool IsMissionPresetActive => false;
            public Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot() { Calls++; return Factory(); }
            public void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable) => throw new Exception("Must not apply");
            public void System_ExitMissionPreset() => throw new Exception("Must not exit");
        }
        private sealed class Host : Endpoint
        {
            [SyncHostOnly] public bool EnableMod { get; set; }
            [SyncHostOnly] public int Value { get => throw new Exception("Live values must not be probed"); set { } }
            [PresetLocal] public string Personal { get; set; }
            internal Host() { Factory = () => new Dictionary<string, byte[]> {
                ["EnableMod"] = MessagePackSerializer.Serialize(false), ["Value"] = MessagePackSerializer.Serialize(7)
            }; }
        }
        private sealed class InvalidEnable : Endpoint { [SyncHostOnly] public int EnableMod { get; set; } }
        private sealed class BrokenMetadata : Endpoint { [SyncHostOnly, Exploding] public int Value { get; set; } }
        [ExcludeFromSavegameModSettings] private sealed class OptOut { }

        private static ModSettingsParticipantDiscovery.Registration Registration(string id, object model) =>
            new ModSettingsParticipantDiscovery.Registration { Guid = id, Name = "Name " + id, Model = model };
        private static List<ModSettingsParticipantInfo> Inspect(params ModSettingsParticipantDiscovery.Registration[] lobby) =>
            ModSettingsParticipantDiscovery.Inspect(lobby, Array.Empty<ModSettingsParticipantDiscovery.Registration>());

        [TestMethod]
        public void AcceptedParticipantKeepsTheExistingSnapshotOnlyContract()
        {
            var host = new Host();
            var info = Inspect(Registration("third.party", host)).Single();
            Assert.IsTrue(info.IsCompatible); Assert.IsNull(info.Diagnostic);
            Assert.AreEqual("third.party", info.ModGuid); Assert.AreEqual("Name third.party", info.DisplayName);
            Assert.AreSame(host, info.Endpoint); Assert.AreEqual(1, host.Calls);
            CollectionAssert.AreEqual(new[] { "EnableMod", "Value" }, info.HostProperties.Select(item => item.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "Personal" }, info.PersonalProperties.Select(item => item.Name).ToArray());
            Assert.ThrowsExactly<NotSupportedException>(() => ((IList<PropertyInfo>)info.HostProperties).Clear());
        }

        [TestMethod]
        public void DuplicateExcludedAndUnidentifiedRegistrationsNeverCreateSnapshots()
        {
            var host = new Host();
            var excluded = Registration("excluded", host); excluded.PluginType = typeof(OptOut);
            var items = Inspect(Registration("duplicate", host), Registration("duplicate", host), excluded, Registration(null, host));
            CollectionAssert.AreEqual(new[] { ModSettingsParticipantStatus.DuplicateRegistration, ModSettingsParticipantStatus.Excluded,
                ModSettingsParticipantStatus.MissingPluginGuid }, items.Select(item => item.Status).ToArray());
            Assert.AreEqual(0, host.Calls);
            Assert.IsTrue(items.All(item => item.Endpoint == null && item.HostProperties.Count == 0));
        }

        [TestMethod]
        public void SchemaRejectionsHaveSpecificStatusesWithoutApplyingSettings()
        {
            var invalid = new InvalidEnable();
            var items = Inspect(Registration("untyped", new object()), Registration("empty", new Endpoint()), Registration("invalid", invalid));
            CollectionAssert.AreEqual(new[] { ModSettingsParticipantStatus.MissingEndpoint, ModSettingsParticipantStatus.NoHostSettings,
                ModSettingsParticipantStatus.InvalidHostSettings }, items.Select(item => item.Status).ToArray());
            Assert.AreEqual(0, invalid.Calls);
            Assert.AreEqual(0, ModSettingsParticipantDiscovery.Accepted(items).Count());
        }

        [TestMethod]
        public void DisabledSnapshotsRejectMissingEnabledAndMalformedValues()
        {
            foreach (var factory in new Func<Dictionary<string, byte[]>>[] {
                () => null, () => new Dictionary<string, byte[]>(),
                () => new Dictionary<string, byte[]> { ["EnableMod"] = MessagePackSerializer.Serialize(true) }
            })
            {
                var host = new Host { Factory = factory };
                Assert.AreEqual(ModSettingsParticipantStatus.InvalidMissionSnapshot, Inspect(Registration("mod", host)).Single().Status);
            }
            var broken = new Host { Factory = () => new Dictionary<string, byte[]> { ["EnableMod"] = new byte[] { 0xc1 } } };
            Assert.AreEqual(ModSettingsParticipantStatus.InspectionFailed, Inspect(Registration("mod", broken)).Single().Status);
        }

        [TestMethod]
        public void MetadataAndFactoryErrorsCannotHideOtherParticipants()
        {
            var broken = new Host { Factory = () => throw new InvalidOperationException("snapshot failure") };
            var items = Inspect(Registration("bad-metadata", new BrokenMetadata()), Registration("bad-snapshot", broken), Registration("healthy", new Host()));
            Assert.AreEqual(ModSettingsParticipantStatus.InspectionFailed, items[0].Status);
            Assert.AreEqual(ModSettingsParticipantStatus.InspectionFailed, items[1].Status);
            Assert.IsTrue(items[2].IsCompatible);
            Assert.AreEqual("healthy", ModSettingsParticipantDiscovery.Accepted(items).Single().ModGuid);
        }

        [TestMethod]
        public void DynamicConfigurationEndpointsRemainIndependentOfOptionalPanels()
        {
            var host = new Host();
            var properties = new[] { typeof(Host).GetProperty("Value") };
            var configuration = Registration("external", host);
            configuration.IsDynamic = () => true; configuration.DynamicProperties = () => properties;
            var info = ModSettingsParticipantDiscovery.Inspect(Array.Empty<ModSettingsParticipantDiscovery.Registration>(), new[] { configuration }).Single();
            Assert.IsTrue(info.IsCompatible && info.IsConfigurationEndpoint); Assert.AreSame(host, info.Endpoint);
            properties[0] = null;
            Assert.IsNotNull(info.HostProperties[0]); Assert.AreEqual(0, host.Calls);
        }

        [TestMethod]
        public void ConflictingAcceptedProvidersAreReportedWithoutPickingOne()
        {
            var configuration = Registration("same", new Host());
            configuration.IsDynamic = () => true; configuration.DynamicProperties = () => throw new Exception("Conflict must be checked first");
            var info = ModSettingsParticipantDiscovery.Inspect(new[] { Registration("same", new Host()) }, new[] { configuration }).Single();
            Assert.AreEqual(ModSettingsParticipantStatus.ConflictingProvider, info.Status);
            Assert.IsNull(info.Endpoint); Assert.IsFalse(info.IsCompatible);
            StringAssert.Contains(info.Diagnostic, "same");
            Assert.ThrowsExactly<InvalidOperationException>(() => ModSettingsParticipantDiscovery.Accepted(new[] { info }).ToArray());
        }

        [TestMethod]
        public void BrokenDynamicDescriptorsRejectOnlyTheirProvider()
        {
            var dynamic = Registration("broken", new Host()); dynamic.IsDynamic = () => true;
            dynamic.DynamicProperties = () => throw new InvalidOperationException("descriptor failure");
            var items = ModSettingsParticipantDiscovery.Inspect(new[] { Registration("healthy", new Host()) }, new[] { dynamic });
            Assert.IsTrue(items[0].IsCompatible); Assert.AreEqual(ModSettingsParticipantStatus.InspectionFailed, items[1].Status);
        }
    }
}
