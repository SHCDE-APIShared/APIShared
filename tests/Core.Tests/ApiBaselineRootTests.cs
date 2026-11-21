using ApiCompatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

// The option is process-wide. No other baseline reader may observe these temporary
// values, and every scope restores the caller's original process environment.
[TestClass, DoNotParallelize]
public sealed class ApiBaselineRootTests
{
    [TestMethod]
    public void UnsetOverrideKeepsTheExistingAncestorLookup()
    {
        string expected = null;
        for (DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "APIShared.csproj"))) { expected = directory.FullName; break; }
        using var environment = new SourceRootEnvironment(null);
        if (expected == null)
            Assert.ThrowsExactly<DirectoryNotFoundException>(() => ApiBaselineFiles.RepositoryRoot());
        else
            Assert.AreEqual(expected, ApiBaselineFiles.RepositoryRoot());
    }

    [TestMethod]
    public void ExplicitRootReadsTheActualCheckoutAndItsDeclaredBaselines()
    {
        // Use the real caller-selected checkout, never a generated project marker
        // or fabricated baseline that could make an external test appear qualified.
        string repository = ApiBaselineFiles.RepositoryRoot();
        using var environment = new SourceRootEnvironment(repository + Path.DirectorySeparatorChar);
        Assert.AreEqual(repository.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            ApiBaselineFiles.RepositoryRoot().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (string name in new[] { ApiBaselineFiles.PublicSurfaceFile, ApiBaselineFiles.ContentSurfaceFile })
        {
            string expected = Path.Combine(repository, "tests", "ApiBaseline", name);
            Assert.AreEqual(expected, ApiBaselineFiles.PathOf(name));
            CollectionAssert.AreEqual(File.ReadAllBytes(expected), File.ReadAllBytes(ApiBaselineFiles.PathOf(name)));
        }
    }

    [TestMethod]
    [DataRow(" ")]
    [DataRow(".")]
    [DataRow("tests")]
    [DataRow(@"C:relative")]
    [DataRow(@"\root-relative")]
    public void InvalidExplicitSyntaxNeverFallsBackToAnAncestor(string root)
    {
        using var environment = new SourceRootEnvironment(root);
        Assert.ThrowsExactly<ArgumentException>(() => ApiBaselineFiles.RepositoryRoot());
    }

    [TestMethod]
    public void MissingExplicitDirectoryNeverFallsBackToAnAncestor()
    {
        using var environment = new SourceRootEnvironment(Path.Combine(Path.GetTempPath(), "api-missing-source-" + Guid.NewGuid().ToString("N")));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => ApiBaselineFiles.RepositoryRoot());
    }

    [TestMethod]
    [DataRow("APIShared.csproj")]
    [DataRow("PublicApi.txt")]
    [DataRow("Content.PublicApi.txt")]
    public void IncompleteExplicitCheckoutCannotSupplyBaselineData(string missing)
    {
        string repository = ApiBaselineFiles.RepositoryRoot();
        using var files = new OwnedDirectory();
        // Negative fixture only: copy genuine files and omit one required input.
        // This tree must be rejected and is never accepted as a source checkout.
        if (missing != "APIShared.csproj") File.Copy(Path.Combine(repository, "APIShared.csproj"), Path.Combine(files.Path, "APIShared.csproj"));
        string baselines = System.IO.Path.Combine(files.Path, "tests", "ApiBaseline");
        Directory.CreateDirectory(baselines);
        foreach (string name in new[] { ApiBaselineFiles.PublicSurfaceFile, ApiBaselineFiles.ContentSurfaceFile })
            if (name != missing) File.Copy(System.IO.Path.Combine(repository, "tests", "ApiBaseline", name), System.IO.Path.Combine(baselines, name));
        using var environment = new SourceRootEnvironment(files.Path);
        Assert.ThrowsExactly<FileNotFoundException>(() => ApiBaselineFiles.RepositoryRoot());
    }

    [TestMethod]
    public void ExplicitRootCannotAuthorizeBaselineWritesEvenWithTheUpdateFlag()
    {
        string repository = ApiBaselineFiles.RepositoryRoot();
        using var files = new OwnedDirectory();
        string output = Path.Combine(files.Path, "owned-output.txt");
        byte[] original = { 1, 3, 5, 7 };
        File.WriteAllBytes(output, original);
        using var environment = new SourceRootEnvironment(repository, "1");
        Assert.IsTrue(ApiBaselineFiles.UpdateRequested);
        Assert.ThrowsExactly<InvalidOperationException>(() => ApiBaselineFiles.Write(output, new[] { "rejected update" }));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(output));
    }

    [TestMethod]
    public void UnsetOverridePreservesTheExistingOwnedFileUpdateWorkflow()
    {
        using var files = new OwnedDirectory();
        string output = Path.Combine(files.Path, "owned-output.txt");
        using var environment = new SourceRootEnvironment(null, "1");
        ApiBaselineFiles.Write(output, new[] { "owned update" });
        Assert.AreEqual("owned update\r\n", File.ReadAllText(output));
    }

    private sealed class SourceRootEnvironment : IDisposable
    {
        private readonly string root = Environment.GetEnvironmentVariable(ApiBaselineFiles.SourceRootVariable);
        private readonly string update = Environment.GetEnvironmentVariable(ApiBaselineFiles.UpdateVariable);
        internal SourceRootEnvironment(string value, string updateValue = null)
        {
            try
            {
                Environment.SetEnvironmentVariable(ApiBaselineFiles.SourceRootVariable, value, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable(ApiBaselineFiles.UpdateVariable, updateValue, EnvironmentVariableTarget.Process);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            try { Environment.SetEnvironmentVariable(ApiBaselineFiles.SourceRootVariable, root, EnvironmentVariableTarget.Process); }
            finally { Environment.SetEnvironmentVariable(ApiBaselineFiles.UpdateVariable, update, EnvironmentVariableTarget.Process); }
        }
    }

    private sealed class OwnedDirectory : IDisposable
    {
        private readonly string temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        internal string Path { get; }
        internal OwnedDirectory()
        {
            Path = System.IO.Path.Combine(temporaryRoot, "api-baseline-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            string target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(temporaryRoot, StringComparison.Ordinal) || target == temporaryRoot)
                throw new InvalidOperationException("Only this fixture's owned temporary directory can be removed.");
            Directory.Delete(target, true);
        }
    }
}
