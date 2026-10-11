using APIShared.Workshop;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace APISharedTests
{
    [TestClass]
    public class WorkshopOwnershipTests
    {
        [TestMethod]
        public void ProtectsOtherSubscriptionsWithoutInventoriesAndRejectsDuplicateOwners()
        {
            string game = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim().Trim('\uFEFF');
            string extender = Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            ResolveEventHandler resolve = (sender, args) => {
                string path = Path.Combine(extender, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.Load(File.ReadAllBytes(path)) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            string root = Path.Combine(Path.GetTempPath(), "ApiWorkshopOwnership-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string own = Path.Combine(root, "own"), other = Path.Combine(root, "other"), duplicate = Path.Combine(root, "duplicate");
                string ownMap = Create(own, "Pack", "BepInEx/plugins/Pack/Host.dll");
                Create(other, "Other", "BepInEx/plugins/Pack/Infrastructure/APIShared_Serp/APIShared.dll");
                Create(duplicate, "pACK", "BepInEx/plugins/Pack/Other.dll");
                string plugins = Path.Combine(root, "plugins");
                var protectedPaths = WorkshopOwnership.InspectSubscriptions(new[] { own, other }, ownMap, "Pack", plugins);
                Assert.IsTrue(protectedPaths.Contains(Path.Combine(plugins, "Pack", "Infrastructure", "APIShared_Serp", "APIShared.dll")));
                Assert.ThrowsExactly<InvalidDataException>(() => WorkshopOwnership.InspectSubscriptions(new[] { own, duplicate }, ownMap, "Pack", plugins));
                Assert.ThrowsExactly<IOException>(() => WorkshopOwnership.InspectSubscriptions(new[] { own, Path.Combine(root, "unavailable") }, ownMap, "Pack", plugins));
            }
            finally { Directory.Delete(root, true); AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        }

        private static string Create(string directory, string guid, string file)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "package.map");
            using (var output = new ZipOutputStream(File.Create(path)))
            {
                Write(output, "info.json", "{\"GUID\":\"" + guid + "\",\"Name\":\"Test\",\"Version\":\"1.0.0\",\"Manifest\":1}");
                Write(output, file, "payload"); output.Finish();
            }
            return path;
        }

        private static void Write(ZipOutputStream output, string path, string content)
        {
            output.PutNextEntry(new ZipEntry(path)); byte[] bytes = Encoding.UTF8.GetBytes(content);
            output.Write(bytes, 0, bytes.Length); output.CloseEntry();
        }
    }
}
