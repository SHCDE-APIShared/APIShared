using System;
using System.IO;
using System.Reflection;
using APIShared.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;

namespace APISharedTests
{
    [TestClass]
    public sealed class InstalledAssemblyIdentityTests
    {
        private static string Game => Environment.GetEnvironmentVariable("SHCDE_GAME_DIR") ??
            File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim();
        private const string ManagedHash = "BC8B6A395F01D48557DB413600C8DD8D1FDFD3ABDF97BFBBB68A3C56B04FD789";

        [TestMethod]
        public void ByteLoadedGameAssemblyIsValidatedWithoutLocation()
        {
            string path = InstalledAssemblyIdentity.GameAssemblyPath(Game);
            Assembly loaded = Assembly.Load(File.ReadAllBytes(path));
            Assert.AreEqual(string.Empty, loaded.Location);
            InstalledAssemblyIdentity.Verify(loaded, path, ManagedHash);
            Assert.ThrowsExactly<ArgumentException>(() => InstalledAssemblyIdentity.GameAssemblyPath(" "));
            Assert.ThrowsExactly<FileNotFoundException>(() => InstalledAssemblyIdentity.Verify(loaded, path + ".missing", ManagedHash));
            Assert.ThrowsExactly<InvalidOperationException>(() => InstalledAssemblyIdentity.Verify(loaded, path, new string('0', 64)));
            Assert.ThrowsExactly<ArgumentException>(() => InstalledAssemblyIdentity.Verify(loaded, path, null));
        }

        [TestMethod]
        public void ByteLoadedUnityCoreAssemblyUsesInstalledImage()
        {
            string path = InstalledAssemblyIdentity.UnityCoreAssemblyPath(Game);
            Assembly loaded = Assembly.Load(File.ReadAllBytes(path));
            Assert.AreEqual(string.Empty, loaded.Location);
            InstalledAssemblyIdentity.Verify(loaded, path,
                "E468E19789D608B41E2C7FE4F367C7082FECE7D1883F568E75870F6C5384CC86");
        }

        [TestMethod]
        public void MatchingFileHashCannotAuthorizeDifferentLoadedModule()
        {
            string path = InstalledAssemblyIdentity.GameAssemblyPath(Game);
            Assembly loaded = Assembly.Load(File.ReadAllBytes(path));
            string altered = Path.GetTempFileName();
            try
            {
                using (var module = AssemblyDefinition.ReadAssembly(path))
                {
                    module.MainModule.Mvid = Guid.NewGuid();
                    module.Write(altered);
                }
                string allowedHash = APIShared.Audio.NativeContract.HashFile(altered);
                var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    InstalledAssemblyIdentity.Verify(loaded, altered, allowedHash));
                StringAssert.Contains(error.Message, "Loaded/disk module identity differs");
                Assert.ThrowsExactly<InvalidOperationException>(() => InstalledAssemblyIdentity.VerifyModule(loaded, altered));
                File.WriteAllText(altered, "changed binary");
                Assert.ThrowsExactly<InvalidOperationException>(() => InstalledAssemblyIdentity.Verify(loaded, altered, ManagedHash));
            }
            finally { File.Delete(altered); }
        }

        [TestMethod]
        public void ManagedModuleProvenanceDoesNotImposeVersionCeilings()
        {
            foreach (var version in new[] { new Version(1, 0, 0, 0), new Version(99, 0, 0, 0), new Version(2026, 10, 17, 0) })
            {
                string path = Path.GetTempFileName();
                try
                {
                    string name = "OptionalParticipant" + Guid.NewGuid().ToString("N");
                    using (var image = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, version), name, ModuleKind.Dll))
                        image.Write(path);
                    Assembly loaded = Assembly.Load(File.ReadAllBytes(path));
                    InstalledAssemblyIdentity.VerifyModule(loaded, path);
                }
                finally { File.Delete(path); }
            }
        }
    }
}
