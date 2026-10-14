using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod] public void SavegameSettingsRoundTrip() => SavegameModSettingsTests.Run(Assert);
        [TestMethod] public void SavegameSettingsReaderAcceptsTheCapturedPerModLimit() => SavegameModSettingsTests.RunPerModLimit(Assert);
        [TestMethod] public void SaveReadinessPacketRoundTrip()
        {
            var packet = new APIShared.SavegameLoadPacket { Operation = 0, Lobby = 123,
                Token = "01234567890123456789012345678901", SaveName = "coop-save", Fingerprint = new string('A', 64),
                Roster = "101,102", Checksum = 17, Time = 29, Metadata = new byte[] { 1, 2 } };
            var copy = MessagePack.MessagePackSerializer.Deserialize<APIShared.SavegameLoadPacket>(
                MessagePack.MessagePackSerializer.Serialize(packet));
            Assert(copy.Lobby == packet.Lobby && copy.Token == packet.Token && copy.SaveName == packet.SaveName &&
                copy.Fingerprint == packet.Fingerprint && copy.Roster == packet.Roster &&
                copy.Checksum == packet.Checksum && copy.Time == packet.Time && copy.Metadata[1] == 2,
                "Save readiness packet lost identity or provenance.");
        }
        [TestMethod] public void CompiledSavePreparationPrecedesTheRootVanillaLoad()
        {
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(APIShared.SavegameModSettings).Assembly.Location))
            {
                var lifecycle = assembly.MainModule.GetType("APIShared.MissionLifecycleService");
                var run = System.Linq.Enumerable.Single(lifecycle.Methods, m => m.Name == "Run" && m.HasGenericParameters);
                var code = run.Body.Instructions;
                int prepare = -1, rootInvoke = -1;
                for (int i = 0; i < code.Count; i++)
                {
                    var call = code[i].Operand as Mono.Cecil.MethodReference;
                    if (call?.Name == "ApplyBeforeLoad") prepare = i;
                    if (prepare >= 0 && call?.Name == "Invoke" && call.DeclaringType.FullName.StartsWith("System.Func"))
                    { rootInvoke = i; break; }
                }
                Assert(prepare >= 0 && rootInvoke > prepare, "Root Vanilla load can precede prepared settings.");
                var settings = assembly.MainModule.GetType("APIShared.SavegameModSettings");
                var begin = System.Linq.Enumerable.Single(settings.Methods, m => m.Name == "BeginLoad");
                bool validates = false, readsDirectly = false, trustsArchiveFlag = false;
                foreach (var instruction in begin.Body.Instructions)
                {
                    var call = instruction.Operand as Mono.Cecil.MethodReference;
                    validates |= call?.Name == "ValidateStart";
                    readsDirectly |= call?.Name == "ReadRecord";
                    trustsArchiveFlag |= call?.Name == "get_IsSaveFile";
                }
                Assert(validates && readsDirectly && !trustsArchiveFlag,
                    "Save entry trusts the misclassified archive callback or bypasses multiplayer admission.");
            }
        }
        [TestMethod]
        [DoNotParallelize]
        public void DialogCleanupCannotRevokeCommittedSaveAdmission()
        {
            var type = typeof(APIShared.SavegameLoadCoordinator);
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var pending = type.GetField("pending", flags);
            var released = type.GetField("released", flags);
            var loading = type.GetField("loading", flags);
            var owner = type.GetField("owner", flags);
            object oldPending = pending.GetValue(null), oldReleased = released.GetValue(null), oldLoading = loading.GetValue(null);
            object oldOwner = owner.GetValue(null);
            try
            {
                var packet = new APIShared.SavegameLoadPacket { Token = "committed", SaveName = "save" };
                pending.SetValue(null, packet); released.SetValue(null, true); loading.SetValue(null, false);
                Assert(!APIShared.SavegameLoadCoordinator.CancelSelection() && ReferenceEquals(pending.GetValue(null), packet),
                    "Dialog cleanup revoked the committed host request.");
                released.SetValue(null, false); loading.SetValue(null, true);
                Assert(!APIShared.SavegameLoadCoordinator.CancelSelection() && ReferenceEquals(pending.GetValue(null), packet),
                    "Dialog cleanup revoked an executing save request.");
                owner.SetValue(null, null); loading.SetValue(null, false);
                Assert(APIShared.SavegameLoadCoordinator.CancelSelection() && pending.GetValue(null) == null,
                    "Uncommitted cancellation kept the old transaction available for a later acknowledgement.");
            }
            finally { pending.SetValue(null, oldPending); released.SetValue(null, oldReleased); loading.SetValue(null, oldLoading); owner.SetValue(null, oldOwner); }
        }
        [TestMethod] public void MarkedSelectionHarmonyContract() => MarkedSelectionHarmonyTests.Run(Assert);
    }
}
