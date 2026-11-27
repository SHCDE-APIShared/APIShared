using System.Globalization;

namespace APIShared.AssemblyMetadata;

// This CLI is independent of the plugin. Selection is exact and never expands to
// callees, dependency assemblies or unrelated types. Limits may only be lowered,
// except output's explicit bounded allowance above the conservative default.
internal sealed record InspectionOptions(string Input, string Sha256, string Output,
    string[] Types, bool IncludeNested, string[] Methods, string? CalledMember,
    int MaxBodyBytes, int MaxOutputBytes)
{
    internal const int MaxInputBytes = 16 * 1024 * 1024;
    internal const int BodyLimit = 256 * 1024;
    internal const int OutputLimit = 8 * 1024 * 1024;
    internal const int DefaultOutputLimit = 512 * 1024;

    internal static InspectionOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Length > 23) throw new ArgumentException(Usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool nested = false;
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key == "--include-nested")
            {
                if (nested) throw new ArgumentException("Duplicate --include-nested option.");
                nested = true;
                continue;
            }
            if (key is not ("--input" or "--sha256" or "--output" or "--types" or
                "--methods" or "--called-member" or "--max-body-bytes" or "--max-output-bytes"))
                throw new ArgumentException("Unknown option. " + Usage);
            if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(key, args[i])) throw new ArgumentException("Missing value or duplicate option: " + key);
        }
        string Required(string key) => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Required option: " + key);
        string sha = Required("--sha256");
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new ArgumentException("Expected SHA-256 must contain exactly 64 hexadecimal characters.");
        string[] types = Names(Required("--types"), 32);
        string[] methods = values.TryGetValue("--methods", out string? methodNames) ? Names(methodNames, 64) : [];
        values.TryGetValue("--called-member", out string? called);
        if (called != null && (methods.Length != 0 || string.IsNullOrWhiteSpace(called) || called.Length > 4096 ||
            called.IndexOf("::", StringComparison.Ordinal) <= 0 || called.EndsWith("::", StringComparison.Ordinal)))
            throw new ArgumentException("Use either --methods or one exact --called-member Type::Method selection.");
        int Limit(string key, int fallback, int maximum) => values.TryGetValue(key, out string? text)
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0 && number <= maximum
                ? number : throw new ArgumentException("Invalid bounded limit: " + key)
            : fallback;
        string input = Path.GetFullPath(Required("--input")), output = Path.GetFullPath(Required("--output"));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Input and output must be different files.");
        return new(input, sha.ToLowerInvariant(), output, types, nested, methods, called,
            Limit("--max-body-bytes", BodyLimit, BodyLimit), Limit("--max-output-bytes", DefaultOutputLimit, OutputLimit));
    }

    private static string[] Names(string value, int count)
    {
        string[] names = value.Split(';');
        if (names.Length == 0 || names.Length > count || names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 4096 || name != name.Trim()) ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Selections must be distinct exact names separated by semicolons.");
        return names;
    }

    private const string Usage = "AssemblyMetadata --input <dll> --sha256 <expected64hex> --output <fresh.json> --types <Exact.Type;Other.Type> [--include-nested] [--methods <Name;Other> | --called-member <Exact.Type::Method>] [--max-body-bytes <1..262144>] [--max-output-bytes <1..8388608>]";
}
