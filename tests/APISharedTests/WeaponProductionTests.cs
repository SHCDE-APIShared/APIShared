using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using APIShared.Economy;
using APIShared.Goods;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class WeaponProductionTests
    {
        [TestMethod]
        public void VanillaOptionsKeepTheirSlotsAndPriorityBreaksTiesByIdentity()
        {
            var high = Recipe("z-owner", "high", priority: 20);
            var tiedWinner = Recipe("a-owner", "a-choice", priority: 10);
            var tiedIdLoser = Recipe("a-owner", "z-choice", priority: 10);
            var tiedOwnerLoser = Recipe("z-owner", "a-choice", priority: 10);
            var first = new[] { tiedOwnerLoser, tiedIdLoser, tiedWinner, high };
            var catalogue = WeaponProductionCatalog.Compile(first, _ => true);
            var reordered = WeaponProductionCatalog.Compile(Enumerable.Reverse(first), _ => true);
            var choices = catalogue.For(eStructs.STRUCT_FLETCHERS_WORKSHOP);

            Assert.AreEqual(4, choices.Count);
            CollectionAssert.AreEquivalent(new[] { eGoods.STORED_BOWS, eGoods.STORED_CROSSBOWS },
                choices.Take(2).Select(option => option.OutputGood).ToArray());
            Assert.IsTrue(choices.Take(2).All(option => option.IsVanilla));
            Assert.AreEqual(high.Option.Id, choices[2].Id);
            Assert.AreEqual(tiedWinner.Option.Id, choices[3].Id);
            Assert.AreEqual(WeaponProductionRegistrationStatus.Admitted, tiedWinner.Status);
            Assert.AreEqual(WeaponProductionRegistrationStatus.ExcludedByPriority, tiedIdLoser.Status);
            Assert.AreEqual(WeaponProductionRegistrationStatus.ExcludedByPriority, tiedOwnerLoser.Status);
            Assert.AreEqual(catalogue.Digest, reordered.Digest);
            CollectionAssert.AreEqual(choices.Select(option => option.Id).ToArray(),
                reordered.For(eStructs.STRUCT_FLETCHERS_WORKSHOP).Select(option => option.Id).ToArray());
        }

        [TestMethod]
        public void TanneryReservesOneSlotAndUnavailableAdapterContributesNoOptions()
        {
            var type = eStructs.STRUCT_TANNERS_WORKSHOP;
            var additions = new[] { Tanner("a"), Tanner("b"), Tanner("c"), Tanner("d") };
            var ready = WeaponProductionCatalog.Compile(additions, _ => true);
            var choices = ready.For(type);
            Assert.AreEqual(4, choices.Count);
            Assert.IsTrue(choices[0].IsVanilla);
            Assert.AreEqual(eGoods.STORED_LEATHER_ARMOUR, choices[0].OutputGood);
            Assert.AreEqual(WeaponProductionRegistrationStatus.ExcludedByPriority, additions[3].Status);

            var unavailable = WeaponProductionCatalog.Compile(additions, _ => false);
            Assert.AreEqual(0, unavailable.For(type).Count);
            Assert.AreEqual(WeaponProductionRegistrationStatus.AdapterUnavailable, additions[0].Status);
        }

        [TestMethod]
        public void GameplayFieldsChangeAgreementWhileLocalizedLabelsDoNot()
        {
            string Digest(WeaponProductionRegistration registration) =>
                WeaponProductionCatalog.Compile(new[] { registration }, _ => true).Digest;
            string baseline = Digest(Recipe("owner", "recipe"));
            Assert.AreEqual(baseline, Digest(Recipe("owner", "recipe", display: "Localized label")));
            Assert.AreNotEqual(baseline, Digest(Recipe("owner", "recipe", amount: 3)));
            Assert.AreNotEqual(baseline, Digest(Recipe("owner", "recipe", duration: 45)));
            Assert.AreNotEqual(baseline, Digest(Recipe("owner", "recipe", ingredientAmount: 2)));
            Assert.AreNotEqual(Digest(Recipe("owner", "recipe", ingredientAmount: 2)),
                Digest(Recipe("owner", "recipe", ingredientAmount: 3)));
            Assert.AreNotEqual(baseline, Digest(Recipe("owner", "recipe", template: eGoods.STORED_CROSSBOWS)));
            Assert.AreNotEqual(baseline, Digest(Recipe("owner", "recipe", output: eGoods.STORED_MACES)));
            Assert.AreNotEqual(baseline, Digest(Recipe("owner", "other-id")));
        }

        [TestMethod]
        public void LegacyNativeOnlyCatalogDigestsRemainByteIdentical()
        {
            // Published /1 and /2 encodings bind native save/catalog agreement. These pins
            // deliberately protect their exact bytes while /3 adds stored-output semantics.
            Assert.AreEqual("9A28598D124EA021FB0EEB43D866F41B35AD5FACF1FFB7BEEEE58EEF4780EAC8",
                WeaponProductionCatalog.Compile(new[] { Recipe("owner", "recipe") }, _ => true).Digest);
            Assert.AreEqual("B4BFB58591A57FCAF000EA758A841F9943BBC33486A0D2613480F328BAB97085",
                WeaponProductionCatalog.Compile(new[] { Recipe("owner", "recipe", ingredientAmount: 2) }, _ => true).Digest);
        }

        [TestMethod]
        public void StoredOutputAndIngredientCopiesComposeWithoutChangingTheNativeDefinition()
        {
            var output = Stored("owner", "ore");
            var original = new WeaponProductionOptionDefinition(eStructs.STRUCT_FLETCHERS_WORKSHOP,
                eGoods.STORED_BOWS, eGoods.STORED_SWORDS, "Recipe", 3, 45, 7);
            var before = original.WithStoredOutput(output).WithIngredientAmount(4);
            var after = original.WithIngredientAmount(4).WithStoredOutput(output);
            Assert.IsNull(original.StoredOutput); Assert.IsNull(original.IngredientAmount);
            foreach (var copy in new[] { before, after })
            {
                Assert.AreSame(output, copy.StoredOutput); Assert.AreEqual(4, copy.IngredientAmount);
                Assert.AreEqual(original.BuildingType, copy.BuildingType); Assert.AreEqual(original.TemplateGood, copy.TemplateGood);
                Assert.AreEqual(original.OutputGood, copy.OutputGood); Assert.AreEqual(original.DisplayName, copy.DisplayName);
                Assert.AreEqual(original.BaseAmount, copy.BaseAmount); Assert.AreEqual(original.WorkTicks, copy.WorkTicks);
                Assert.AreEqual(original.Priority, copy.Priority);
                var option = new WeaponProductionOption(new WeaponProductionOptionId("owner", "recipe"), copy, false);
                Assert.AreSame(output, option.StoredOutput); Assert.AreEqual(eGoods.STORED_SWORDS, option.OutputGood);
            }
            Assert.ThrowsExactly<ArgumentNullException>(() => original.WithStoredOutput(null));
        }

        [TestMethod]
        public void StoredOutputValidationRequiresTheSameOwnerArmoryAndExactRegisteredHandle()
        {
            var registered = Stored("owner", "ore");
            bool Lookup(NativeStoredGood candidate) => ReferenceEquals(candidate, registered);
            Assert.IsTrue(WeaponProductionRuntime.ValidateStoredOutput("owner", registered, Lookup, out string reason), reason);
            Assert.IsFalse(WeaponProductionRuntime.ValidateStoredOutput("other", registered,
                _ => throw new InvalidOperationException("Foreign output must reject before registration lookup."), out reason));
            Assert.IsFalse(string.IsNullOrEmpty(reason));
            Assert.IsFalse(WeaponProductionRuntime.ValidateStoredOutput("owner", Stored("owner", "ore"), Lookup, out reason),
                "Equal key values cannot forge the process-owned registration handle.");
            Assert.IsFalse(WeaponProductionRuntime.ValidateStoredOutput("owner", Stored("owner", "food", StoragePool.Granary),
                _ => throw new InvalidOperationException("Granary output must reject before registration lookup."), out reason));
            Assert.IsFalse(WeaponProductionRuntime.ValidateStoredOutput("owner", null, Lookup, out reason));
            Assert.IsFalse(WeaponProductionRuntime.ValidateStoredOutput("owner", registered, null, out reason));
        }

        [TestMethod]
        public void StoredCatalogAgreementTagsGoodIdentityPoolAndNativeSemanticsWithoutLabels()
        {
            string Digest(NativeStoredGood output, string display = "Recipe", int? ingredient = null) =>
                WeaponProductionCatalog.Compile(new[] { StoredRecipe("owner", "recipe", output, display: display, ingredient: ingredient) }, _ => true).Digest;
            var output = Stored("owner", "ore");
            string baseline = Digest(output);
            Assert.AreEqual("626CF206A44371DE6F5E6BFDEB2817512AC5B55E684065C3DA38B917AD189C2C", baseline);
            Assert.AreEqual(baseline, Digest(Stored("owner", "ore"), "Localized label"));
            Assert.AreNotEqual(baseline, Digest(Stored("owner", "other")));
            Assert.AreNotEqual(baseline, Digest(Stored("other", "ore")));
            // Catalogue serialization is independently tagged even for an invalid pool candidate;
            // public registration rejects that candidate before it can reach this compiler.
            Assert.AreNotEqual(baseline, Digest(Stored("owner", "ore", StoragePool.Granary)));
            Assert.AreNotEqual(baseline, Digest(output, ingredient: 4));
            Assert.AreNotEqual(baseline, WeaponProductionCatalog.Compile(new[] { Recipe("owner", "recipe") }, _ => true).Digest);
            var additions = new[] { StoredRecipe("owner", "a", output), Recipe("owner", "b") };
            var catalogue = WeaponProductionCatalog.Compile(additions, _ => true);
            Assert.IsTrue(catalogue.HasStoredOutputs);
            Assert.AreEqual(catalogue.Digest, WeaponProductionCatalog.Compile(Enumerable.Reverse(additions), _ => true).Digest);
        }

        [TestMethod]
        public void UnavailableStoredDeliveryLeavesSlotsAndLegacyDigestForNativeRecipes()
        {
            var custom = StoredRecipe("owner", "custom", Stored("owner", "ore"), priority: 100);
            var native = new[] { Recipe("owner", "a"), Recipe("owner", "b") };
            var catalogue = WeaponProductionCatalog.Compile(new[] { custom }.Concat(native), _ => true,
                option => option.StoredOutput == null);
            Assert.AreEqual(WeaponProductionRegistrationStatus.AdapterUnavailable, custom.Status);
            Assert.IsFalse(catalogue.HasStoredOutputs);
            Assert.AreEqual(WeaponProductionCatalog.Compile(native, _ => true).Digest, catalogue.Digest);
            Assert.AreEqual(4, catalogue.For(eStructs.STRUCT_FLETCHERS_WORKSHOP).Count);
        }

        [TestMethod]
        public void UnavailableIngredientOverrideLeavesTheSlotForACompatibleRecipe()
        {
            var type = eStructs.STRUCT_TANNERS_WORKSHOP;
            var incompatible = Tanner("a", ingredientAmount: 2);
            var compatible = Tanner("b", ingredientAmount: 1);
            var catalogue = WeaponProductionCatalog.Compile(new[] { incompatible, compatible }, _ => true,
                option => option.IngredientAmount <= 1);
            CollectionAssert.AreEqual(new[] { compatible.Option.Id },
                catalogue.For(type).Where(option => !option.IsVanilla).Select(option => option.Id).ToArray());
            Assert.AreEqual(WeaponProductionRegistrationStatus.AdapterUnavailable, incompatible.Status);
            Assert.IsFalse(WeaponProductionCatalog.Compile(new[] { incompatible }, _ => true, _ => false).HasOptions);
        }

        [TestMethod]
        public void PacketRetainsSingleTargetIdentityAndExplicitGroupScope()
        {
            string digest = new string('A', 64);
            var single = new WeaponProductionPacket(digest, 4, 77, 4_000_000_000);
            var group = new WeaponProductionPacket(digest, 4, 0, 0);
            foreach (var packet in new[] { single, group })
            {
                byte[] body = MessagePackSerializer.Serialize(packet);
                Assert.IsTrue(body.Length + sizeof(short) <= 128);
                var decoded = MessagePackSerializer.Deserialize<WeaponProductionPacket>(body);
                Assert.AreEqual(packet.Catalog, decoded.Catalog);
                Assert.AreEqual(packet.OptionIndex, decoded.OptionIndex);
                Assert.AreEqual(packet.BuildingId, decoded.BuildingId);
                Assert.AreEqual(packet.BuildingGlobalId, decoded.BuildingGlobalId);
            }
        }

        [TestMethod]
        public void FormatterRejectsMalformedPayloadBeforeAnySimulationHandler()
        {
            string digest = new string('A', 64);
            foreach (var body in new[]
            {
                Raw(0x94, 1, digest, 4, 5, 6),
                Raw(0x95, 2, digest, 4, 5, 6),
                Raw(0x95, 1, digest.Substring(1), 4, 5, 6),
                Raw(0x95, 1, digest, 0, 5, 6),
                Raw(0x95, 1, digest, 21, 5, 6),
                Raw(0x95, 1, digest, 4, 5, 0),
                Raw(0x95, 1, digest, 4, 0, 6)
            })
                Assert.ThrowsExactly<MessagePackSerializationException>(() => MessagePackSerializer.Deserialize<WeaponProductionPacket>(body));
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TransferTailsBindRecipePhaseAndProgressToTheNativeIdentity(bool storedOutput)
        {
            var registration = storedOutput ? StoredRecipe("owner", "recipe", Stored("owner", "ore")) :
                Recipe("owner", "recipe", duration: 60);
            var catalogue = WeaponProductionCatalog.Compile(new[] { registration }, _ => true);
            int option = catalogue.IndexOf(registration.Option.Id);
            var type = registration.Option.BuildingType;
            var kind = eChimps.CHIMP_TYPE_FLETCHER;
            WeaponProductionRuntime.BeginWorld(catalogue);
            try
            {
                var building = new byte[WeaponProductionRuntime.BuildingWireBytes];
                BinaryPrimitives.WriteUInt16LittleEndian(building, 12);
                BinaryPrimitives.WriteUInt32LittleEndian(building.AsSpan(2), 246);
                BinaryPrimitives.WriteUInt16LittleEndian(building.AsSpan(6), (ushort)type);
                building[8] = (byte)option;
                building[9] = (byte)(option | (1 << 5)); // Finished local product.
                BinaryPrimitives.WriteUInt16LittleEndian(building.AsSpan(10), 51);
                BinaryPrimitives.WriteUInt32LittleEndian(building.AsSpan(12), 310);
                Assert.IsTrue(WeaponProductionRuntime.ValidateBuildingWire(12, 246, type, building));
                Assert.IsFalse(WeaponProductionRuntime.ValidateBuildingWire(12, 247, type, building));
                Assert.IsFalse(WeaponProductionRuntime.ValidateBuildingWire(12, 246, eStructs.STRUCT_TANNERS_WORKSHOP, building));
                building[9] = (byte)(option | (3 << 5));
                Assert.IsFalse(WeaponProductionRuntime.ValidateBuildingWire(12, 246, type, building));

                var worker = new byte[WeaponProductionRuntime.WorkerWireBytes];
                BinaryPrimitives.WriteUInt16LittleEndian(worker, 51);
                BinaryPrimitives.WriteUInt32LittleEndian(worker.AsSpan(2), 310);
                BinaryPrimitives.WriteUInt16LittleEndian(worker.AsSpan(6), 12);
                BinaryPrimitives.WriteUInt32LittleEndian(worker.AsSpan(8), 246);
                worker[12] = (byte)option;
                BinaryPrimitives.WriteUInt16LittleEndian(worker.AsSpan(13), 59);
                worker[19] = 1; // The last-tick field participates in deduplication.
                Assert.IsTrue(WeaponProductionRuntime.ValidateWorkerWire(51, 310, kind, worker));
                Assert.IsFalse(WeaponProductionRuntime.ValidateWorkerWire(51, 310, eChimps.CHIMP_TYPE_TANNER, worker));
                Assert.IsFalse(WeaponProductionRuntime.ValidateWorkerWire(52, 310, kind, worker));
                BinaryPrimitives.WriteUInt16LittleEndian(worker.AsSpan(13), 61);
                Assert.IsFalse(WeaponProductionRuntime.ValidateWorkerWire(51, 310, kind, worker));
            }
            finally { WeaponProductionRuntime.EndWorld(); }
        }

        // Encode the independently constructed MessagePack frame so invalid input reaches the decoder.
        private static byte[] Raw(byte header, byte version, string digest, byte index, byte id, byte gid) =>
            new byte[] { header, version, 0xd9, checked((byte)digest.Length) }
                .Concat(Encoding.ASCII.GetBytes(digest)).Concat(new[] { index, id, gid }).ToArray();

        private static WeaponProductionRegistration Recipe(string owner, string id, int priority = 0,
            string display = "Recipe", int amount = 2, int duration = 60, eGoods template = eGoods.STORED_BOWS,
            eGoods output = eGoods.STORED_SWORDS, int? ingredientAmount = null) => new WeaponProductionRegistration(new WeaponProductionOption(
                new WeaponProductionOptionId(owner, id), new WeaponProductionOptionDefinition(eStructs.STRUCT_FLETCHERS_WORKSHOP,
                    template, output, display, amount, duration, priority).WithIngredientAmount(ingredientAmount), false));

        private static NativeStoredGood Stored(string owner, string local, StoragePool pool = StoragePool.Armory) =>
            new NativeStoredGood(new CustomGoodId(owner, local), pool);

        private static WeaponProductionRegistration StoredRecipe(string owner, string id, NativeStoredGood output,
            int priority = 0, string display = "Recipe", int? ingredient = null) =>
            new WeaponProductionRegistration(new WeaponProductionOption(new WeaponProductionOptionId(owner, id),
                new WeaponProductionOptionDefinition(eStructs.STRUCT_FLETCHERS_WORKSHOP, eGoods.STORED_BOWS,
                    eGoods.STORED_SWORDS, display, 2, 60, priority).WithStoredOutput(output).WithIngredientAmount(ingredient), false));

        private static WeaponProductionRegistration Tanner(string id, int? ingredientAmount = null) => new WeaponProductionRegistration(new WeaponProductionOption(
            new WeaponProductionOptionId("owner", id), new WeaponProductionOptionDefinition(eStructs.STRUCT_TANNERS_WORKSHOP,
                eGoods.STORED_LEATHER_ARMOUR, eGoods.STORED_METAL_ARMOUR, "Recipe").WithIngredientAmount(ingredientAmount), false));
    }
}
