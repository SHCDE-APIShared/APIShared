namespace System.Runtime.CompilerServices
{
    // .NET Framework 4.8.1 lacks this attribute; the compiler recognizes it by name. NativeInlineStubGroup
    // forwards the caller's handle expression as RedBird hook name, as a direct AddInline call does.
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;
        public string ParameterName { get; }
    }
}
