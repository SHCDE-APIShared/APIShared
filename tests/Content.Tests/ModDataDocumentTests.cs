using System;
using System.IO;
using System.Text;
using APIShared.ModData;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Content.Tests
{
    [TestClass]
    public class ModDataDocumentTests
    {
        [TestMethod]
        public void EntireDocumentAndEveryNamespaceAreValidated()
        {
            foreach (string json in new[] { "[]", "{\"a\":{},\"A\":{}}", "{\"a\":{},\"b\":4}", "{\" \":{}}", "{" })
                Assert.AreEqual(ModDataReadStatus.InvalidDocument, ModDataDocumentJson.ReadNamespace(json, "a", "test").Status);
            Assert.AreEqual(ModDataReadStatus.InvalidRequest, ModDataDocumentJson.ReadNamespace("{}", " ", "test").Status);
            Assert.AreEqual(ModDataReadStatus.NamespaceNotFound, ModDataDocumentJson.ReadNamespace("{}", "a", "test").Status);
        }

        [TestMethod]
        public void LimitsCountCompactUtf8NamespacesAndRawDocumentBytes()
        {
            // {"text":""} has eleven compact UTF-8 bytes; string whitespace must be retained.
            string atLimit = "{\"a\":{\"text\":\"" + new string(' ', ModDataDocumentJson.MaximumNamespaceBytes - 11) + "\"}}";
            Assert.IsTrue(ModDataDocumentJson.ReadNamespace(atLimit, "a", "test").Success);
            Assert.AreEqual(ModDataReadStatus.InvalidDocument, ModDataDocumentJson.ReadNamespace(atLimit.Replace("\"}}", "x\"}}"), "a", "test").Status);
            string documentLimit = "{}" + new string(' ', ModDataDocumentJson.MaximumDocumentBytes - 2);
            Assert.AreEqual(0, ModDataDocumentJson.Parse(documentLimit).Count);
            Assert.ThrowsExactly<InvalidDataException>(() => ModDataDocumentJson.Parse(documentLimit + " "));
            Assert.ThrowsExactly<InvalidDataException>(() => ModDataDocumentJson.Parse("{\"a\":{\"text\":\"水水\"}}", 100, 16));
        }

        [TestMethod]
        public void StrictStreamReadAcceptsBomLeavesOpenAndRejectsMalformedUtf8()
        {
            using (var stream = new MemoryStream(new UTF8Encoding(true).GetPreamble()))
                Assert.ThrowsExactly<InvalidDataException>(() => ModDataDocumentJson.ReadUtf8(stream));
            byte[] bytes = Encoding.UTF8.GetBytes("\uFEFF{\"a\":{\"text\":\"水\"}}");
            using (var stream = new MemoryStream(bytes))
            {
                Assert.AreEqual("{\"a\":{\"text\":\"水\"}}", ModDataDocumentJson.ReadUtf8(stream));
                Assert.IsTrue(stream.CanRead);
            }
            using (var stream = new MemoryStream(new byte[] { 0xff }))
                Assert.ThrowsExactly<DecoderFallbackException>(() => ModDataDocumentJson.ReadUtf8(stream));
        }

        [TestMethod]
        public void OversizeStreamStopsAfterOneExtraByte()
        {
            using (var stream = new MemoryStream(new byte[1024 * 1024]))
            {
                Assert.ThrowsExactly<InvalidDataException>(() => ModDataDocumentJson.ReadUtf8(stream, 32));
                Assert.AreEqual(33L, stream.Position);
            }
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ModDataDocumentJson.Parse("{}", 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ModDataDocumentJson.Parse("{}", 100, 0));
        }

        [TestMethod]
        public void FailureFactoryCannotConstructSuccessfulOrUnknownResults()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ModDataReadResult.Failure(ModDataReadStatus.Success, "a", "source", ""));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ModDataReadResult.Failure((ModDataReadStatus)999, "a", "source", ""));
            var result = ModDataReadResult.Failure(ModDataReadStatus.HostDataUnavailable, "a", null, null);
            Assert.IsNull(result.Data); Assert.IsNull(result.Json); Assert.AreEqual("", result.Source);
        }
    }
}
