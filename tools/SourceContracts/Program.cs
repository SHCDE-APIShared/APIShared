using APIShared.SourceContracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

try
{
    Console.InputEncoding = new System.Text.UTF8Encoding(false);
    if (args.Length < 2) throw new ArgumentException("Usage: SourceContracts <repository> <wheat|hooks|workshop|lifecycle|interop> [additional source files]");
    if (args[1] == "dependencies") {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SourceModel.DependencyDeclarations(Console.In.ReadToEnd(),
            args.Length > 2 ? args[2].Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>())));
        return 0;
    }
    var model = SourceModel.Read(args[0]);
    switch (args[1])
    {
        case "wheat": model.CheckWheat(); model.CheckWheatMemory(); break;
        case "hooks": model.CheckHooks(); break;
        case "workshop": model.CheckWorkshop(); break;
        case "lifecycle": model.CheckLifecycle(); break;
        case "interop":
            var selected = model.Types.Where(t => t.Identifier.ValueText is "UnitAccess" or "UnitSettledScriptExtenderSource").Cast<Microsoft.CodeAnalysis.SyntaxNode>()
                .Concat(args.Skip(2).Select(path => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot())).ToArray();
            SourceModel.Require(selected.Length > 0, "No interop source owner was found.");
            var fields = selected.SelectMany(r => r.DescendantTokens()).Where(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken) && t.ValueText.StartsWith("r_", StringComparison.Ordinal))
                .Select(t => t.ValueText).Distinct().Order().ToArray();
            SourceModel.Require(fields.Length > 0, "No consumed interop field was found.");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(fields));
            return 0;
        default: throw new ArgumentException("Unknown source contract group.");
    }
    Console.WriteLine("PASS: " + args[1] + " syntax guardrails (not a runtime/backend proof).");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
