using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using APIShared;
using APIShared.Compatibility;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class FixesWheatStartupTests
    {
        [TestMethod]
        public void MissingFixesHasNoCorrectionAndNoSecondAttempt()
        {
            var once = typeof(FixesWheatStartup).GetField("attempted", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = once.GetValue(null);
            bool existed = Chainloader.PluginInfos.TryGetValue("fixes", out var prior);
            var messages = new List<string>();
            using (var logger = new ManualLogSource("FixesWheatAbsentFixture"))
            {
                logger.LogEvent += (_, entry) => messages.Add(entry.Data.ToString());
                try
                {
                    Chainloader.PluginInfos.Remove("fixes");
                    once.SetValue(null, 0);
                    FixesWheatStartup.Initialize(logger);
                    FixesWheatStartup.Initialize(logger);
                    Assert.AreEqual(1, messages.Count);
                    StringAssert.Contains(messages[0], "NOT-NEEDED: Fixes absent");
                }
                finally
                {
                    once.SetValue(null, previous);
                    if (existed) Chainloader.PluginInfos["fixes"] = prior;
                }
            }
        }

        [TestMethod]
        public void CompiledDependencyOrdersFixesBeforeApiSharedAndAllowsAbsence()
        {
            var dependencies = typeof(APISharedPlugin).GetCustomAttributes(typeof(BepInDependency), false)
                .Cast<BepInDependency>().ToArray();
            var fixes = dependencies.Single(d => d.DependencyGUID == "fixes");
            Assert.AreEqual(BepInDependency.DependencyFlags.SoftDependency, fixes.Flags);
            foreach (bool installed in new[] { false, true })
            {
                var graph = new SortedDictionary<string, IEnumerable<string>>(StringComparer.InvariantCultureIgnoreCase)
                {
                    ["000shcdese"] = Array.Empty<string>(),
                    [APISharedPlugin.PluginGuid] = dependencies.Select(d => d.DependencyGUID),
                    ["foreign.consumer"] = new[] { APISharedPlugin.PluginGuid }
                };
                if (installed) graph.Add("fixes", new[] { "000shcdese" });
                // This is the installed Chainloader's sorting operation, including missing soft nodes.
                var order = Utility.TopologicalSort(graph.Keys,
                    key => graph.TryGetValue(key, out var next) ? next : Array.Empty<string>())
                    .Where(graph.ContainsKey).ToList();
                Assert.IsTrue(order.IndexOf("000shcdese") < order.IndexOf(APISharedPlugin.PluginGuid));
                Assert.IsTrue(order.IndexOf(APISharedPlugin.PluginGuid) < order.IndexOf("foreign.consumer"));
                if (installed) Assert.IsTrue(order.IndexOf("fixes") < order.IndexOf(APISharedPlugin.PluginGuid));
                else Assert.IsFalse(order.Contains("fixes"));
            }
        }

        [TestMethod]
        public void UnsupportedBinaryIsRefusedWithoutInvokingNativeWriter()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "unknown binary");
                var method = typeof(FixesWheatStartup).GetMethod("VerifyFile", BindingFlags.Static | BindingFlags.NonPublic);
                var error = Assert.ThrowsExactly<TargetInvocationException>(() =>
                    method.Invoke(null, new object[] { path, FixesWheatContract.NativeHash }));
                Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
            }
            finally { File.Delete(path); }
        }
    }
}
