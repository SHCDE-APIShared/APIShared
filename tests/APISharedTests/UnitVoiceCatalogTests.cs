using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using APIShared.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace APISharedTests
{
    [TestClass]
    public sealed class UnitVoiceCatalogTests
    {
        [TestMethod]
        public void CatalogMatchesEveryAuditedNativeTableWithoutInventingManagedRoutes()
        {
            var files = new InstalledAudioFiles(InstalledTestEnvironment.GameDirectory());
            files.Validate(UnitVoiceNativeContract.Hash);
            byte[] image = File.ReadAllBytes(files.Native);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string name) { if (!string.Equals(name, "Null.wav", StringComparison.OrdinalIgnoreCase)) expected.Add(name); }
            // These offsets and strides are the hash-bound table contract under test.
            for (int row = 0; row < 42; row++)
                for (int bank = 0; bank < 27; bank++) Add(PointerString(image, 0x2AD298 + row * 0x150 + bank * 8));
            for (int row = 0; row < 42; row++) Add(PointerString(image, 0x2B0EB0 + row * 8));
            for (int row = 0; row < 40; row++)
                foreach (int column in new[] { 8, 16 }) Add(PointerString(image, 0x2B09B0 + row * 32 + column));
            for (int row = 65; row < 77; row++) Add(ReadString(image, 0x2B1000 + row * 1000));
            foreach (int table in new[] { 0x2D7E50, 0x2D8E50 })
                for (int row = 0; row < 126; row++) Add(ReadString(image, table + row * 32));

            var actual = new HashSet<string>(UnitVoiceCatalog.Cues.Select(cue => cue.FileName), StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(expected.SetEquals(actual), "The native tables define discovery, rather than filename heuristics or language-folder inventory.");
            Assert.AreEqual(UnitVoiceCatalog.Cues.Count, actual.Count);
            Assert.AreEqual(UnitVoiceCatalog.Cues.Count, UnitVoiceCatalog.Cues.Select(cue => cue.Key).Distinct(StringComparer.Ordinal).Count());
            Assert.IsFalse(actual.Contains("Null.wav"));

            var folders = ManagedFolders(files.Managed);
            foreach (var cue in UnitVoiceCatalog.Cues)
            {
                folders.TryGetValue(cue.FileName, out string expectedFolder);
                Assert.AreEqual(expectedFolder, UnitVoiceCatalog.SpeechFolder(cue), cue.FileName);
                Assert.IsTrue(UnitVoiceCatalog.TryGetCue(cue.Key, out var found)); Assert.AreSame(cue, found);
            }
            Assert.IsTrue(UnitVoiceCatalog.TryResolveFileName("fx\\speech\\Bedouin_M1.wav", out var bedouin));
            Assert.AreEqual("troops", UnitVoiceCatalog.SpeechFolder(bedouin));
            Assert.IsTrue(UnitVoiceCatalog.TryResolveFileName("Peasant_Female120.wav", out var civilian));
            Assert.AreEqual("peasants", UnitVoiceCatalog.SpeechFolder(civilian));
            Assert.IsTrue(UnitVoiceCatalog.TryResolveFileName("aengineer_pouroil2.wav", out var unmapped));
            Assert.IsNull(UnitVoiceCatalog.SpeechFolder(unmapped), "A native intent absent from speechFolders must not silently receive a guessed playback route.");
            Assert.IsFalse(UnitVoiceCatalog.TryGetCue(null, out _));
            Assert.IsFalse(UnitVoiceCatalog.TryResolveFileName("General_Warning11.wav", out _));
        }

        private static Dictionary<string, string> ManagedFolders(string path)
        {
            var fields = new Dictionary<string, string> {
                ["scribeSpeech"] = "scribe", ["aiSpeech"] = "ai", ["inMissionSpeech"] = "inmission",
                ["insultSpeech"] = "insults", ["nameSpeech"] = "names", ["peasantSpeech"] = "peasants",
                ["troopsSpeech"] = "troops", ["tutorialSpeech"] = "tutorial", ["campaignSpeech"] = "campaign"
            };
            var arrays = new Dictionary<string, List<string>>();
            using (var assembly = AssemblyDefinition.ReadAssembly(path))
            {
                var type = assembly.MainModule.Types.Single(value => value.FullName == "SFXManager");
                foreach (var constructor in type.Methods.Where(value => value.IsConstructor && value.HasBody))
                {
                    List<string> values = null;
                    foreach (var instruction in constructor.Body.Instructions)
                    {
                        if (instruction.OpCode == OpCodes.Newarr && instruction.Operand is TypeReference element && element.FullName == "System.String") values = new List<string>();
                        else if (values != null && instruction.OpCode == OpCodes.Ldstr) values.Add((string)instruction.Operand);
                        else if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field)
                        {
                            if (values != null && fields.ContainsKey(field.Name)) arrays[field.Name] = values;
                            values = null;
                        }
                    }
                }
            }
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Match the audited initialization order: later arrays replace duplicate filenames.
            foreach (string name in new[] { "scribeSpeech", "aiSpeech", "inMissionSpeech", "insultSpeech", "nameSpeech", "peasantSpeech", "troopsSpeech", "tutorialSpeech", "campaignSpeech" })
            {
                Assert.IsTrue(arrays.TryGetValue(name, out var names), "Installed speech array missing: " + name);
                foreach (string file in names) result[file] = fields[name];
            }
            return result;
        }

        private static string PointerString(byte[] image, int rva)
        {
            int pe = BitConverter.ToInt32(image, 0x3C);
            ulong imageBase = BitConverter.ToUInt64(image, pe + 48);
            ulong address = BitConverter.ToUInt64(image, Offset(image, rva));
            Assert.IsTrue(address >= imageBase && address - imageBase <= int.MaxValue);
            return ReadString(image, (int)(address - imageBase));
        }
        private static string ReadString(byte[] image, int rva)
        {
            int offset = Offset(image, rva), end = offset;
            while (end < image.Length && image[end] != 0) end++;
            Assert.IsTrue(end < image.Length);
            return System.Text.Encoding.ASCII.GetString(image, offset, end - offset);
        }
        internal static int Offset(byte[] image, int rva)
        {
            int pe = BitConverter.ToInt32(image, 0x3C), count = BitConverter.ToUInt16(image, pe + 6);
            int start = pe + 24 + BitConverter.ToUInt16(image, pe + 20);
            for (int i = 0; i < count; i++)
            {
                int section = start + i * 40, address = BitConverter.ToInt32(image, section + 12);
                int size = BitConverter.ToInt32(image, section + 16), pointer = BitConverter.ToInt32(image, section + 20);
                if (rva >= address && rva < address + size) return pointer + rva - address;
            }
            throw new InvalidOperationException("RVA has no file bytes: 0x" + rva.ToString("X"));
        }
    }
}
