using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class ModSettingsSearchCatalogTests
    {
        private const string Page = "<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><CheckBox Content='{Binding Caption}' ToolTip='Description'/></Grid>";

        public sealed class ForeignSettings
        {
            public string Caption { get; set; } = "Überbau rules";
            public bool Fail;
            public string Broken => Fail ? throw new InvalidOperationException("foreign getter") : "Title";
        }

        [TestMethod]
        public void MissingEmptyAndBrokenSourcesAreDistinctFromAnUnregisteredMod()
        {
            Assert.IsFalse(ModSettingsSearchCatalog.TryGetEntries(new object(), out var unknown));
            Assert.AreEqual(0, unknown.Count);
            WithFile("<Grid/>", path => {
                var model = new object();
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "arbitrary.author.mod");
                Assert.IsTrue(ModSettingsSearchCatalog.IsRegistered(model));
                Assert.IsTrue(ModSettingsSearchCatalog.TryGetEntries(model, out var empty));
                Assert.AreEqual(0, empty.Count);
                File.WriteAllText(path, "broken XML");
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "arbitrary.author.mod");
                Assert.IsTrue(ModSettingsSearchCatalog.TryGetEntries(model, out var broken));
                Assert.AreEqual(0, broken.Count);
                File.WriteAllText(path, Page);
                Assert.IsTrue(ModSettingsSearchCatalog.TryGetEntries(model, out var cachedFailure));
                Assert.AreSame(broken, cachedFailure);
            });
        }

        [TestMethod]
        public void ForeignViewModelsNeedNeitherPresetInheritanceNorNoesis()
        {
            WithFile(Page, path => {
                var model = new ForeignSettings();
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "someone.else");
                Assert.IsTrue(ModSettingsSearchCatalog.TryGetEntries(model, out var entries));
                Assert.AreEqual("Überbau rules", entries.Single().Title);
                Assert.IsFalse(ModSettingsSearchCatalog.TryNavigate(model, entries[0].Key));
                Assert.ThrowsExactly<NotSupportedException>(() => ((IList<ModSettingsSearchEntry>)entries).Clear());
                model.Caption = "Replacement";
                Assert.AreEqual("Überbau rules", entries[0].Title);
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "someone.else");
                Assert.IsTrue(ModSettingsSearchCatalog.TryGetEntries(model, out var replaced));
                Assert.AreEqual("Replacement", replaced.Single().Title);
            });
        }

        [TestMethod]
        public void NavigationValidatesCurrentKeysUsesCurrentTitlesAndIsolatesFailures()
        {
            WithFile(Page, path => {
                var model = new ForeignSettings();
                string receivedKey = null, receivedTitle = null;
                int calls = 0;
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "foreign", (key, title) => {
                    calls++; receivedKey = key; receivedTitle = title; return true;
                });
                ModSettingsSearchCatalog.TryGetEntries(model, out var entries);
                Assert.IsFalse(ModSettingsSearchCatalog.TryNavigate(model, "stale"));
                Assert.AreEqual(0, calls);
                Assert.IsTrue(ModSettingsSearchCatalog.TryNavigate(model, entries[0].Key));
                Assert.AreEqual(entries[0].Key, receivedKey);
                Assert.AreEqual(entries[0].Title, receivedTitle);
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "foreign", (key, title) => throw new Exception("navigation"));
                Assert.IsFalse(ModSettingsSearchCatalog.TryNavigate(model, entries[0].Key));
                Assert.IsFalse(ModSettingsSearchCatalog.TryNavigate(model, entries[0].Key));
            });
        }

        [TestMethod]
        public void SourceNotificationsArePublishedAfterReplacementAndIsolateObservers()
        {
            var model = new ForeignSettings();
            int notifications = 0;
            Action<object> throwing = value => { if (ReferenceEquals(value, model)) throw new Exception("observer"); };
            Action<object> healthy = value => {
                if (!ReferenceEquals(value, model)) return;
                Assert.IsTrue(ModSettingsSearchCatalog.IsRegistered(value));
                notifications++;
            };
            ModSettingsSearchCatalog.SourceChanged += throwing;
            ModSettingsSearchCatalog.SourceChanged += healthy;
            try
            {
                WithFile(Page, path => {
                    ModSettingsSearchCatalog.RegisterSource(model, path, null, "foreign");
                    ModSettingsSearchCatalog.RegisterSource(model, path, null, "foreign");
                    Assert.ThrowsExactly<ArgumentException>(() => ModSettingsSearchCatalog.RegisterSource(model, "relative.xaml", null, "foreign"));
                });
                Assert.AreEqual(2, notifications);
            }
            finally
            {
                ModSettingsSearchCatalog.SourceChanged -= throwing;
                ModSettingsSearchCatalog.SourceChanged -= healthy;
            }
        }

        [TestMethod]
        public void RecursiveSourceObserversAndNavigationCannotOverflowTheStack()
        {
            WithFile(Page, path => {
                var model = new ForeignSettings();
                int notifications = 0;
                Action<object> recursive = value => {
                    if (!ReferenceEquals(value, model)) return;
                    notifications++;
                    ModSettingsSearchCatalog.RegisterSource(model, path, null, "recursive");
                };
                ModSettingsSearchCatalog.SourceChanged += recursive;
                try { ModSettingsSearchCatalog.RegisterSource(model, path, null, "recursive"); }
                finally { ModSettingsSearchCatalog.SourceChanged -= recursive; }
                Assert.AreEqual(1, notifications);
                int navigations = 0;
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "recursive", (key, title) => {
                    navigations++;
                    // Replacing the source must not bypass the per-model recursion guard.
                    ModSettingsSearchCatalog.RegisterSource(model, path, null, "replacement", (k, t) => true);
                    Assert.IsFalse(ModSettingsSearchCatalog.TryNavigate(model, key));
                    return true;
                });
                ModSettingsSearchCatalog.TryGetEntries(model, out var entries);
                Assert.IsTrue(ModSettingsSearchCatalog.TryNavigate(model, entries[0].Key));
                Assert.AreEqual(1, navigations);
                Assert.IsTrue(ModSettingsSearchCatalog.TryNavigate(model, entries[0].Key));
            });
        }

        [TestMethod]
        public void GetterFailuresAndRecursiveCatalogReadsCannotEscape()
        {
            WithFile(Page.Replace("Caption", "Broken"), path => {
                var model = new ForeignSettings { Fail = true };
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "foreign");
                Assert.IsTrue(ModSettingsSearchCatalog.TryGetEntries(model, out var entries));
                Assert.AreEqual(0, entries.Count);
            });
            WithFile(Page, path => {
                var model = new RecursiveSettings();
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "recursive");
                Assert.IsTrue(ModSettingsSearchCatalog.TryGetEntries(model, out var entries));
                Assert.AreEqual("Recursive title", entries.Single().Title);
            });
        }

        public sealed class RecursiveSettings
        {
            public string Caption
            {
                get
                {
                    ModSettingsSearchCatalog.TryGetEntries(this, out var pending);
                    Assert.AreEqual(0, pending.Count);
                    return "Recursive title";
                }
            }
        }

        [TestMethod]
        public void UnicodeMatchingRepairsOnlyMalformedSurrogatesAndRetainsExactKeys()
        {
            foreach (string invalid in new[] { "\uD800", "\uDC00", "\uD800x\uDC00", "\uFFFE" })
            {
                Assert.IsTrue(ModSettingsSearchMatcher.IsMatch("test" + invalid, false, null, "key", "test" + invalid, ""));
                Assert.IsTrue(ModSettingsSearchMatcher.IsMatch("test", false, null, "key", "test" + invalid, ""));
                Assert.IsTrue(ModSettingsSearchMatcher.IsMatch("test", true, null, "key", "", "test" + invalid));
            }
            Assert.IsTrue(ModSettingsSearchMatcher.IsMatch("😀", false, null, "key", "Title 😀", ""));
            Assert.IsTrue(ModSettingsSearchMatcher.IsMatch("uberbau", false, null, "key", "Überbau", ""));
            Assert.IsTrue(ModSettingsSearchMatcher.IsMatch("gate range", true, null, "key", "Gate", "Range"));
            Assert.IsFalse(ModSettingsSearchMatcher.IsMatch("gate range", false, null, "key", "Gate", "Range"));
            Assert.IsTrue(ModSettingsSearchMatcher.IsSectionTitleMatch("market", "Market 😀"));
            Assert.IsTrue(ModSettingsSearchMatcher.IsMatch("ignored", false, "\uD800", "\uD800", "", ""));
            Assert.IsFalse(ModSettingsSearchMatcher.IsMatch("ignored", false, "\uD800", "\uFFFD", "", ""));
        }

        [TestMethod]
        public void EverySingleUtf16CodeUnitCanBeComparedWithoutAnException()
        {
            for (int code = char.MinValue; code <= char.MaxValue; code++)
            {
                string text = ((char)code).ToString();
                try { Assert.IsTrue(ModSettingsSearchMatcher.IsSectionTitleMatch(text, text), "Code unit: " + code.ToString("x4")); }
                catch (ArgumentException ex) { Assert.Fail("Code unit " + code.ToString("x4") + ": " + ex.Message); }
            }
        }

        [TestMethod]
        public void SupplementaryPlaneBoundariesAndNoncharactersAreSafe()
        {
            for (int plane = 1; plane <= 16; plane++)
            {
                foreach (int offset in new[] { 0, 0x1234, 0xFFFD, 0xFFFE, 0xFFFF })
                {
                    string value = char.ConvertFromUtf32((plane << 16) + offset);
                    Assert.IsTrue(ModSettingsSearchMatcher.IsSectionTitleMatch(value, value));
                    Assert.IsTrue(ModSettingsSearchMatcher.IsSectionTitleMatch("uberbau", "Überbau" + value));
                }
            }
        }

        [TestMethod]
        public void AutomaticTitleTruncationNeverCutsAValidSurrogatePair()
        {
            foreach (int position in new[] { 115, 116, 117, 118 })
            {
                string tooltip = new string('a', position) + "😀" + new string('b', 12);
                WithFile("<Grid><TextBox ToolTip='" + tooltip + "'/></Grid>", path => {
                    var model = new object();
                    ModSettingsSearchCatalog.RegisterSource(model, path, null, "unicode");
                    ModSettingsSearchCatalog.TryGetEntries(model, out var entries);
                    string title = entries.Single().Title;
                    Assert.IsTrue(title.Length <= 120);
                    Assert.IsTrue(title.EndsWith("...", StringComparison.Ordinal));
                    Assert.IsNotNull(title.Normalize());
                });
            }
        }

        private static void WithFile(string xaml, Action<string> action)
        {
            string path = Path.GetTempFileName();
            try { File.WriteAllText(path, xaml); action(path); }
            finally { File.Delete(path); }
        }
    }
}
