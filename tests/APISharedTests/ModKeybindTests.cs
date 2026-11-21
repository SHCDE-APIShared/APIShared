using APIShared.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace APISharedTests
{
    [TestClass]
    public sealed class ModKeybindTests
    {
        [TestMethod]
        public void ExactChordDoesNotMatchAnAddedOrMissingModifier()
        {
            var chord = new KeybindChord(KeyCode.A, KeybindModifiers.Control);
            Assert.IsTrue(chord.Equals(new KeybindChord(KeyCode.A, KeybindModifiers.Control)));
            Assert.IsFalse(chord.Equals(new KeybindChord(KeyCode.A, KeybindModifiers.Control | KeybindModifiers.Shift)));
            Assert.IsFalse(chord.Equals(new KeybindChord(KeyCode.A)));
            Assert.AreEqual(default, new KeybindChord(KeyCode.None, KeybindModifiers.Alt));
        }

        [TestMethod]
        public void VanillaModifierEncodingIsSeparateFromThePublicChord()
        {
            Assert.AreEqual(new KeybindChord(KeyCode.Mouse3, KeybindModifiers.Shift | KeybindModifiers.Alt),
                ModKeybinds.FromGameBinding((int)KeyCode.Mouse3 | 0x10000 | 0x40000));
            Assert.AreEqual(new KeybindChord(KeyCode.F2, KeybindModifiers.Control),
                ModKeybinds.FromGameBinding((int)KeyCode.F2 | 0x20000));
            Assert.IsFalse(ModKeybinds.IsValidChord(new KeybindChord(KeyCode.F2, (KeybindModifiers)64)));
        }
    }
}
