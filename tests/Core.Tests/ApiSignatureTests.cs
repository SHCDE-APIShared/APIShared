using System.Reflection;
using ApiCompatibility;
using ApiCompatibility.Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace APIShared.Core.Tests;

// The baseline only detects a change that alters a formatted line. These fixtures pin the
// line for each signature element a source or binary consumer depends on.
[TestClass]
public class ApiSignatureTests
{
    [TestMethod] public void SurfaceLinesCarryEverySourceAndBinaryRelevantElement()
    {
        const string Shape = "ApiCompatibility.Fixtures.ShapeFixture<T> | ";
        CollectionAssert.AreEqual(new[]
        {
            Shape + "public abstract class ApiCompatibility.Fixtures.ShapeFixture<T> : System.IDisposable where T : class, new()",
            Shape + "protected .ctor(int start)",
            Shape + "protected int count",
            Shape + "public abstract bool TryGet(string key, out T value)",
            Shape + "public const int Limit = 3",
            Shape + "public event System.EventHandler Changed",
            Shape + "public int Count { get; protected set; }",
            Shape + "public static U Convert<U>(T value, ApiCompatibility.Fixtures.KindFixture kind = (ApiCompatibility.Fixtures.KindFixture)4) where U : struct",
            Shape + "public static readonly string Name",
            Shape + "public string Item[int index] { get; }",
            Shape + "public virtual ref readonly int Peek(in int first, ref int second, params object[] rest)",
            Shape + "public void Dispose()",
        }, ApiSignatures.Surface(typeof(ShapeFixture<>)).ToList());
        CollectionAssert.AreEqual(new[]
        {
            "ApiCompatibility.Fixtures.HandlerFixture<TArg> | public delegate int ApiCompatibility.Fixtures.HandlerFixture<in TArg>(TArg argument)",
            "ApiCompatibility.Fixtures.IVarianceFixture<T> | public interface ApiCompatibility.Fixtures.IVarianceFixture<out T>",
            "ApiCompatibility.Fixtures.IVarianceFixture<T> | public T Current { get; }",
            "ApiCompatibility.Fixtures.KindFixture | public enum ApiCompatibility.Fixtures.KindFixture : byte",
            "ApiCompatibility.Fixtures.KindFixture | First = 1",
            "ApiCompatibility.Fixtures.KindFixture | Second = 4",
            "ApiCompatibility.Fixtures.SealedFixture | public sealed class ApiCompatibility.Fixtures.SealedFixture : ApiCompatibility.Fixtures.ShapeFixture<object>",
            "ApiCompatibility.Fixtures.SealedFixture | public .ctor()",
            "ApiCompatibility.Fixtures.SealedFixture | public override bool TryGet(string key, out object value)",
            "ApiCompatibility.Fixtures.SealedFixture | public sealed override ref readonly int Peek(in int first, ref int second, params object[] rest)",
        }, ApiSignatures.PublicSurface(new[] { typeof(KindFixture), typeof(HandlerFixture<>), typeof(IVarianceFixture<>), typeof(SealedFixture) }));
    }

    [TestMethod] public void IdentitiesKeepTheBoundReferenceAndIgnoreAccessAndNames()
    {
        Type shape = typeof(ShapeFixture<>);
        Assert.AreEqual("class ApiCompatibility.Fixtures.ShapeFixture<!0>", ApiSignatures.Identity(shape));
        Assert.AreEqual("bool ApiCompatibility.Fixtures.ShapeFixture<!0>::TryGet(string, ref !0)", ApiSignatures.Identity(shape.GetMethod("TryGet")));
        Assert.AreEqual("static !!0 ApiCompatibility.Fixtures.ShapeFixture<!0>::Convert`1(!0, ApiCompatibility.Fixtures.KindFixture)",
            ApiSignatures.Identity(shape.GetMethod("Convert")));
        Assert.AreEqual("static string ApiCompatibility.Fixtures.ShapeFixture<!0>::Name", ApiSignatures.Identity(shape.GetField("Name")));
        Assert.AreEqual("ApiCompatibility.Fixtures.KindFixture::Second = 4", ApiSignatures.EnumValueIdentity(typeof(KindFixture).GetField("Second")));
        Assert.AreEqual("ApiCompatibility.Fixtures.ShapeFixture<!0>", ApiSignatures.OwnerOf("static !!0 ApiCompatibility.Fixtures.ShapeFixture<!0>::Convert`1(!0, int)"));
        Assert.AreEqual("ApiCompatibility.Fixtures.KindFixture", ApiSignatures.OwnerOf("enum ApiCompatibility.Fixtures.KindFixture"));

        Dictionary<string, bool> identities = ApiSignatures.Identities(new[] { shape, shape.GetNestedType("Hidden", BindingFlags.NonPublic) });
        Assert.IsTrue(identities["void ApiCompatibility.Fixtures.ShapeFixture<!0>::Internal()"], "Friend assemblies reach internal members.");
        Assert.IsFalse(identities["void ApiCompatibility.Fixtures.ShapeFixture<!0>::Private()"]);
        Assert.IsFalse(identities["class ApiCompatibility.Fixtures.ShapeFixture<!0>.Hidden"], "Private nested types are unreachable.");
        Assert.IsFalse(ApiSignatures.PubliclyVisible(shape.GetMethod("Internal", BindingFlags.Instance | BindingFlags.NonPublic)));
    }
}
