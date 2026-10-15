namespace System.Runtime.CompilerServices
{
    // Compiler-recognized marker (C# 9) that .NET Framework 4.8.1 does not ship. Methods it covers are
    // emitted without `.locals init`, so the JIT does not zero their whole frame on entry; the desktop
    // JIT does that with `rep stos`, which costs about 8 ns per call on the native broker dispatch path.
    // Use it only where every local is definitely assigned before it is read.
    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Constructor |
        AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event | AttributeTargets.Interface, Inherited = false)]
    internal sealed class SkipLocalsInitAttribute : Attribute
    {
    }
}
