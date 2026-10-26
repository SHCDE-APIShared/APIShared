using APIShared.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APISharedTests
{
    [TestClass]
    public class LocalizationTests
    {
        [TestMethod]
        public void CatalogFormatPreservesUnicodeWhitespaceEqualsAndEscapedLines()
        {
            var values = TextCatalog.Parse("\uFEFF# comment\nTitle=Änderung 日本語\nFragment= trailing = text \nLines=one\\ntwo\\\\n\n");
            Assert.AreEqual("Änderung 日本語", values["Title"]);
            Assert.AreEqual(" trailing = text ", values["Fragment"]);
            Assert.AreEqual("one\ntwo\\n", values["Lines"]);
            Assert.ThrowsExactly<InvalidDataException>(() => TextCatalog.Parse("A=one\nA=two"));
            Assert.ThrowsExactly<InvalidDataException>(() => TextCatalog.Parse("broken entry"));
        }

        [TestMethod]
        public void MissingFilesAndKeysFallBackToEmbeddedEnglishAndLookupsAreCached()
        {
            var reads = new Dictionary<string, int>();
            var catalog = new TextCatalog("A=English\nB=Fallback\n", locale => {
                reads[locale] = reads.TryGetValue(locale, out int count) ? count + 1 : 1;
                return locale == "de-DE" ? "A=Deutsch\n" : null;
            });
            for (int i = 0; i < 20; i++) {
                Assert.AreEqual("Deutsch", catalog.Get("de-DE", "A"));
                Assert.AreEqual("Fallback", catalog.Get("de-DE", "B"));
            }
            Assert.AreEqual(1, reads["en-US"]);
            Assert.AreEqual(1, reads["de-DE"]);
            Assert.AreEqual("English", catalog.Get("fr-FR", "A"));
        }

        [TestMethod]
        public void CorruptOrMismatchedExternalCatalogCannotReplaceTheSafeEnglishCatalog()
        {
            foreach (string broken in new[] { "A=Hallo {Wrong}", "A=one\nA=two", "NotAKey=bad", "malformed" })
            {
                var warnings = new List<string>();
                var catalog = new TextCatalog("A=Hello {Name}\n", locale => locale == "de-DE" ? broken : null, warnings.Add);
                Assert.AreEqual("Hello a", catalog.Get("de-DE", "A", "Name", "a"));
                Assert.AreEqual("Hello b", catalog.Get("de-DE", "A", "Name", "b"));
                Assert.AreEqual(1, warnings.Count, "A rejected file is diagnosed once per cached locale.");
            }
            foreach (Exception failure in new Exception[] { new IOException(), new UnauthorizedAccessException(), new System.Text.DecoderFallbackException() })
            {
                var catalog = new TextCatalog("A=Safe English\n", locale => locale == "de-DE" ? throw failure : null);
                Assert.AreEqual("Safe English", catalog.Get("de-DE", "A"));
            }
        }

        [TestMethod]
        public void PlaceholderValuesAreNotReinterpretedAndTemplateOrderingCanChange()
        {
            var catalog = new TextCatalog("A={Name} / {Version}\n", locale => locale == "de-DE" ? "A={Version}: {Name}\n" : null);
            Assert.AreEqual("2: {Version}", catalog.Get("de-DE", "A", "Name", "{Version}", "Version", 2));
            Assert.IsTrue(TextCatalog.SamePlaceholders("{A} {B}", "{B}: {A}"));
            Assert.IsFalse(TextCatalog.SamePlaceholders("{A} {B}", "{A}"));
        }

        [TestMethod]
        public void GameLanguagePriorityAndSteamNamesAreNormalizedWithoutUsingOsCulture()
        {
            Assert.AreEqual("de-DE", LocaleSelection.Select("deDE", "english", "fr-FR"));
            Assert.AreEqual("en-US", LocaleSelection.Select("detect", "english", "de-DE"));
            Assert.AreEqual("fr-FR", LocaleSelection.Select(null, null, "frFR"));
            Assert.AreEqual("en-US", LocaleSelection.Select(null, null, null));
            Assert.AreEqual("zh-HK", LocaleSelection.Normalize("tchinese"));
            Assert.AreEqual("pt-BR", LocaleSelection.Normalize("brazilian"));
            Assert.AreEqual("de-DE", LocaleSelection.Normalize("de_AT"));
            Assert.AreEqual("en-US", LocaleSelection.Normalize("../../other"));
        }

        [TestMethod]
        public void EveryShippedCatalogHasTheSameKeysAndPlaceholders()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "APIShared.csproj"))) directory = directory.Parent;
            Assert.IsNotNull(directory, "Test checkout root is unavailable.");
            string root = Path.Combine(directory.FullName, "Locales");
            CollectionAssert.AreEquivalent(LocaleSelection.SupportedLocales.ToArray(), Directory.GetFiles(root, "*.txt").Select(Path.GetFileNameWithoutExtension).ToArray(),
                "The supported game languages and shipped catalog inventory must agree.");
            var english = TextCatalog.Parse(File.ReadAllText(Path.Combine(root, "en-US.txt")));
            foreach (string path in Directory.GetFiles(root, "*.txt"))
            {
                var locale = TextCatalog.Parse(File.ReadAllText(path));
                CollectionAssert.AreEquivalent(english.Keys.ToArray(), locale.Keys.ToArray(), path);
                foreach (var entry in english) Assert.IsTrue(TextCatalog.SamePlaceholders(entry.Value, locale[entry.Key]), path + ": " + entry.Key);
            }
            Assert.AreEqual("Different mods", english["LobbyMismatch.Title"]);
            var german = TextCatalog.Parse(File.ReadAllText(Path.Combine(root, "de-DE.txt")));
            Assert.AreEqual("Unterschiedliche Mods", german["LobbyMismatch.Title"]);
        }
    }
}
