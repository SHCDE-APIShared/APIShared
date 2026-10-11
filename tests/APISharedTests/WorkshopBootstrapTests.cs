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
    public sealed class WorkshopBootstrapTests
    {
        [TestMethod]
        public void ActualBootstrapArchiveMustContainOnlyItsOwnFolder()
        {
            string game = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim().Trim('\uFEFF');
            string extender = Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            ResolveEventHandler resolve = (sender, args) => {
                string path = Path.Combine(extender, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.Load(File.ReadAllBytes(path)) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            string root = Path.Combine(Path.GetTempPath(), "ApiBootstrap-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                var package = new WorkshopPackage { Bootstrap = new WorkshopBootstrap("Retained.map", "ExampleBootstrap", "2.0.0", "BepInEx/plugins/ExampleBootstrap") };
                Create(root, null); WorkshopBootstrapValidation.Verify(package, root);
                foreach (string illegal in new[] { "BepInEx/plugins/ExampleHost/Host.dll", "BepInEx/config/bootstrap.cfg", "BepInEx/plugins/ExampleBootstrap/../escape.dll", "Other.map" }) {
                    Create(root, illegal);
                    Assert.ThrowsExactly<InvalidDataException>(() => WorkshopBootstrapValidation.Verify(package, root), illegal);
                }
            } finally { Directory.Delete(root, true); AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        }
        private static void Create(string root, string extra)
        {
            using (var zip = new ZipOutputStream(File.Create(Path.Combine(root, "Retained.map")))) {
                const string info = "{\"GUID\":\"ExampleBootstrap\",\"Name\":\"Example\",\"Version\":\"2.0.0\",\"Manifest\":1}";
                Write(zip, "info.json", info); Write(zip, "BepInEx/plugins/ExampleBootstrap/info.json", info); Write(zip, "BepInEx/plugins/ExampleBootstrap/ExampleBootstrap.dll", "payload");
                if (extra != null) Write(zip, extra, "undeclared"); zip.Finish();
            }
        }
        private static void Write(ZipOutputStream output, string name, string text)
        {
            output.PutNextEntry(new ZipEntry(name)); byte[] bytes = Encoding.UTF8.GetBytes(text); output.Write(bytes, 0, bytes.Length); output.CloseEntry();
        }
    }
}
