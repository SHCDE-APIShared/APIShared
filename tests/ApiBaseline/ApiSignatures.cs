using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ApiCompatibility
{
    // Stable text form of the compatibility-relevant shape of compiled types. The full local
    // run formats APIShared.dll; the game-independent CI run formats the production types that
    // Core.Tests compiles. Both compare against the checked-in files beside this source.
    // Surface lines carry what source and binary consumers depend on: kinds, modifiers, base
    // types, signatures, parameter names, defaults and constant values. Identity lines carry
    // only the member reference a compiled consumer binds at JIT time, independent of access.
    internal static class ApiSignatures
    {
        internal const string Separator = " | ";
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<Type, string> Aliases = new Dictionary<Type, string>
        {
            { typeof(void), "void" }, { typeof(bool), "bool" }, { typeof(byte), "byte" }, { typeof(sbyte), "sbyte" },
            { typeof(char), "char" }, { typeof(short), "short" }, { typeof(ushort), "ushort" }, { typeof(int), "int" },
            { typeof(uint), "uint" }, { typeof(long), "long" }, { typeof(ulong), "ulong" }, { typeof(float), "float" },
            { typeof(double), "double" }, { typeof(decimal), "decimal" }, { typeof(string), "string" }, { typeof(object), "object" }
        };

        // Public and protected members of the given externally visible types, grouped by type:
        // the declaration line first, then members in ordinal order.
        internal static List<string> PublicSurface(IEnumerable<Type> types)
        {
            var lines = new List<string>();
            foreach (Type type in types.Where(type => type.IsVisible).OrderBy(type => TypeName(type), StringComparer.Ordinal))
                lines.AddRange(Surface(type));
            return lines;
        }

        internal static string KeyOf(string line)
        {
            int separator = line.IndexOf(Separator, StringComparison.Ordinal);
            return separator < 0 ? line : line.Substring(0, separator);
        }

        internal static IEnumerable<string> Surface(Type type)
        {
            string key = TypeName(type);
            yield return key + Separator + Declaration(type);
            if (IsDelegate(type)) yield break;
            var members = new List<string>();
            if (type.IsEnum)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                    members.Add(field.Name + " = " + Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture));
            }
            else
            {
                var accessors = new HashSet<int>();
                foreach (PropertyInfo property in type.GetProperties(Declared))
                {
                    foreach (MethodInfo accessor in property.GetAccessors(true)) accessors.Add(accessor.MetadataToken);
                    string line = Property(property);
                    if (line != null) members.Add(line);
                }
                foreach (EventInfo item in type.GetEvents(Declared))
                {
                    foreach (MethodInfo accessor in new[] { item.GetAddMethod(true), item.GetRemoveMethod(true), item.GetRaiseMethod(true) })
                        if (accessor != null) accessors.Add(accessor.MetadataToken);
                    MethodInfo add = item.GetAddMethod(true);
                    if (Visible(add)) members.Add(Access(add) + Modifiers(add) + " event " + TypeName(item.EventHandlerType) + " " + item.Name);
                }
                foreach (FieldInfo field in type.GetFields(Declared))
                    if (Visible(field)) members.Add(Field(field));
                foreach (ConstructorInfo constructor in type.GetConstructors(Declared))
                    if (Visible(constructor)) members.Add(Method(constructor));
                foreach (MethodInfo method in type.GetMethods(Declared))
                    if (Visible(method) && !accessors.Contains(method.MetadataToken)) members.Add(Method(method));
            }
            members.Sort(StringComparer.Ordinal);
            foreach (string member in members) yield return key + Separator + member;
        }

        // Binary identities of every type and member, mapped to whether an InternalsVisibleTo
        // consumer can still reach it. Property and event accessors appear as methods because
        // compiled consumers reference the accessor. Enum values are pinned as constants because
        // consumers compile them into their own IL.
        internal static Dictionary<string, bool> Identities(IEnumerable<Type> types)
        {
            var identities = new Dictionary<string, bool>(StringComparer.Ordinal);
            void Add(string identity, bool reachable)
            {
                bool existing;
                identities[identity] = identities.TryGetValue(identity, out existing) ? existing || reachable : reachable;
            }
            foreach (Type type in types)
            {
                bool typeReachable = FriendReachable(type);
                Add(Identity(type), typeReachable);
                if (type.IsEnum)
                    foreach (FieldInfo value in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                        Add(EnumValueIdentity(value), typeReachable);
                foreach (FieldInfo field in type.GetFields(Declared)) Add(Identity(field), typeReachable && !field.IsPrivate);
                foreach (ConstructorInfo constructor in type.GetConstructors(Declared)) Add(Identity(constructor), typeReachable && !constructor.IsPrivate);
                foreach (MethodInfo method in type.GetMethods(Declared)) Add(Identity(method), typeReachable && !method.IsPrivate);
            }
            return identities;
        }

        internal static string Identity(Type type) => Kind(type) + " " + TypeName(type, true);

        internal static string EnumValueIdentity(FieldInfo value) =>
            TypeName(value.DeclaringType, true) + "::" + value.Name + " = " +
            Convert.ToString(value.GetRawConstantValue(), CultureInfo.InvariantCulture);

        internal static string Identity(MemberInfo member)
        {
            var field = member as FieldInfo;
            if (field != null)
                return (field.IsStatic ? "static " : string.Empty) + TypeName(field.FieldType, true) + " " +
                    TypeName(field.DeclaringType, true) + "::" + field.Name;
            var method = (MethodBase)member;
            var info = method as MethodInfo;
            return (method.IsStatic ? "static " : string.Empty) + (info == null ? "void" : TypeName(info.ReturnType, true)) + " " +
                TypeName(method.DeclaringType, true) + "::" + method.Name +
                (method.IsGenericMethodDefinition ? "`" + method.GetGenericArguments().Length.ToString(CultureInfo.InvariantCulture) : string.Empty) +
                "(" + string.Join(", ", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType, true)).ToArray()) + ")";
        }

        // Declaring type of an identity: the type itself for a type identity, otherwise the
        // binary type name before "::".
        internal static string OwnerOf(string identity)
        {
            int members = identity.IndexOf("::", StringComparison.Ordinal);
            if (members < 0) return identity.Substring(identity.IndexOf(' ') + 1);
            int depth = 0, start = 0;
            for (int index = members - 1; index >= 0; index--)
            {
                char character = identity[index];
                if (character == '>') depth++;
                else if (character == '<') depth--;
                else if (character == ' ' && depth == 0) { start = index + 1; break; }
            }
            return identity.Substring(start, members - start);
        }

        // Friend assemblies reach internal and protected members; private members and members
        // of private nested types stay unreachable.
        internal static bool FriendReachable(Type type)
        {
            for (Type current = type; current != null; current = current.DeclaringType)
                if (current.IsNestedPrivate) return false;
            return true;
        }

        internal static bool PubliclyVisible(MemberInfo member)
        {
            if (!member.DeclaringType.IsVisible) return false;
            var field = member as FieldInfo;
            return field != null ? Visible(field) : Visible((MethodBase)member);
        }

        internal static string TypeName(Type type, bool binary = false)
        {
            if (type.IsByRef) return "ref " + TypeName(type.GetElementType(), binary);
            if (type.IsPointer) return TypeName(type.GetElementType(), binary) + "*";
            if (type.IsArray) return TypeName(type.GetElementType(), binary) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsGenericParameter)
                return binary ? (type.DeclaringMethod != null ? "!!" : "!") + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture) : type.Name;
            string alias;
            if (Aliases.TryGetValue(type, out alias)) return alias;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return TypeName(type.GetGenericArguments()[0], binary) + "?";
            return Qualified(type, type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes, binary, false);
        }

        private static string Qualified(Type type, Type[] arguments, bool binary, bool declaration)
        {
            // Nested types repeat the arguments of their declaring types first.
            string name = type.Name;
            int tick = name.IndexOf('`');
            int own = tick < 0 ? 0 : int.Parse(name.Substring(tick + 1), CultureInfo.InvariantCulture);
            if (tick >= 0) name = name.Substring(0, tick);
            int outer = arguments.Length - own;
            string prefix = type.IsNested
                ? Qualified(type.DeclaringType, arguments.Take(outer).ToArray(), binary, declaration) + "."
                : string.IsNullOrEmpty(type.Namespace) ? string.Empty : type.Namespace + ".";
            if (own == 0) return prefix + name;
            return prefix + name + "<" + string.Join(", ", arguments.Skip(outer).Select(argument =>
                (declaration ? Variance(argument) : string.Empty) + TypeName(argument, binary)).ToArray()) + ">";
        }

        private static string Variance(Type parameter)
        {
            if (!parameter.IsGenericParameter) return string.Empty;
            GenericParameterAttributes variance = parameter.GenericParameterAttributes & GenericParameterAttributes.VarianceMask;
            return variance == GenericParameterAttributes.Covariant ? "out " : variance == GenericParameterAttributes.Contravariant ? "in " : string.Empty;
        }

        private static string Kind(Type type) =>
            IsDelegate(type) ? "delegate" : type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";

        private static bool IsDelegate(Type type) => type.BaseType == typeof(MulticastDelegate);

        private static string Declaration(Type type)
        {
            string access = type.IsPublic || type.IsNestedPublic ? "public" : "protected";
            string name = Qualified(type, type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes, false, true);
            Type[] parameters = type.IsGenericTypeDefinition ? type.GetGenericArguments() : Type.EmptyTypes;
            if (IsDelegate(type))
            {
                MethodInfo invoke = type.GetMethod("Invoke");
                return access + " delegate " + ReturnType(invoke) + " " + name + "(" + Parameters(invoke) + ")" + Constraints(parameters);
            }
            if (type.IsEnum) return access + " enum " + name + " : " + TypeName(Enum.GetUnderlyingType(type));
            string modifiers = type.IsInterface || type.IsValueType ? string.Empty
                : type.IsAbstract && type.IsSealed ? " static" : type.IsAbstract ? " abstract" : type.IsSealed ? " sealed" : string.Empty;
            var bases = new List<string>();
            if (!type.IsInterface && !type.IsValueType && type.BaseType != typeof(object)) bases.Add(TypeName(type.BaseType));
            // Interfaces a framework base class adds differ between runtimes; list only the type's own.
            Type[] inherited = type.BaseType == null ? Type.EmptyTypes : type.BaseType.GetInterfaces();
            bases.AddRange(type.GetInterfaces().Except(inherited).Select(item => TypeName(item)).OrderBy(item => item, StringComparer.Ordinal));
            return access + modifiers + " " + Kind(type) + " " + name + (bases.Count > 0 ? " : " + string.Join(", ", bases.ToArray()) : string.Empty) +
                Constraints(parameters);
        }

        private static string Constraints(Type[] parameters)
        {
            var text = new StringBuilder();
            foreach (Type parameter in parameters)
            {
                GenericParameterAttributes attributes = parameter.GenericParameterAttributes;
                var items = new List<string>();
                if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0) items.Add("class");
                bool valueType = (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
                if (valueType) items.Add("struct");
                items.AddRange(parameter.GetGenericParameterConstraints().Where(item => item != typeof(ValueType))
                    .Select(item => TypeName(item)).OrderBy(item => item, StringComparer.Ordinal));
                if (!valueType && (attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0) items.Add("new()");
                if (items.Count > 0) text.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", items.ToArray()));
            }
            return text.ToString();
        }

        private static string Property(PropertyInfo property)
        {
            MethodInfo getter = property.GetGetMethod(true), setter = property.GetSetMethod(true);
            if (!Visible(getter)) getter = null;
            if (!Visible(setter)) setter = null;
            if (getter == null && setter == null) return null;
            MethodInfo primary = getter != null && (getter.IsPublic || setter == null || !setter.IsPublic) ? getter : setter;
            string access = Access(primary);
            var text = new StringBuilder(access).Append(Modifiers(primary)).Append(' ')
                .Append(TypeName(property.PropertyType)).Append(' ').Append(property.Name);
            ParameterInfo[] index = property.GetIndexParameters();
            if (index.Length > 0) text.Append('[').Append(string.Join(", ", index.Select(Parameter).ToArray())).Append(']');
            text.Append(" {");
            if (getter != null) text.Append(Access(getter) == access ? string.Empty : " " + Access(getter)).Append(" get;");
            if (setter != null)
            {
                bool init = setter.ReturnParameter.GetRequiredCustomModifiers().Any(modifier =>
                    modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                text.Append(Access(setter) == access ? string.Empty : " " + Access(setter)).Append(init ? " init;" : " set;");
            }
            return text.Append(" }").ToString();
        }

        private static string Field(FieldInfo field)
        {
            string access = field.IsPublic ? "public" : "protected";
            if (field.IsLiteral)
                return access + " const " + TypeName(field.FieldType) + " " + field.Name + " = " + Literal(field.GetRawConstantValue(), field.FieldType);
            return access + (field.IsStatic ? " static" : string.Empty) + (field.IsInitOnly ? " readonly" : string.Empty) + " " +
                TypeName(field.FieldType) + " " + field.Name;
        }

        private static string Method(MethodBase method)
        {
            var text = new StringBuilder(Access(method)).Append(Modifiers(method));
            var info = method as MethodInfo;
            if (info != null) text.Append(' ').Append(ReturnType(info));
            text.Append(' ').Append(method.Name);
            Type[] generic = method.IsGenericMethodDefinition ? method.GetGenericArguments() : Type.EmptyTypes;
            if (generic.Length > 0) text.Append('<').Append(string.Join(", ", generic.Select(item => item.Name).ToArray())).Append('>');
            return text.Append('(').Append(Parameters(method)).Append(')').Append(Constraints(generic)).ToString();
        }

        private static string Modifiers(MethodBase method)
        {
            if (method.IsStatic) return " static";
            if (method.DeclaringType.IsInterface) return string.Empty;
            if (method.IsAbstract) return " abstract";
            // C# emits non-virtual implicit interface implementations as final newslot virtuals.
            bool newSlot = (method.Attributes & MethodAttributes.VtableLayoutMask) == MethodAttributes.NewSlot;
            if (!method.IsVirtual || method.IsFinal && newSlot) return string.Empty;
            if (newSlot) return " virtual";
            return method.IsFinal ? " sealed override" : " override";
        }

        private static string ReturnType(MethodInfo method)
        {
            Type type = method.ReturnType;
            if (!type.IsByRef) return TypeName(type);
            return (HasAttribute(method.ReturnParameter, "System.Runtime.CompilerServices.IsReadOnlyAttribute") ? "ref readonly " : "ref ") +
                TypeName(type.GetElementType());
        }

        private static string Parameters(MethodBase method) => string.Join(", ", method.GetParameters().Select(Parameter).ToArray());

        private static string Parameter(ParameterInfo parameter)
        {
            var text = new StringBuilder();
            Type type = parameter.ParameterType;
            if (type.IsByRef)
            {
                text.Append(parameter.IsOut && !parameter.IsIn ? "out "
                    : HasAttribute(parameter, "System.Runtime.CompilerServices.RequiresLocationAttribute") ? "ref readonly "
                    : HasAttribute(parameter, "System.Runtime.CompilerServices.IsReadOnlyAttribute") ? "in " : "ref ");
                type = type.GetElementType();
            }
            else if (HasAttribute(parameter, "System.ParamArrayAttribute")) text.Append("params ");
            text.Append(TypeName(type)).Append(' ').Append(parameter.Name);
            if (parameter.IsOptional) text.Append(" = ").Append(Literal(parameter.RawDefaultValue, type));
            return text.ToString();
        }

        private static string Literal(object value, Type type)
        {
            if (value == null) return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? "default" : "null";
            if (value == DBNull.Value || value is Missing) return "?";
            if (type.IsEnum)
                return "(" + TypeName(type) + ")" + Convert.ToString(value is Enum
                    ? Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture) : value, CultureInfo.InvariantCulture);
            if (value is string) return "\"" + Escape((string)value) + "\"";
            if (value is char) return "'" + Escape(value.ToString()) + "'";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture) + "f";
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture) + "d";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Escape(string text)
        {
            var escaped = new StringBuilder();
            foreach (char character in text)
            {
                if (character == '\\' || character == '"' || character == '\'') escaped.Append('\\').Append(character);
                else if (character < ' ') escaped.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                else escaped.Append(character);
            }
            return escaped.ToString();
        }

        private static bool HasAttribute(ParameterInfo parameter, string name) =>
            parameter.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName == name);

        private static string Access(MethodBase method) => method.IsPublic ? "public" : "protected";

        private static bool Visible(MethodBase method) => method != null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

        private static bool Visible(FieldInfo field) => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;
    }

    // Reads, compares and (on request) rewrites the checked-in baseline files.
    internal static class ApiBaselineFiles
    {
        internal const string PublicSurfaceFile = "PublicApi.txt";
        internal const string FriendFilePrefix = "FriendInternals.";
        internal const string UpdateVariable = "APISHARED_UPDATE_API_BASELINE";

        internal static bool UpdateRequested => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

        internal static string RepositoryRoot()
        {
            for (DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "APIShared.csproj"))) return directory.FullName;
            throw new DirectoryNotFoundException("The APIShared repository root was not found above " + AppDomain.CurrentDomain.BaseDirectory);
        }

        internal static string Directory() => Path.Combine(RepositoryRoot(), "tests", "ApiBaseline");

        internal static string PathOf(string name) => Path.Combine(Directory(), name);

        // Consumer name -> pin file, from FriendInternals.<AssemblyName>.txt.
        internal static Dictionary<string, string> FriendFiles() =>
            System.IO.Directory.GetFiles(Directory(), FriendFilePrefix + "*.txt").ToDictionary(
                path => Path.GetFileNameWithoutExtension(path).Substring(FriendFilePrefix.Length), path => path, StringComparer.Ordinal);

        // Entry lines without the leading '#' comments and blank lines.
        internal static List<string> Entries(string path) =>
            File.ReadAllLines(path).Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)).ToList();

        internal static void Write(string path, IEnumerable<string> entries)
        {
            List<string> header = File.Exists(path)
                ? File.ReadAllLines(path).TakeWhile(line => line.StartsWith("#", StringComparison.Ordinal)).ToList()
                : new List<string>();
            File.WriteAllText(path, string.Join("\r\n", header.Concat(entries).ToArray()) + "\r\n", new UTF8Encoding(false));
        }

        internal static string Difference(string title, IEnumerable<string> lines, int limit = 40)
        {
            List<string> list = lines.ToList();
            if (list.Count == 0) return string.Empty;
            return Environment.NewLine + title + " (" + list.Count + "):" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", list.Take(limit).ToArray()) + (list.Count > limit ? Environment.NewLine + "  ..." : string.Empty);
        }
    }
}
