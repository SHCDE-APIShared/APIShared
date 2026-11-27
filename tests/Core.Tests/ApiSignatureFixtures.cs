// Public shapes for ApiSignatureTests. They live in Core.Tests, not src/, so the baseline
// comparison does not treat them as production types.
namespace ApiCompatibility.Fixtures
{
    public enum KindFixture : byte { First = 1, Second = 4 }

    public delegate int HandlerFixture<in TArg>(TArg argument);

    public interface IVarianceFixture<out T> { T Current { get; } }

    public abstract class ShapeFixture<T> : IDisposable where T : class, new()
    {
        public const int Limit = 3;
        public static readonly string Name = "shape";
        protected int count;
        private int stored;
        protected ShapeFixture(int start) { count = start; }
        public event EventHandler Changed { add { } remove { } }
        public int Count { get; protected set; }
        public string this[int index] => index.ToString();
        public abstract bool TryGet(string key, out T value);
        public virtual ref readonly int Peek(in int first, ref int second, params object[] rest) => ref stored;
        public static U Convert<U>(T value, KindFixture kind = KindFixture.Second) where U : struct => default;
        public void Dispose() { }
        internal void Internal() { }
        private void Private() { }
        private sealed class Hidden { }
    }

    public sealed class SealedFixture : ShapeFixture<object>
    {
        public SealedFixture() : base(0) { }
        public override bool TryGet(string key, out object value) { value = key; return true; }
        public sealed override ref readonly int Peek(in int first, ref int second, params object[] rest) => ref first;
    }
}
