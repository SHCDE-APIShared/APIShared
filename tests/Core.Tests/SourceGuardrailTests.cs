using APIShared.SourceContracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace APIShared.Core.Tests;

// These fixtures execute the production checker, not a second implementation.
[TestClass]
public sealed class SourceGuardrailTests
{
    const string WheatSupport = """
        class FixesWheatStartup {
            static void Initialize(object log) { Interlocked.Exchange(ref attempted, 1); Monitor.TryEnter(gate, 0, ref taken); FixesWheatMemory.Read(); }
        }
        class FixesWheatMemory { }
        """;
    static SourceModel Wheat(string body, string extra = "", string dependency = "SoftDependency") => Model("""
        [BepInDependency("fixes", BepInDependency.DependencyFlags.DEPENDENCY)]
        partial class APISharedPlugin {
            private void Awake() { BODY }
            EXTRA
        }
        """.Replace("DEPENDENCY", dependency).Replace("BODY", body).Replace("EXTRA", extra) + WheatSupport);
    static SourceModel Model(string text) => new(new[] { KeyValuePair.Create("fixture.cs", text) });

    [TestMethod]
    [DataRow("FixesWheatStartup.Initialize(Logger); runtime.InitializeManaged(Logger);")]
    [DataRow("Debug.InitializeDiagnostics(Config, Logger); FixesWheatStartup . Initialize ( RuntimeLogger ); runtime.InitializeManaged(RuntimeLogger);")]
    [DataRow("/* FixesWheatStartup.Initialize(fake); */ FixesWheatStartup.Initialize(Forward(Logger)); runtime.InitializeManaged(Logger);")]
    public void HarmlessPreparationAndLoggerForwardingPass(string body) => Wheat(body).CheckWheat();

    [TestMethod]
    public void HelperAndPartialFileReorganizationPass()
    {
        var original = Wheat("BeforeApi(); runtime.InitializeManaged(Logger);", "void BeforeApi() { FixesWheatStartup.Initialize(Logger); }");
        var text = original.Roots.Single().ToFullString();
        var helper = "void BeforeApi() { FixesWheatStartup.Initialize(Logger); }";
        new SourceModel(new[] {
            KeyValuePair.Create("entry.cs", text.Replace(helper, "")),
            KeyValuePair.Create("renamed-helper.cs", "partial class APISharedPlugin { " + helper + " }")
        }).CheckWheat();
    }

    [TestMethod]
    [DataRow("runtime.InitializeManaged(Logger); FixesWheatStartup.Initialize(Logger);")]
    [DataRow("runtime.InitializeManaged(Logger);")]
    [DataRow("FixesWheatStartup.Initialize(Logger); FixesWheatStartup.Initialize(Logger);")]
    [DataRow("if (enabled) FixesWheatStartup.Initialize(Logger); runtime.InitializeManaged(Logger);")]
    [DataRow("if (enabled) return; FixesWheatStartup.Initialize(Logger);")]
    [DataRow("LibraryLoaded += () => FixesWheatStartup.Initialize(Logger);")]
    public void MissingLateConditionalRepeatedOrDeferredAttemptsFail(string body) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => Wheat(body).CheckWheat());

    [TestMethod]
    public void HardDependencyAndLaterCallerFail()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Wheat("FixesWheatStartup.Initialize(Logger);", dependency: "HardDependency").CheckWheat());
        Assert.ThrowsExactly<InvalidOperationException>(() => Wheat("FixesWheatStartup.Initialize(Logger);", "void Later() { FixesWheatStartup.Initialize(Logger); }").CheckWheat());
        Assert.ThrowsExactly<InvalidOperationException>(() => Wheat("FixesWheatStartup.Initialize(Logger);", "void Later() { FixesWheatMemory.Read(); }").CheckWheat());
    }

    const string Sound = """
        class NativeDetour { }
        class SoundRuntime {
            NativeDetour arbitraryName;
            bool publicationStarted;
            void Install() { SEQUENCE }
            void RollbackUnpublished() { GUARD arbitraryName?.Dispose(); }
        }
        """;
    [TestMethod]
    public void AudioCountsFormattingAndNeighborMethodsAreNotContracts()
    {
        Model(Sound.Replace("SEQUENCE", "publicationStarted = true; arbitraryName.Enable(); SoundContextPolicy.SetReady(true);")
            .Replace("GUARD", "if ( publicationStarted ) { throw new InvalidOperationException(); } /* padding is immaterial */" )).CheckHooks();
        Model(Sound.Replace("SEQUENCE", "publicationStarted = true; arbitraryName.Enable(); SoundContextPolicy.SetReady(true);")
            .Replace("GUARD", "if (publicationStarted) throw new InvalidOperationException();")
            .Replace("publicationStarted", "renamedBarrier").Replace("RollbackUnpublished", "ReleaseCandidate")
            .Replace("if (renamedBarrier)", "if ((renamedBarrier))")).CheckHooks();
    }
    [TestMethod]
    [DataRow("", "publicationStarted = true; arbitraryName.Enable();")]
    [DataRow("if (publicationStarted) throw new InvalidOperationException();", "arbitraryName.Enable(); publicationStarted = true;")]
    [DataRow("if (publicationStarted) throw new InvalidOperationException();", "publicationStarted = true; SoundContextPolicy.SetReady(true); arbitraryName.Enable();")]
    public void UnsafeAudioRollbackAndPublicationFail(string guard, string sequence) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => Model(Sound.Replace("SEQUENCE", sequence).Replace("GUARD", guard)).CheckHooks());

    [TestMethod]
    public void PublishedTeardownAndExecutableWriterFail()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Model("class SoundRuntime { NativeDetour renamed; void OnDestroy() { renamed.Dispose(); } }").CheckHooks());
        Assert.ThrowsExactly<InvalidOperationException>(() => Model("class Other { void Write() { CodePatch.Write(pointer, bytes); } }").CheckHooks());
        var bootstrap = new SourceModel(new[] { KeyValuePair.Create("moved.cs", "class RenamedBootstrap { ILHook arbitrary; void Later() { arbitrary.Apply(); } }") }, new[] { "moved.cs" });
        Assert.ThrowsExactly<InvalidOperationException>(() => bootstrap.CheckHooks());
    }

    [TestMethod]
    public void PreparedPlagueCodeCannotBeReprotectedOrFreedAfterPublication()
    {
        // The audited private allocation may become executable only in its constructor.
        Model("class PlagueNativeCode { bool published; PlagueNativeCode() { VirtualProtect(p,n,x,out old); FlushInstructionCache(p,n,x); } void Rollback() { if (published) throw new Exception(); VirtualFree(p,n,x); } }").CheckHooks();
        Assert.ThrowsExactly<InvalidOperationException>(() => Model("class PlagueNativeCode { void Later() { VirtualProtect(p,n,x,out old); } }").CheckHooks());
        Assert.ThrowsExactly<InvalidOperationException>(() => Model("class PlagueNativeCode { void Later() { VirtualFree(p,n,x); } }").CheckHooks());
    }

    [TestMethod]
    public void LifecycleChecksDeclarationsRatherThanCommentsOrFileNeighbors()
    {
        Model("class Service { void Update() {} } class Plugin : BaseUnityPlugin { void Awake() { Log(\"Update() StartCoroutine()\"); } }").CheckLifecycle();
        Assert.ThrowsExactly<InvalidOperationException>(() => Model("class Plugin : BaseUnityPlugin { void Update() {} }").CheckLifecycle());
        Assert.ThrowsExactly<InvalidOperationException>(() => Model("class Plugin : BaseUnityPlugin { void OnDestroy() => service.Dispose(); }").CheckLifecycle());
        Assert.ThrowsExactly<InvalidOperationException>(() => Model("class Plugin { void Later() { StartCoroutine(task); } }").CheckLifecycle());
        Model("class Service { void Log() { Print(\"Newtonsoft.Json is forbidden here\"); } }").CheckLifecycle();
        Assert.ThrowsExactly<InvalidOperationException>(() => Model("class Service { void Load() { JsonUtility.FromJson(text); } }").CheckLifecycle());
    }

    [TestMethod]
    public void DependencyMetadataIgnoresFormattingAndResolvesConstantsInTheirOwner()
    {
        var parsed = SourceModel.DependencyDeclarations("""
            // [BepInDependency("comment", "9.9.9")]
            [BepInEx.BepInDependency ( Guid, Min )]
            class First { const string Guid = "first"; const string Min = "1.2.3"; }
            [BepInDependency(Guid, Min)]
            class Second { const string Guid = "second"; const string Min = "2.0.0"; }
            """);
        CollectionAssert.AreEquivalent(new[] { "first:1.2.3", "second:2.0.0" }, parsed.Select(d => d.GUID + ":" + d.MinimumVersion).ToArray());
    }

    [TestMethod]
    public void PartialTypeClassificationIncludesNamespaceNestingAndArity()
    {
        var root = CSharpSyntaxTree.ParseText("namespace One { partial class Same<T> { partial class Nested {} } } namespace Two { partial class Same {} }").GetRoot();
        CollectionAssert.AreEquivalent(new[] { "One.Same`1", "One.Same`1+Nested", "Two.Same" },
            root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(ApiBaselineTests.PartialTypeIdentity).ToArray());
    }

    [TestMethod]
    public void EvaluatedBuildSymbolsSelectTheActualConditionalRuntimeCode()
    {
        var source = new[] { KeyValuePair.Create("conditional.cs", "#if FEATURE\nclass Service { void Load() { JsonUtility.FromJson(text); } }\n#endif") };
        new SourceModel(source).CheckLifecycle();
        var selected = new Dictionary<string, string[]> { ["conditional.cs"] = new[] { "FEATURE" } };
        Assert.ThrowsExactly<InvalidOperationException>(() => new SourceModel(source, definesByPath: selected).CheckLifecycle());
        string dependency = "#if FEATURE\n[BepInDependency(\"selected.api\", \"1.0.0\")] class Plugin {}\n#endif";
        Assert.AreEqual(0, SourceModel.DependencyDeclarations(dependency).Count);
        Assert.AreEqual("selected.api", SourceModel.DependencyDeclarations(dependency, new[] { "FEATURE" }).Single().GUID);
    }

    [TestMethod]
    public void ActualMemoryBoundarySurvivesFormattingAndLocalRenamesButRejectsBrokenProtection()
    {
        var source = SourceModel.Read(ApiCompatibility.ApiBaselineFiles.RepositoryRoot()).Type("FixesWheatMemory");
        var identifiers = source.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) &&
            t.ValueText is "pointer" or "restored" or "flushed" or "observed" or "old").ToArray();
        var renamed = source.ReplaceTokens(identifiers, (token, _) => SyntaxFactory.Identifier("renamed_" + token.ValueText).WithTriviaFrom(token));
        Model(renamed.NormalizeWhitespace().ToFullString()).CheckWheatMemory();
        foreach (string operation in new[] { "FlushInstructionCache", "VirtualProtect" })
        {
            var call = source.DescendantNodes().OfType<FinallyClauseSyntax>().SelectMany(SourceModel.Calls).First(c => SourceModel.Name(c) == operation);
            var broken = source.ReplaceNode(call, SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression));
            Assert.ThrowsExactly<InvalidOperationException>(() => Model(broken.ToFullString()).CheckWheatMemory());
        }
        var write = SourceModel.Calls(source).Single(c => SourceModel.Name(c) == "WriteByte");
        Assert.ThrowsExactly<InvalidOperationException>(() => Model(source.ReplaceNode(write,
            write.WithExpression(SyntaxFactory.ParseExpression("Marshal.WriteInt32"))).ToFullString()).CheckWheatMemory());
    }
}
