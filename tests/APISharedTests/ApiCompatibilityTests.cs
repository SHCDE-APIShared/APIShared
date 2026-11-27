using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared;
using ApiCompatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;

namespace APISharedTests
{
    // Issue #8: accidental changes to the compiled public surface, and to the internals that
    // InternalsVisibleTo consumers bind, fail the local build. Core.Tests checks the
    // game-independent subset in CI; only this suite sees the whole APIShared.dll.
    // docs/API_COMPATIBILITY.md describes intentional changes and consumer rescans.
    [TestClass]
    public sealed class ApiCompatibilityTests
    {
        private const string ScanVariable = "APISHARED_CONSUMER_SCAN";
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private static Assembly Api => typeof(IApiShared).Assembly;
        // The plugin version constant changes with every release by design. Its line stays in the
        // baseline without the value.
        private const string VersionLine = "APIShared.APISharedPlugin | public const string PluginVersion = ";

        [TestMethod]
        public void PublicSurfaceMatchesTheBaseline()
        {
            List<string> actual = ApiSignatures.PublicSurface(Api.GetExportedTypes())
                .Select(line => line.StartsWith(VersionLine, StringComparison.Ordinal) ? VersionLine + "\"<release>\"" : line).ToList();
            string path = ApiBaselineFiles.PathOf(ApiBaselineFiles.PublicSurfaceFile);
            if (ApiBaselineFiles.UpdateRequested)
            {
                ApiBaselineFiles.Write(path, actual);
                return;
            }
            List<string> expected = ApiBaselineFiles.Entries(path);
            var expectedLines = new HashSet<string>(expected, StringComparer.Ordinal);
            var actualLines = new HashSet<string>(actual, StringComparer.Ordinal);
            string removed = ApiBaselineFiles.Difference("Removed or changed: a breaking change unless intended",
                expected.Where(line => !actualLines.Contains(line)));
            string added = ApiBaselineFiles.Difference("Added: record them in the baseline", actual.Where(line => !expectedLines.Contains(line)));
            Assert.IsTrue(removed.Length == 0 && added.Length == 0,
                "The compiled public API differs from tests/ApiBaseline/" + ApiBaselineFiles.PublicSurfaceFile + "." + removed + added);
            Assert.IsTrue(actual.SequenceEqual(expected, StringComparer.Ordinal),
                "The baseline lists the compiled API out of canonical order; regenerate it with " + ApiBaselineFiles.UpdateVariable + "=1.");
            Console.WriteLine("Public API baseline: {0} lines in {1} types.", actual.Count, actual.Select(ApiSignatures.KeyOf).Distinct().Count());
        }

        [TestMethod]
        public void FriendGrantsAndPinFilesNameTheSameConsumers()
        {
            string[] grants = Api.GetCustomAttributes<InternalsVisibleToAttribute>().Select(grant => grant.AssemblyName)
                .Where(name => name != "APISharedTests").ToArray();
            CollectionAssert.AreEquivalent(grants, ApiBaselineFiles.FriendFiles().Keys.ToArray(),
                "Every InternalsVisibleTo consumer needs tests/ApiBaseline/" + ApiBaselineFiles.FriendFilePrefix +
                "<AssemblyName>.txt, and every pin file needs its grant.");
        }

        [TestMethod]
        public void InternalsPinnedForFriendConsumersStillResolve()
        {
            Dictionary<string, bool> identities = ApiSignatures.Identities(Api.GetTypes());
            var failures = new List<string>();
            int pins = 0;
            foreach (KeyValuePair<string, string> file in ApiBaselineFiles.FriendFiles())
                foreach (string pin in ApiBaselineFiles.Entries(file.Value))
                {
                    pins++;
                    bool reachable;
                    if (!identities.TryGetValue(pin, out reachable)) failures.Add(file.Key + ": removed or changed: " + pin);
                    else if (!reachable) failures.Add(file.Key + ": now private: " + pin);
                }
            Assert.AreEqual(0, failures.Count, "Internals that compiled friend consumers bind no longer match:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.ToArray()) + Environment.NewLine +
                "Keep them, or follow the breaking-change steps in docs/API_COMPATIBILITY.md.");
            Console.WriteLine("Friend consumer pins: {0} resolved.", pins);
        }

        // Opt-in: list the internals a compiled friend consumer binds, for adding new builds to
        // the pin files. APISHARED_CONSUMER_SCAN holds consumer assembly paths separated by ';'.
        [TestMethod]
        public void ScannedFriendConsumersBindOnlyPinnedInternals()
        {
            string list = Environment.GetEnvironmentVariable(ScanVariable);
            if (string.IsNullOrEmpty(list))
            {
                Assert.Inconclusive("Set " + ScanVariable + " to friend consumer assembly paths separated by ';' to rescan them.");
                return;
            }
            Dictionary<string, string> files = ApiBaselineFiles.FriendFiles();
            var failures = new List<string>();
            foreach (string path in list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()))
            {
                ConsumerScan scan = Scan(path);
                string file;
                if (!files.TryGetValue(scan.Name, out file))
                {
                    failures.Add(scan.Name + ": no InternalsVisibleTo pin file; add a grant and its pin file first.");
                    continue;
                }
                List<string> pinned = ApiBaselineFiles.Entries(file);
                failures.AddRange(scan.Unresolved.Select(reference => scan.Name + ": already unresolved in this APIShared: " + reference));
                List<string> missing = scan.Internals.Except(pinned, StringComparer.Ordinal).ToList();
                // Union only: other supported builds of the consumer may still need older pins.
                if (ApiBaselineFiles.UpdateRequested) ApiBaselineFiles.Write(file, pinned.Union(missing).OrderBy(pin => pin, StringComparer.Ordinal));
                else failures.AddRange(missing.Select(pin => scan.Name + ": not pinned: " + pin));
                Console.WriteLine("{0} {1}: {2} internal references, {3} not pinned.", scan.Name, scan.Version, scan.Internals.Count, missing.Count);
            }
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.ToArray()));
        }

        private sealed class ConsumerScan
        {
            internal string Name, Version;
            internal readonly SortedSet<string> Internals = new SortedSet<string>(StringComparer.Ordinal);
            internal readonly SortedSet<string> Unresolved = new SortedSet<string>(StringComparer.Ordinal);
        }

        // Reads the consumer's type and member references into APIShared, plus members it
        // implements or overrides by name, which carry no member reference.
        private static ConsumerScan Scan(string path)
        {
            Module api = Api.ManifestModule;
            var scan = new ConsumerScan();
            void AddType(TypeReference reference)
            {
                TypeDefinition definition = reference.Resolve();
                if (definition == null) { scan.Unresolved.Add(reference.FullName); return; }
                Type type = api.ResolveType(definition.MetadataToken.ToInt32());
                if (type.IsVisible) return;
                scan.Internals.Add(ApiSignatures.Identity(type));
                if (type.IsEnum)
                    foreach (FieldInfo value in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                        scan.Internals.Add(ApiSignatures.EnumValueIdentity(value));
            }
            void AddMember(MemberReference reference, IMemberDefinition definition)
            {
                if (definition == null) { scan.Unresolved.Add(reference.FullName); return; }
                MemberInfo member = api.ResolveMember(definition.MetadataToken.ToInt32());
                if (!ApiSignatures.PubliclyVisible(member)) scan.Internals.Add(ApiSignatures.Identity(member));
            }
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Path.GetDirectoryName(Api.Location));
                using (ModuleDefinition consumer = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver }))
                {
                    scan.Name = consumer.Assembly.Name.Name;
                    scan.Version = consumer.Assembly.Name.Version.ToString();
                    foreach (TypeReference reference in consumer.GetTypeReferences())
                        if (FromApi(reference)) AddType(reference);
                    foreach (MemberReference reference in consumer.GetMemberReferences())
                        if (FromApi(reference.DeclaringType) && !(reference.DeclaringType is ArrayType))
                            AddMember(reference, reference.Resolve());
                    foreach (TypeDefinition type in consumer.GetTypes())
                    {
                        foreach (InterfaceImplementation implementation in type.Interfaces)
                        {
                            if (!FromApi(implementation.InterfaceType)) continue;
                            TypeDefinition definition = implementation.InterfaceType.Resolve();
                            Type contract = definition == null ? null : api.ResolveType(definition.MetadataToken.ToInt32());
                            if (contract != null && !contract.IsVisible)
                                foreach (MethodInfo method in contract.GetMethods(Declared)) scan.Internals.Add(ApiSignatures.Identity(method));
                        }
                        for (TypeReference baseType = type.BaseType; baseType != null && FromApi(baseType);)
                        {
                            TypeDefinition definition = baseType.Resolve();
                            if (definition == null) { scan.Unresolved.Add(baseType.FullName); break; }
                            foreach (MethodDefinition method in definition.Methods.Where(method => method.IsVirtual && type.Methods.Any(candidate =>
                                candidate.IsVirtual && !candidate.IsNewSlot && candidate.Name == method.Name && candidate.Parameters.Count == method.Parameters.Count)))
                                AddMember(method, method);
                            baseType = definition.BaseType;
                        }
                    }
                }
            }
            return scan;
        }

        private static bool FromApi(TypeReference reference)
        {
            if (reference == null) return false;
            while (reference.DeclaringType != null) reference = reference.DeclaringType;
            IMetadataScope scope = reference.Scope;
            var module = scope as ModuleDefinition;
            return module != null ? module.Assembly.Name.Name == "APIShared" : scope != null && scope.Name == "APIShared";
        }
    }
}
