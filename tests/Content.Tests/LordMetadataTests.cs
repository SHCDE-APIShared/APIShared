using APIShared.Content.Lords;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace APIShared.Content.Tests
{
    [TestClass]
    public class LordMetadataTests
    {
        [TestMethod]
        public void CompleteInputAndFutureFieldsSurviveAndLocalesResolveIndependently()
        {
            const string json = "{\n\"LocalizedDisplayName\":{\"en-US\":\"Doctor\",\"de-DE\":\"Doktor\"},\"LocalizedTitles\":{\"en-US\":[\", First\",\"Second\"]},\"Future\":{\"items\":[1,null,true,{\"nested\":\"kept\"}]},\"LocalizedDescription\":{\"de-DE\":\"Beschreibung\"}}";
            var document = LordMetadataDocument.Parse(json);
            Assert.AreEqual(json, document.OriginalJson);
            Assert.AreEqual("Doktor, First", LordMetadataDocument.Combine(document.ResolveName("de-DE", "Fallback"), document.ResolveTitle("de-DE", 2)));
            Assert.AreEqual("Doctor Second", LordMetadataDocument.Combine(document.ResolveName("fr-FR", "Fallback"), document.ResolveTitle("fr-FR", 1)));
            Assert.AreEqual("Beschreibung", document.ResolveText("LocalizedDescription", "de-DE"));
            Assert.AreEqual("", document.ResolveText("LocalizedDescription", "fr-FR"));
            Assert.AreEqual("", document.ResolveTitle("en-US", -1));
            Assert.AreEqual("Fallback", LordMetadataDocument.Parse("{}").ResolveName("de-DE", "Fallback"));
            Assert.AreEqual("English", LordMetadataDocument.Parse("{\"LocalizedTitles\":{\"de-DE\":[\"not-set\"],\"en-US\":[\"English\"]}}").ResolveTitle("de-DE", 0));
        }
        [TestMethod]
        public void MessageReferencesIncludeEveryClipAndNoScriptOrUnrelatedUnknownField()
        {
            var document = LordMetadataDocument.Parse("{\"FacePath\":\"face\",\"IncomingMessage\":\"incoming\",\"FuturePath\":\"script.lua\",\"Messages\":{\"Taunt1\":[{\"AudioPath\":\"voice\",\"VideoPath\":\"video\",\"LocalizedText\":{\"de-DE\":\"Hallo\"}},{\"AudioPath\":\"voice2\"}]}}");
            CollectionAssert.AreEquivalent(new[] { "face", "incoming", "voice", "video", "voice2" }, (System.Collections.ICollection)document.MediaReferences());
            Assert.IsTrue(document.GetMessageClipsJson("Taunt1").Contains("Hallo"));
            Assert.AreEqual("[]", document.GetMessageClipsJson("Missing"));
        }
    }
}
