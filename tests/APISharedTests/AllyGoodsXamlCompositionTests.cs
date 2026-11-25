using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API.Components.Noesis.XAML;

namespace APISharedTests
{
    // Portable authored XML exercises the actual installed Extender patch engine.
    // These tests establish XML composition, not Noesis measurement or rendering.
    [TestClass]
    public sealed class AllyGoodsXamlCompositionTests
    {
        private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string Api = "clr-namespace:APIShared.Presentation;assembly=APIShared";
        private static readonly XName Role = XName.Get("AllyGoodsPanelAnchors.Role", Api);
        private static readonly MethodInfo Apply = typeof(XmlPatchOperation).Assembly
            .GetType("SHCDESE.API.Components.Noesis.XAML.XamlPatcher", true)
            .GetMethod("ApplyPatches", BindingFlags.Public | BindingFlags.Static);

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ActualPatchPreservesTheCompleteOriginalTreeApartFromItsOwnedAnchors(bool background)
        {
            string original = Minimal(background);
            AssertComposition(original, Array.Empty<string>(), new[] { CurrentPatch() }, background);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void HeightMainViewAndLegacyFooterComposeInEveryPatchOrder(bool background)
        {
            string original = Minimal(background), own = CurrentPatch();
            foreach (var order in Permutations(new[] { own, ForeignPatch }))
                AssertComposition(original, new[] { ForeignPatch }, order, background);
            foreach (var order in Permutations(new[] { own, ForeignPatch, LegacyFooterPatch }))
                AssertComposition(original, new[] { ForeignPatch, LegacyFooterPatch }, order, background);
        }

        private static void AssertComposition(string original, string[] foreign, string[] ordered, bool background)
        {
            var expected = XDocument.Parse(Patch(original, foreign));
            var actual = XDocument.Parse(Patch(original, ordered));
            XElement[] marked = actual.Descendants().Where(element => element.Attribute(Role) != null).ToArray();
            XElement frame = OneRole(marked, "GoodsFrame"), footer = OneRole(marked, "Footer");
            Assert.AreEqual(XName.Get("Button", Presentation), frame.Name);
            Assert.AreEqual("320", (string)frame.Attribute("Height"), "A foreign root height must not replace the goods-frame anchor.");
            Assert.AreEqual(XName.Get("Grid", Presentation), footer.Name);
            foreach (string command in new[] { "ConfirmGoods", "CancelGoods" })
                Assert.AreEqual(1, footer.Elements().Count(element => (string)element.Attribute("CommandParameter") == command));
            Assert.AreEqual(background ? 1 : 0, marked.Count(element => (string)element.Attribute(Role) == "FooterBackground"));
            Assert.AreEqual(background ? 3 : 2, marked.Length, "Unrelated decorative or commanded buttons must not gain a role.");
            XElement[] hosts = actual.Descendants().Where(element => (string)element.Attribute(XName.Get("Name", Xaml)) ==
                "APISharedAllyGoodsExtensionsHost").ToArray();
            Assert.AreEqual(1, hosts.Length);
            Assert.AreEqual("LayoutRoot", (string)hosts[0].Parent.Attribute(XName.Get("Name", Xaml)));
            hosts[0].Remove();
            foreach (XAttribute attribute in actual.Descendants().Attributes(Role).ToArray()) attribute.Remove();
            RemoveNamespaceDeclarations(actual); RemoveNamespaceDeclarations(expected);
            Assert.IsTrue(XNode.DeepEquals(expected, actual),
                "Composing APIShared changed another element, binding, command, name, content or foreign layout attribute.");
        }
        private static XElement OneRole(IEnumerable<XElement> elements, string value)
        {
            var found = elements.Where(element => (string)element.Attribute(Role) == value).ToArray();
            Assert.AreEqual(1, found.Length, "Expected one structural anchor: " + value); return found[0];
        }
        private static void RemoveNamespaceDeclarations(XDocument document)
        {
            foreach (var attribute in document.Descendants().Attributes().Where(attribute => attribute.IsNamespaceDeclaration).ToArray())
                attribute.Remove();
        }
        private static string Patch(string original, IEnumerable<string> patches)
        {
            var operations = new List<XmlPatchOperation>();
            foreach (string patch in patches)
                foreach (var node in XDocument.Parse(patch).Root.Elements("Operation"))
                {
                    var content = node.Element("Content");
                    if (content != null) Assert.AreEqual(1, content.Elements().Count(), "SE retains only the first Content root.");
                    operations.Add(new XmlPatchOperation {
                        Type = (XmlPatchType)Enum.Parse(typeof(XmlPatchType), (string)node.Attribute("Type")),
                        XPath = (string)node.Attribute("XPath"), AttributeName = (string)node.Attribute("AttributeName"),
                        Value = (string)node.Attribute("Value"), Content = content?.Elements().Single().ToString()
                    });
                }
            // Match GameXAMLManagerAPI: concatenate operations in patch-file order,
            // then invoke the real patcher once. No replacement patch algorithm lives here.
            return (string)Apply.Invoke(null, new object[] { original, operations });
        }
        private static IEnumerable<string[]> Permutations(string[] values)
        {
            if (values.Length == 0) { yield return Array.Empty<string>(); yield break; }
            for (int index = 0; index < values.Length; index++)
                foreach (string[] rest in Permutations(values.Where((_, other) => other != index).ToArray()))
                    yield return new[] { values[index] }.Concat(rest).ToArray();
        }
        private static string CurrentPatch([CallerFilePath] string source = "")
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(source));
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "APIShared.csproj"))) directory = directory.Parent;
            Assert.IsNotNull(directory, "The current production XAML patch must be available in the tested checkout.");
            return File.ReadAllText(Path.Combine(directory.FullName, "Patches", "Assets", "GUI", "XAMLResources", "HUD_AlliesPanel.xaml"));
        }
        private static string Minimal(bool background) => @"<UserControl xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
<Grid x:Name='LayoutRoot' Width='700' Height='320'>
<Button Name='OriginalMainFrame' Height='320' Visibility='{Binding Allies_MainViewVis, Converter={StaticResource booleanToVisibilityConverter}}'/>
<Grid Visibility='{Binding Allies_MainViewVis, Converter={StaticResource booleanToVisibilityConverter}}'><Button Name='OriginalFace' Command='{Binding AlliesClickCommand}' CommandParameter='0'/></Grid>
<Button Name='OriginalGoodsFrame' Height='320' Visibility='{Binding Allies_GoodsViewVis, Converter={StaticResource booleanToVisibilityConverter}}'/>
<Grid Visibility='{Binding Allies_GoodsViewVis, Converter={StaticResource booleanToVisibilityConverter}}'>
<TextBlock Name='OriginalGoodsCaption' Text='{Binding OriginalCaption}'/><Grid Name='OriginalFooter'>" +
            (background ? "<Button Name='OriginalFooterBackground' Style='{StaticResource BDR_SH1DEe}'/>" : "") + @"
<Button Name='ForeignCommandedDecoration' Style='{StaticResource BDR_SH1DEe}' Command='{Binding ForeignCommand}' CommandParameter='Foreign'/>
<Button Name='OriginalConfirm' Command='{Binding AlliesClickCommand}' CommandParameter='ConfirmGoods'/>
<Button Name='OriginalCancel' Command='{Binding AlliesClickCommand}' CommandParameter='CancelGoods'/>
</Grid></Grid></Grid></UserControl>";

        // Authored third-party-like changes: root/main-frame height plus an independent
        // main-view control. This is not copied proprietary or foreign mod source.
        private const string ForeignPatch = @"<Patch xmlns:n='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
<Operation Type='SetAttribute' XPath='/n:UserControl/n:Grid' AttributeName='Height' Value='484'/>
<Operation Type='SetAttribute' XPath='/n:UserControl/n:Grid/n:Button[@Name=&quot;OriginalMainFrame&quot;]' AttributeName='Height' Value='484'/>
<Operation Type='Add' XPath='//n:Grid[@Visibility=&quot;{Binding Allies_MainViewVis, Converter={StaticResource booleanToVisibilityConverter}}&quot;]'><Content>
<Grid xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Name='ThirdPartyMainExtension'><Button Name='ThirdPartyAction' Command='{Binding ForeignCommand}' CommandParameter='Foreign:Action'/></Grid>
</Content></Operation></Patch>";
        private const string LegacyFooterPatch = @"<Patch xmlns:n='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
<Operation Type='Add' XPath='//n:Grid[n:Button[@CommandParameter=&quot;ConfirmGoods&quot;] and n:Button[@CommandParameter=&quot;CancelGoods&quot;]]'><Content>
<ContentControl xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Name='AutoSendDisclosureHost' Visibility='Collapsed'/>
</Content></Operation></Patch>";
    }
}
