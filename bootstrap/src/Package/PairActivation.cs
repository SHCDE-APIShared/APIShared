using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace APIShared.DependencyBootstrap
{
    // Owns package acceptance only; rejection never removes an already published hook.
    internal sealed class PairActivation
    {
        private readonly PairContract pair;
        private readonly Action verifyOwner;
        private ShadowPlugin selected;
        internal PairActivation(PairContract pair, Action verifyOwner)
        {
            this.pair = pair ?? throw new ArgumentNullException(nameof(pair));
            this.verifyOwner = verifyOwner ?? throw new ArgumentNullException(nameof(verifyOwner));
        }
        internal static PairActivation Prepare()
        {
            var pair = PairContract.Read(BepInEx.Paths.GameRootPath);
            Action verify = () => {

                var own = typeof(PairActivation).Assembly;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                bool probe = assemblies.Any(a => a.GetName().Name == "DependencyBootstrapProbe.Patcher") ||
                    File.Exists(Path.Combine(BepInEx.Paths.PatcherPluginPath, "DependencyBootstrapProbe.Patcher.dll"));
                int instances = assemblies.Count(a => a.GetName().Name == "APIShared.DependencyBootstrap");
                pair.VerifyBootstrap(own.Location, instances, probe);
                using (var diskModule = ModuleDefinition.ReadModule(own.Location))
                    if (diskModule.Mvid != own.ManifestModule.ModuleVersionId)
                        throw new InvalidOperationException("Loaded bootstrap differs from current file");
                // Inspect metadata only. Renamed copies must not evade the ownership gate.
                VerifyPatcherFiles(BepInEx.Paths.PatcherPluginPath, own.Location);

            };
            verify();
            return new PairActivation(pair, verify);
        }
        internal static void VerifyPatcherFiles(string directory, string ownPath)
        {
            foreach (string file in PatcherFiles(directory))
            {
                // Native helper DLLs are normal in patcher packages. Only a valid
                // PE without a CLR directory may bypass managed metadata inspection.
                if (!HasClrDirectory(file))
                {
                    if (Path.GetFileName(file).Equals("APIShared.DependencyBootstrap.dll", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(file).Equals("DependencyBootstrapProbe.Patcher.dll", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid bootstrap file: " + file);
                    continue;
                }
                var name = AssemblyName.GetAssemblyName(file).Name;
                if (name == "DependencyBootstrapProbe.Patcher" ||
                    (name == "APIShared.DependencyBootstrap" && !string.Equals(Path.GetFullPath(file), Path.GetFullPath(ownPath), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Competing bootstrap file: " + file);
            }
        }
        private static bool HasClrDirectory(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new BadImageFormatException(path);
                stream.Position = 0x3c;
                long pe = reader.ReadUInt32();
                if (pe < 64 || pe > stream.Length - 24) throw new BadImageFormatException(path);
                stream.Position = pe;
                if (reader.ReadUInt32() != 0x4550) throw new BadImageFormatException(path);
                stream.Position = pe + 20;
                int optionalSize = reader.ReadUInt16();
                long optional = pe + 24;
                if (optionalSize < 2 || optional + optionalSize > stream.Length) throw new BadImageFormatException(path);
                stream.Position = optional;
                int magic = reader.ReadUInt16();
                int directories = magic == 0x10b ? 96 : magic == 0x20b ? 112 : 0;
                if (directories == 0 || optionalSize < directories) throw new BadImageFormatException(path);
                stream.Position = optional + directories - 4;
                uint count = reader.ReadUInt32();
                if (count > (optionalSize - directories) / 8) throw new BadImageFormatException(path);
                if (count <= 14) return false;
                stream.Position = optional + directories + 14 * 8;
                uint rva = reader.ReadUInt32(), size = reader.ReadUInt32();
                if ((rva == 0) != (size == 0)) throw new BadImageFormatException(path);
                return rva != 0;
            }
        }
        internal bool Bind(IEnumerable<ShadowPlugin> winners)
        {
            selected = null;
            try
            {
                var owners = winners.Where(p => p.Guid == "APIShared_Serp").ToArray();
                if (owners.Length != 1) throw new InvalidDataException("No unique selected owner");
                // Retain identity values, not mutable loader/test metadata.
                selected = new ShadowPlugin { Guid = owners[0].Guid, Version = owners[0].Version, Path = owners[0].Path };
                if (!CanCorrect()) return false;
                return true;
            }
            catch (Exception ex) { Reject(ex); return false; }
        }
        internal bool CanCorrect()
        {
            if (selected == null) return false;
            try
            {
                verifyOwner();
                if (!pair.MatchesSelected(selected.Guid, selected.Version, selected.Path))
                    throw new InvalidDataException("Selected owner or prepared package changed");
                return true;
            }
            catch (Exception ex) { Reject(ex); return false; }
        }
        private void Reject(Exception ex)
        {
            selected = null;
            ProbeTrace.Warning(ex.Message + "; original check retained");
        }
        private static IEnumerable<string> PatcherFiles(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked patcher directory; ownership unclear");
            foreach (string file in Directory.GetFiles(directory, "*.dll"))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked patcher file; ownership unclear");
                yield return file;
            }
            foreach (string child in Directory.GetDirectories(directory))
                foreach (string file in PatcherFiles(child)) yield return file;
        }

    }
}
