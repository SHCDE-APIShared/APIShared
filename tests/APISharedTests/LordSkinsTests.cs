using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass]
    public sealed class LordSkinsTests
    {
        [TestMethod]
        [DataRow(GM.GM_BODY_LORD, LordSkin.Crusader)]
        [DataRow(GM.GM_BODY_ARABIC_LORD, LordSkin.Arabic)]
        [DataRow(GM.GM_BODY_BEDOUIN_LORD, LordSkin.Bedouin)]
        [DataRow(GM.GM_BODY_SCRIBE_LORD, LordSkin.Scribe)]
        [DataRow(GM.GM_BODY_LORD_FEMALE, LordSkin.CrusaderFemale)]
        [DataRow(GM.GM_BODY_LORD_BESSY, LordSkin.Bessy)]
        [DataRow(GM.GM_BODY_ARABIC_LORD_FEMALE, LordSkin.ArabicFemale)]
        [DataRow(GM.GM_BODY_BEDOUIN_LORD_FEMALE, LordSkin.BedouinFemale)]
        public unsafe void LivingLordUsesCurrentMaterial(GM material, LordSkin expected)
        {
            GameUnit lord = CreateLord();
            lord.r_GameMaterialIndex = material;
            Assert.IsTrue(LordSkins.TryGetFromMaterial(material, out LordSkin skin));
            Assert.AreEqual(expected, skin);
            Assert.IsTrue(LordSkins.TryResolveUnitSkin(2, 123, &lord, out skin));
            Assert.AreEqual(expected, skin);
        }

        [TestMethod]
        public unsafe void UnknownMaterialAndInvalidLordIdentityReturnUnknown()
        {
            GameUnit lord = CreateLord();
            AssertUnknown(2, 123, null);
            AssertUnknown(2, 0, &lord);
            AssertUnknown(2, 124, &lord);
            AssertUnknown(1, 123, &lord);

            lord.r_UnitChimp = eChimps.CHIMP_TYPE_ARCHER;
            AssertUnknown(2, 123, &lord);
            lord = CreateLord();
            lord.r_AliveState = AliveState.NeedsInit;
            AssertUnknown(2, 123, &lord);
            lord = CreateLord();
            lord.r_CurrentHealth = 0;
            AssertUnknown(2, 123, &lord);
            lord = CreateLord();
            lord.r_IsKilledByProjectile = 1;
            AssertUnknown(2, 123, &lord);
            lord = CreateLord();
            lord.r_GameMaterialIndex = GM.GM_BODY_ARCHER;
            AssertUnknown(2, 123, &lord);
            Assert.IsFalse(LordSkins.TryGetFromMaterial(lord.r_GameMaterialIndex, out LordSkin skin));
            Assert.AreEqual(LordSkin.Unknown, skin);
        }

        private static unsafe void AssertUnknown(int playerId, uint globalId, GameUnit* lord)
        {
            Assert.IsFalse(LordSkins.TryResolveUnitSkin(playerId, globalId, lord, out LordSkin skin));
            Assert.AreEqual(LordSkin.Unknown, skin);
        }

        private static GameUnit CreateLord() => new GameUnit
        {
            r_UnitChimp = eChimps.CHIMP_TYPE_LORD,
            r_ControllableForPlayerId = 2,
            r_GlobalId = 123,
            r_AliveState = AliveState.IsAlive,
            r_CurrentHealth = 10,
            r_GameMaterialIndex = GM.GM_BODY_LORD
        };
    }
}
