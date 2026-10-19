using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared.ModData;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Content.Tests
{
    [TestClass]
    public class ModDataTests
    {
        [TestMethod]
        public void PublishedContentSurfaceRetainsItsOwnAssemblyContract()
        {
            var actual = ApiCompatibility.ApiSignatures.PublicSurface(typeof(ModDataNamespaceReader).Assembly.GetExportedTypes());
            actual.Sort(StringComparer.Ordinal);
            string path = System.IO.Path.Combine(ApiCompatibility.ApiBaselineFiles.Directory(), "Content.PublicApi.txt");
            if (Environment.GetEnvironmentVariable(ApiCompatibility.ApiBaselineFiles.UpdateVariable) == "1")
                ApiCompatibility.ApiBaselineFiles.Write(path, actual);
            CollectionAssert.AreEqual(ApiCompatibility.ApiBaselineFiles.Entries(path), actual);
        }

        [TestMethod]
        public void NamespaceSnapshotsAreIsolatedAndRecursivelyImmutable()
        {
            const string json = "{\"Author.Mod\":{\"nested\":{\"x\":7},\"array\":[{\"v\":true},null]},\"Other.Mod\":{\"hidden\":3}}";
            var first = ModDataNamespaceReader.Read("\uFEFF" + json, "AUTHOR.MOD", "opaque source");
            Assert.IsTrue(first.Success); Assert.AreEqual("Author.Mod", first.ModGuid);
            Assert.AreEqual("opaque source", first.Source); Assert.IsFalse(first.Data.ContainsKey("hidden"));
            Assert.ThrowsExactly<NotSupportedException>(() => ((IDictionary<string, object>)first.Data).Add("new", 1));
            var nested = (IDictionary<string, object>)first.Data["nested"];
            Assert.ThrowsExactly<NotSupportedException>(() => nested["x"] = 8);
            var array = (IList<object>)first.Data["array"];
            Assert.ThrowsExactly<NotSupportedException>(() => array.Add(2));
            Assert.ThrowsExactly<NotSupportedException>(() => ((IDictionary<string, object>)array[0])["v"] = false);
            var second = ModDataNamespaceReader.Read(json, "author.mod", null);
            Assert.AreNotSame(first.Data, second.Data); Assert.AreNotSame(first.Data["nested"], second.Data["nested"]);
            Assert.IsTrue(first.Json.EndsWith("\r\n", StringComparison.Ordinal));
            Assert.IsFalse(first.Json.Contains("Other.Mod"));
        }

        [TestMethod]
        public void InvalidContainersRejectEvenWhenRequestedNamespaceIsValid()
        {
            foreach (string json in new[] { null, "", "{", "[]", "null", "{\"a\":{},\"A\":{}}", "{\"a\":{},\"other\":1}", "{\"a\":{},\" \":{}}", "{\"a\":{},\"a\":{}}" })
                Assert.AreEqual(ModDataReadStatus.InvalidDocument, ModDataNamespaceReader.Read(json, "a", "memory").Status, json);
            Assert.AreEqual(ModDataReadStatus.NamespaceNotFound, ModDataNamespaceReader.Read("{\"other\":{}}", "a", "memory").Status);
            Assert.AreEqual(ModDataReadStatus.InvalidRequest, ModDataNamespaceReader.Read("{", " ", "memory").Status);
        }

        [TestMethod]
        public void GenericReaderDoesNotAcquireLordSpecificSizeLimits()
        {
            string value = new string('x', 70000);
            var result = ModDataNamespaceReader.Read("{\"arbitrary.third-party\":{\"value\":\"" + value + "\"}}", "arbitrary.third-party", "map");
            Assert.IsTrue(result.Success); Assert.AreEqual(value, result.Data["value"]);
        }

        [TestMethod]
        public void ContentAssemblyHasNoGameOrRuntimeDependencies()
        {
            var assembly = typeof(ModDataNamespaceReader).Assembly;
            Assert.AreEqual("APIShared.Content", assembly.GetName().Name);
            foreach (var dependency in assembly.GetReferencedAssemblies())
                Assert.AreEqual("netstandard", dependency.Name);
            Assert.AreEqual(".NETStandard,Version=v2.0", assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>().FrameworkName);
        }
    }
}
