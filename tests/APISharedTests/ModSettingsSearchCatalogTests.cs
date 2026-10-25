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

        [TestMethod]
        public void CaseFoldingDoesNotDependOnTheCurrentCulture()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
                Assert.IsTrue(ModSettingsSearchMatcher.IsSectionTitleMatch("info", "INFO"));
                Assert.IsTrue(ModSettingsSearchMatcher.IsSectionTitleMatch("INFO", "info"));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
        }

        [TestMethod]
        public void UnresolvableMarkupExtensionsAreNoTextButEscapedLiteralsAre()
        {
            WithFile("<Grid><CheckBox Content='{StaticResource Caption}' ToolTip='Plain tip'/>" +
                "<CheckBox Content='{}{Literal} title' ToolTip='{TemplateBinding ToolTip}'/>" +
                "<CheckBox Content='Second' ToolTip='{}{Escaped} tip'/></Grid>", path => {
                var model = new object();
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "markup");
                ModSettingsSearchCatalog.TryGetEntries(model, out var entries);
                Assert.IsFalse(entries.Any(e => e.Title.Contains("StaticResource") || e.ToolTip.Contains("TemplateBinding")));
                Assert.AreEqual("{Escaped} tip", entries.Single(e => e.Title == "Second").ToolTip);
            });
        }

        private static void WithFile(string xaml, Action<string> action)
        {
            string path = Path.GetTempFileName();
            try { File.WriteAllText(path, xaml); action(path); }
            finally { File.Delete(path); }
        }

        public sealed class StateFixture : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private double value;
            public int ValueReads;
            public Action OnRead;
            public double Value { get { ValueReads++; OnRead?.Invoke(); return value; } set { this.value = value; } }
            public StateFixture Child { get; set; }
            public bool Enabled { get; set; }
            public double Broken => throw new InvalidOperationException("state getter failed");
            public double Recursive
            {
                get
                {
                    Assert.AreEqual(ModSettingsSearchState.Unknown, ModSettingsSearchCatalog.GetState(this, "recursive"));
                    return Value;
                }
            }
            public void Changed() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
            public void Changed(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        [TestMethod]
        public void StateMetadataIsLiveIsolatedAndSupportsPerSliderNeutralValues()
        {
            WithFile("<Grid xmlns:s='urn:search'><Grid s:Key='row' s:Title='Row' s:ToolTipText='Help'>" +
                "<CheckBox IsChecked='{Binding Enabled}'/><Slider Value='{Binding Value}' s:InactiveValues='1;100'/>" +
                "<Slider Value='{Binding Broken}'/></Grid>" +
                "<Slider s:Key='range' s:Title='Range' ToolTip='Help' Value='{Binding Value}' s:InactiveValues='0..1'/>" +
                "<Slider s:Key='default' s:Title='Default' ToolTip='Default' Value='{Binding Value}'/></Grid>", path => {
                var model = new StateFixture { Value = 1 };
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "independent.state.fixture");
                ModSettingsSearchCatalog.TryGetEntries(model, out var metadata);
                Assert.AreEqual(ModSettingsSearchState.Inactive, ModSettingsSearchCatalog.GetState(model, "row"));
                model.Value = 0;
                Assert.AreEqual(ModSettingsSearchState.Active | ModSettingsSearchState.Inactive, ModSettingsSearchCatalog.GetState(model, "row"));
                model.Value = 100;
                Assert.AreEqual(ModSettingsSearchState.Inactive, ModSettingsSearchCatalog.GetState(model, "row"));
                model.Value = 0.5;
                Assert.AreEqual(ModSettingsSearchState.Inactive, ModSettingsSearchCatalog.GetState(model, "range"));
                foreach (double value in new[] { -1d, 0d, 1d, 100d, -2d })
                {
                    model.Value = value;
                    Assert.AreEqual(value == -1 || value == 0 ? ModSettingsSearchState.Inactive : ModSettingsSearchState.Active,
                        ModSettingsSearchCatalog.GetState(model, "default"));
                }
                model.Value = double.NaN;
                Assert.AreEqual(ModSettingsSearchState.Unknown, ModSettingsSearchCatalog.GetState(model, "default"));
                int notifications = 0;
                Action<object> handler = owner => { if (ReferenceEquals(owner, model)) notifications++; };
                ModSettingsSearchCatalog.StateChanged += handler;
                try { model.Value = 2; model.Changed(); Assert.AreEqual(1, notifications); }
                finally { ModSettingsSearchCatalog.StateChanged -= handler; }
                ModSettingsSearchCatalog.TryGetEntries(model, out var current);
                Assert.AreSame(metadata, current, "Value changes must not rebuild immutable metadata.");
            });
        }

        [TestMethod]
        public void LocalStateConverterCombinesSectionTextAndLiveValuesWithoutHidingMixedRows()
        {
            WithFile("<Grid xmlns:s='urn:search'><Grid s:IsSection='True' s:SectionKey='section' s:SectionTitle='Group'/>" +
                "<Grid s:Key='row' s:Title='Row' s:ToolTipText='Help' s:SectionKey='section' s:SectionTitle='Group'>" +
                "<CheckBox IsChecked='{Binding Enabled}'/><Slider Value='{Binding Value}'/></Grid></Grid>", path => {
                var model = new StateFixture { Enabled = false, Value = 2 };
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "local.state.fixture");
                var converter = new ModSettingsSearchStateVisibilityConverter();
                object[] values = { model, ModSettingsSearchStateFilter.Active, 0, "group", false, "", "section", "Group", "", "section", "Group" };
                Assert.AreEqual(Noesis.Visibility.Visible, converter.Convert(values, typeof(Noesis.Visibility), null, null));
                values[1] = ModSettingsSearchStateFilter.Inactive;
                Assert.AreEqual(Noesis.Visibility.Visible, converter.Convert(values, typeof(Noesis.Visibility), null, null));
                model.Enabled = true;
                Assert.AreEqual(Noesis.Visibility.Collapsed, converter.Convert(values, typeof(Noesis.Visibility), null, null));
                values[3] = "missing";
                Assert.AreEqual(Noesis.Visibility.Collapsed, converter.Convert(values, typeof(Noesis.Visibility), null, null));
                values[1] = ModSettingsSearchStateFilter.All;
                values[3] = "group";
                Assert.AreEqual(Noesis.Visibility.Visible, converter.Convert(values, typeof(Noesis.Visibility), null, null));
            });
        }

        [TestMethod]
        public void RecursiveStatusGettersAreBoundedAndAutomaticDuplicateEditorsShareState()
        {
            WithFile("<Grid xmlns:s='urn:search'><Slider s:Key='recursive' s:Title='Recursive' ToolTip='Recursive' Value='{Binding Recursive}'/>" +
                "<Grid><TextBlock Text='Number'/><TextBox Text='{Binding Value}' ToolTip='Shared help'/>" +
                "<Slider Value='{Binding Value}' ToolTip='Shared help'/></Grid></Grid>", path => {
                var model = new StateFixture { Value = 2 };
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "recursive.state.fixture");
                Assert.AreEqual(ModSettingsSearchState.Active, ModSettingsSearchCatalog.GetState(model, "recursive"));
                ModSettingsSearchCatalog.TryGetEntries(model, out var entries);
                var automatic = entries.Where(entry => entry.Title == "Number").ToArray();
                Assert.AreEqual(1, automatic.Length);
                Assert.AreEqual(ModSettingsSearchState.Active, ModSettingsSearchCatalog.GetState(model, automatic[0].Key),
                    "The associated text editor must not hide the slider's state when discovered first.");
            });
        }

        [TestMethod]
        public void UnsupportedBindingContextsNeverClassifyAnUnrelatedRootProperty()
        {
            WithFile("<Grid xmlns:s='urn:search'><Grid DataContext='{Binding Other}'>" +
                "<Slider s:Key='context' s:Title='Other context' ToolTip='Other' Value='{Binding Value}'/></Grid>" +
                "<Slider s:Key='source' s:Title='External source' ToolTip='Source' Value='{Binding Value, Source = other}'/>" +
                "<Slider s:Key='converter' s:Title='Converter' ToolTip='Converter' Value='{Binding Value, Converter = custom}'/>" +
                "<Slider s:Key='normal' s:Title='Normal' ToolTip='Normal' Value='{Binding Value, UpdateSourceTrigger=PropertyChanged}'/></Grid>", path => {
                var model = new StateFixture { Value = 2 };
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "binding.state.fixture");
                foreach (string key in new[] { "context", "source", "converter" })
                    Assert.AreEqual(ModSettingsSearchState.Unknown, ModSettingsSearchCatalog.GetState(model, key), key);
                Assert.AreEqual(ModSettingsSearchState.Active, ModSettingsSearchCatalog.GetState(model, "normal"));
            });
        }

        [TestMethod]
        public void NestedBatchesPublishOnlyTheCompletedStateAndUnwindAfterFailure()
        {
            WithFile("<Grid xmlns:s='urn:search'><Slider s:Key='value' s:Title='Value' ToolTip='Help' Value='{Binding Value}'/></Grid>", path => {
                var model = new StateFixture { Value = 2 };
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "batch.fixture");
                ModSettingsSearchCatalog.GetState(model, "value");
                int notifications = 0;
                Action<object> listener = owner => { if (ReferenceEquals(owner, model)) notifications++; };
                ModSettingsSearchCatalog.StateChanged += listener;
                try
                {
                    try
                    {
                        using (ModSettingsSearchUpdateScope.Begin(model))
                        {
                            model.Value = 0; model.Changed();
                            using (ModSettingsSearchUpdateScope.Begin(model)) { model.Value = 4; model.Changed(); }
                            Assert.AreEqual(0, notifications);
                            Assert.AreEqual(ModSettingsSearchState.Active, ModSettingsSearchCatalog.GetState(model, "value"));
                            throw new InvalidOperationException("setter failure");
                        }
                    }
                    catch (InvalidOperationException) { }
                    Assert.AreEqual(1, notifications);
                    model.Value = 0; model.Changed();
                    Assert.AreEqual(2, notifications, "A failed operation must not suppress subsequent single edits.");
                }
                finally { ModSettingsSearchCatalog.StateChanged -= listener; }
            });
        }

        [TestMethod]
        public void PresentationReusesValuesOnlyDuringItsNotificationWhilePublicQueriesStayLive()
        {
            WithFile("<Grid xmlns:s='urn:search'><Slider s:Key='value' s:Title='Value' ToolTip='Help' s:SectionKey='section' s:SectionTitle='Group' Value='{Binding Value}'/></Grid>", path => {
                var model = new StateFixture { Value = 2 };
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "presentation.fixture");
                ModSettingsSearchCatalog.GetState(model, "value");
                var converter = new ModSettingsSearchStateVisibilityConverter();
                object[] inputs = { model, ModSettingsSearchStateFilter.Active, 0, "", false, "", "section", "Group", "", "section", "Group" };
                bool allVisible = true;
                int readsDuringNotification = -1;
                ModSettingsSearchState directState = ModSettingsSearchState.Unknown;
                Action<object> listener = owner => {
                    if (!ReferenceEquals(owner, model)) return;
                    model.ValueReads = 0;
                    for (int index = 0; index < 3; index++) allVisible &= Equals(Noesis.Visibility.Visible, converter.Convert(inputs, typeof(Noesis.Visibility), null, null));
                    readsDuringNotification = model.ValueReads;
                    model.Value = 0; // Explicit non-notifying changes remain visible to public live queries.
                    directState = ModSettingsSearchCatalog.GetState(model, "value");
                };
                ModSettingsSearchCatalog.StateChanged += listener;
                try { model.Changed(); }
                finally { ModSettingsSearchCatalog.StateChanged -= listener; }
                Assert.IsTrue(allVisible);
                Assert.AreEqual(1, readsDuringNotification, "Section and row queries must share the notification snapshot.");
                Assert.AreEqual(ModSettingsSearchState.Inactive, directState);
                Assert.AreEqual(Noesis.Visibility.Collapsed, converter.Convert(inputs, typeof(Noesis.Visibility), null, null), "No value cache may survive its notification.");
            });
        }

        [TestMethod]
        public void SourceReplacementFromAValueGetterCannotReattachTheRetiredObserver()
        {
            WithFile("<Grid xmlns:s='urn:search'><Slider s:Key='value' s:Title='Value' ToolTip='Help' Value='{Binding Value}'/></Grid>", path => {
                var model = new StateFixture { Value = 2 };
                ModSettingsSearchCatalog.RegisterSource(model, path, null, "old.fixture");
                model.OnRead = () => { model.OnRead = null; ModSettingsSearchCatalog.RegisterSource(model, path, null, "new.fixture"); };
                Assert.AreEqual(ModSettingsSearchState.Unknown, ModSettingsSearchCatalog.GetState(model, "value"));
                Assert.AreEqual(ModSettingsSearchState.Active, ModSettingsSearchCatalog.GetState(model, "value"));
                int notifications = 0;
                Action<object> listener = owner => { if (ReferenceEquals(owner, model)) notifications++; };
                ModSettingsSearchCatalog.StateChanged += listener;
                try { model.Value = 0; model.Changed(); Assert.AreEqual(1, notifications); }
                finally { ModSettingsSearchCatalog.StateChanged -= listener; }
            });
        }
    }
}
