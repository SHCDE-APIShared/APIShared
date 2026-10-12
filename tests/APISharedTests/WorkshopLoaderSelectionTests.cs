using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Tests
{
    [TestClass]
    public sealed class WorkshopLoaderSelectionTests
    {
        // Execute the installed BepInEx selectors rather than substitute their key semantics.
        private static Func<PluginInfo, T> Selector<T>()
        {
            var candidates = typeof(Chainloader).GetNestedTypes(BindingFlags.NonPublic)
                .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.Name.StartsWith("<Start>b__", StringComparison.Ordinal) && m.ReturnType == typeof(T) &&
                    m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(PluginInfo)).ToArray();
            Assert.AreEqual(1, candidates.Length, "Installed loader selection contract changed; re-audit before migration.");
            var method = candidates[0];
            return (Func<PluginInfo, T>)Delegate.CreateDelegate(typeof(Func<PluginInfo, T>),
                method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType, true), method);
        }
        private static PluginInfo Plugin(string version, string location)
        {
            var plugin = new PluginInfo();
            typeof(PluginInfo).GetProperty("Metadata").SetValue(plugin, new BepInPlugin("APIShared_Serp", "APIShared", version), null);
            typeof(PluginInfo).GetProperty("Location").SetValue(plugin, location, null);
            return plugin;
        }
        [TestMethod]
        public void InstalledLoaderPrefersNewCentralVersionButEqualOrNewerForeignCopiesAreAmbiguous()
        {
            var guid = Selector<string>(); var version = Selector<Version>();
            var central = Plugin("0.7.1", "central");
            var old = Plugin("0.4.10", "embedded");
            Assert.AreSame(central, new[] { old, central }.GroupBy(guid).Single().OrderByDescending(version).First());
            var equal = Plugin("0.7.1", "foreign");
            Assert.AreSame(equal, new[] { equal, central }.OrderByDescending(version).First());
            Assert.AreSame(central, new[] { central, equal }.OrderByDescending(version).First());
            Assert.AreEqual("foreign", new[] { central, Plugin("0.8.0", "foreign") }.OrderByDescending(version).First().Location);
        }
    }
}
