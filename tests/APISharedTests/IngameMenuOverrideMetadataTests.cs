using System;
using System.Reflection;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class IngameMenuOverrideMetadataTests
    {
        [TestMethod]
        public void OriginalButtonOverrideIsAnAdditivePublicContract()
        {
            var method = typeof(IngameMenuActions).GetMethod("TryOverrideVanillaAction", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(string), typeof(IngameMenuRow), typeof(Func<bool?>), typeof(Action),
                    typeof(IngameMenuActionRegistration).MakeByRefType(), typeof(string).MakeByRefType() }, null);
            Assert.IsNotNull(method); Assert.AreEqual(typeof(bool), method.ReturnType);
            Assert.IsTrue(method.GetParameters()[5].IsOut); Assert.IsTrue(method.GetParameters()[6].IsOut);
            // Existing placement and content contracts remain callable alongside this opt-in API.
            Assert.IsNotNull(typeof(IngameMenuActions).GetMethod("TryRegister", BindingFlags.Public | BindingFlags.Static));
            Assert.IsNotNull(typeof(IngameMenuActions).GetMethod("TryRegisterContent", BindingFlags.Public | BindingFlags.Static));
            Assert.IsNotNull(typeof(IngameMenuActionState).GetConstructor(new[] { typeof(string), typeof(bool), typeof(bool) }));
        }
    }
}
