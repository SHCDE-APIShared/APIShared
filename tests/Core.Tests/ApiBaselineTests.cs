using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using ApiCompatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace APIShared.Core.Tests;

// Issue #8, game-independent part: the production types Core.Tests compiles from src/ must
// match the checked-in public baseline, and friend pins on those types must still resolve.
// The whole APIShared.dll is compared only by the local APISharedTests run.
[TestClass]
public class ApiBaselineTests
{
    // Portable PDB record that names the source files of types without method bodies.
    private static readonly Guid TypeDefinitionDocuments = new Guid("932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");
    private static readonly Lazy<CompiledProduction> Production = new Lazy<CompiledProduction>(ReadCompiledProduction);

    [TestMethod] public void CompiledProductionTypesMatchThePublicBaseline()
    {
        Dictionary<string, List<string>> baseline = ApiBaselineFiles.Entries(ApiBaselineFiles.PathOf(ApiBaselineFiles.PublicSurfaceFile))
            .GroupBy(ApiSignatures.KeyOf, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var failures = new List<string>();
        int complete = 0, partial = 0;
        foreach (Type type in Production.Value.Types.Where(type => type.IsVisible))
        {
            List<string> actual = ApiSignatures.Surface(type).ToList();
            List<string> expected;
            if (!baseline.TryGetValue(ApiSignatures.KeyOf(actual[0]), out expected))
            {
                failures.Add("Not in the baseline: " + actual[0]);
                continue;
            }
            // Partial parts outside Core.Tests add members and base types here; only lines this
            // part compiles are comparable, and those must all be in the baseline.
            if (Production.Value.Incomplete.Contains(type)) partial++;
            else
            {
                complete++;
                failures.AddRange(expected.Except(actual, StringComparer.Ordinal).Select(line => "Removed or changed: " + line));
            }
            failures.AddRange(actual.Skip(Production.Value.Incomplete.Contains(type) ? 1 : 0).Except(expected, StringComparer.Ordinal)
                .Select(line => "Added: " + line));
        }
        Assert.IsTrue(complete > 0, "No production type was found in the Core.Tests PDB; the comparison would be empty.");
        Assert.AreEqual(0, failures.Count, "Public API of game-independent types differs from tests/ApiBaseline/" +
            ApiBaselineFiles.PublicSurfaceFile + ":" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        Console.WriteLine("Compared {0} of {1} baseline types completely and {2} partial types for additions; the others need the full local build.",
            complete, baseline.Count, partial);
    }

    [TestMethod] public void FriendPinsOnCompiledProductionTypesStillResolve()
    {
        CompiledProduction production = Production.Value;
        Dictionary<string, bool> identities = ApiSignatures.Identities(production.Types);
        var owners = production.Types.ToDictionary(type => ApiSignatures.TypeName(type, true), type => production.Incomplete.Contains(type), StringComparer.Ordinal);
        var failures = new List<string>();
        int checkedPins = 0, pins = 0;
        foreach (KeyValuePair<string, string> file in ApiBaselineFiles.FriendFiles())
            foreach (string pin in ApiBaselineFiles.Entries(file.Value))
            {
                pins++;
                bool incomplete, reachable;
                if (!owners.TryGetValue(ApiSignatures.OwnerOf(pin), out incomplete)) continue;
                bool found = identities.TryGetValue(pin, out reachable);
                if (!found && incomplete) continue;
                checkedPins++;
                if (!found) failures.Add(file.Key + ": removed or changed: " + pin);
                else if (!reachable) failures.Add(file.Key + ": now private: " + pin);
            }
        Assert.IsTrue(checkedPins > 0, "No friend pin belongs to a type compiled by Core.Tests; the check would be empty.");
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        Console.WriteLine("Checked {0} of {1} friend pins; the others need the full local build.", checkedPins, pins);
    }

    [TestMethod] public void FriendGrantsAndPinFilesNameTheSameConsumers()
    {
        string source = File.ReadAllText(Path.Combine(ApiBaselineFiles.RepositoryRoot(), "Properties", "AssemblyInfo.cs"));
        string[] grants = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<AttributeSyntax>()
            .Where(a => a.Name.ToString().Split('.').Last() is "InternalsVisibleTo" or "InternalsVisibleToAttribute")
            .Select(a => ((LiteralExpressionSyntax)a.ArgumentList.Arguments[0].Expression).Token.ValueText)
            .Where(name => name != "APISharedTests").ToArray();
        Assert.IsTrue(grants.Length > 0, "No InternalsVisibleTo grant was parsed from Properties/AssemblyInfo.cs.");
        CollectionAssert.AreEquivalent(grants, ApiBaselineFiles.FriendFiles().Keys.ToArray(),
            "Every InternalsVisibleTo consumer needs a pin file in tests/ApiBaseline, and every pin file its grant.");
    }

    [TestMethod] public void BaselineFilesAreCanonical()
    {
        foreach (string path in Directory.GetFiles(ApiBaselineFiles.Directory(), "*.txt"))
        {
            string text = File.ReadAllText(path);
            Assert.IsFalse(Regex.IsMatch(text, "(?<!\r)\n"), Path.GetFileName(path) + " must use CRLF line endings.");
            List<string> entries = ApiBaselineFiles.Entries(path);
            Assert.AreEqual(entries.Count, entries.Distinct(StringComparer.Ordinal).Count(), Path.GetFileName(path) + " repeats an entry.");
            if (Path.GetFileName(path) != ApiBaselineFiles.PublicSurfaceFile)
                CollectionAssert.AreEqual(entries.OrderBy(entry => entry, StringComparer.Ordinal).ToList(), entries, Path.GetFileName(path) + " is not sorted.");
        }
    }

    private sealed class CompiledProduction
    {
        internal readonly List<Type> Types = new List<Type>();
        internal readonly HashSet<Type> Incomplete = new HashSet<Type>();
    }

    // Types whose every source document is under src/, read from this assembly's portable PDB;
    // test doubles and types extended by test sources are excluded. A type is incomplete when
    // a src/ file that Core.Tests does not compile declares a partial part of the same name.
    private static CompiledProduction ReadCompiledProduction()
    {
        Assembly assembly = typeof(ApiBaselineTests).Assembly;
        string root = ApiBaselineFiles.RepositoryRoot();
        string source = Path.Combine(root, "src") + Path.DirectorySeparatorChar;
        var inSource = new Dictionary<int, bool>();
        var compiledFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (FileStream stream = File.OpenRead(Path.ChangeExtension(assembly.Location, ".pdb")))
        using (MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream))
        {
            MetadataReader pdb = provider.GetMetadataReader();
            void Record(int typeToken, DocumentHandle document)
            {
                // Deterministic CI builds map the repository root to "/_/".
                string recorded = pdb.GetString(pdb.GetDocument(document).Name);
                string name = Path.GetFullPath(recorded.StartsWith("/_/", StringComparison.Ordinal) ? Path.Combine(root, recorded.Substring(3)) : recorded);
                bool production = name.StartsWith(source, StringComparison.OrdinalIgnoreCase);
                if (production) compiledFiles.Add(name);
                inSource[typeToken] = (!inSource.TryGetValue(typeToken, out bool previous) || previous) && production;
            }
            foreach (MethodDebugInformationHandle handle in pdb.MethodDebugInformation)
            {
                MethodDebugInformation information = pdb.GetMethodDebugInformation(handle);
                if (information.SequencePointsBlob.IsNil) continue;
                MethodBase method = assembly.ManifestModule.ResolveMethod(MetadataTokens.GetToken(handle.ToDefinitionHandle()));
                foreach (SequencePoint point in information.GetSequencePoints()) Record(method.DeclaringType.MetadataToken, point.Document);
            }
            foreach (CustomDebugInformationHandle handle in pdb.CustomDebugInformation)
            {
                CustomDebugInformation information = pdb.GetCustomDebugInformation(handle);
                if (information.Parent.Kind != HandleKind.TypeDefinition || pdb.GetGuid(information.Kind) != TypeDefinitionDocuments) continue;
                BlobReader blob = pdb.GetBlobReader(information.Value);
                while (blob.RemainingBytes > 0)
                    Record(MetadataTokens.GetToken(information.Parent), MetadataTokens.DocumentHandle(blob.ReadCompressedInteger()));
            }
        }
        var partialElsewhere = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories).Where(file => !compiledFiles.Contains(Path.GetFullPath(file))))
            foreach (var declaration in CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Modifiers.Any(SyntaxKind.PartialKeyword)))
                partialElsewhere.Add(PartialTypeIdentity(declaration));
        var result = new CompiledProduction();
        foreach (KeyValuePair<int, bool> entry in inSource.Where(entry => entry.Value))
        {
            Type type = assembly.ManifestModule.ResolveType(entry.Key);
            result.Types.Add(type);
            if (partialElsewhere.Contains(type.FullName)) result.Incomplete.Add(type);
        }
        return result;
    }

    internal static string PartialTypeIdentity(TypeDeclarationSyntax declaration)
    {
        var namespaces = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString());
        var types = declaration.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t =>
            t.Identifier.ValueText + (t.TypeParameterList == null ? "" : "`" + t.TypeParameterList.Parameters.Count));
        string prefix = string.Join(".", namespaces);
        return (prefix.Length == 0 ? "" : prefix + ".") + string.Join("+", types);
    }
}
