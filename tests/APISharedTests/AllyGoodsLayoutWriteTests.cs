using System;
using System.Reflection;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using Noesis;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class AllyGoodsLayoutWriteTests
    {
        private static object liveValue;
        private static int reads, writes;

        [TestMethod]
        public void UnchangedGeometrySkipsSettersAndForeignChangesAreReadAgain()
        {
            foreach (string property in new[] { "Height", "Width", "MaxHeight", "MaxWidth" })
                CheckSetter(property, 80f, 100f, 140f);
            CheckSetter("Margin", new Thickness(0, 320, 0, 0), new Thickness(0, 484, 0, 0), new Thickness(1, 2, 3, 4));
            CheckSetter("Visibility", Visibility.Visible, Visibility.Collapsed, Visibility.Hidden);
        }

        private static void CheckSetter<T>(string property, T initial, T desired, T foreign)
        {
            // Execute the compiled production guard; only the external Noesis
            // property boundary is replaced. Null is intentional: the fixture must
            // never construct native controls or imply a rendered-layout test.
            using (var definition = new DynamicMethodDefinition(typeof(AllyGoodsPanelExtensions).GetMethod(
                "SetLayout" + property, BindingFlags.NonPublic | BindingFlags.Static)))
            {
                int replacements = 0;
                foreach (var instruction in definition.Definition.Body.Instructions)
                {
                    if (!(instruction.Operand is MethodReference method) ||
                        method.DeclaringType.Namespace != "Noesis") continue;
                    string replacement;
                    if (method.Name == "get_" + property) replacement = nameof(Read);
                    else if (method.Name == "set_" + property) replacement = nameof(Write);
                    else continue;
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = definition.Definition.Module.ImportReference(typeof(AllyGoodsLayoutWriteTests)
                        .GetMethod(replacement, BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(T)));
                    replacements++;
                }
                Assert.AreEqual(2, replacements, "The fixture must replace exactly the target property getter and setter.");
                var apply = (Action<FrameworkElement, T>)definition.Generate().CreateDelegate(typeof(Action<FrameworkElement, T>));
                try
                {
                    liveValue = initial; reads = writes = 0;
                    apply(null, desired);
                    Assert.AreEqual(1, writes); Assert.AreEqual(desired, liveValue);
                    for (int index = 0; index < 8; index++) apply(null, desired);
                    Assert.AreEqual(1, writes, "Stable GUI passes must not repeat the native setter.");
                    Assert.AreEqual(9, reads, "Stable desired values must not bypass live ownership/geometry observations.");
                    liveValue = foreign;
                    apply(null, desired);
                    Assert.AreEqual(2, writes); Assert.AreEqual(10, reads); Assert.AreEqual(desired, liveValue);
                }
                finally { liveValue = null; reads = writes = 0; }
            }
        }
        private static T Read<T>(UIElement element) { reads++; return (T)liveValue; }
        private static void Write<T>(UIElement element, T value) { writes++; liveValue = value; }
    }
}
