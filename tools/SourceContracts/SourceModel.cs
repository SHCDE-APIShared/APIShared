#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace APIShared.SourceContracts;

// Deliberately limited source guardrails. Parse C# rather than comments/line windows.
// This is not a whole-program alias, delegate, reflection or exception-flow proof.
// Runtime/backend tests remain authoritative for publication and native contracts.
internal sealed class SourceModel
{
    internal readonly SyntaxNode[] Roots;
    readonly CSharpCompilation compilation;
    readonly HashSet<string> bootstrapFiles;
    readonly Dictionary<InvocationExpressionSyntax, MethodDeclarationSyntax?> targets = new();
    readonly Dictionary<SyntaxTree, SemanticModel> semantics = new();
    SemanticModel Semantic(SyntaxTree tree) {
        if (!semantics.TryGetValue(tree, out var model)) semantics[tree] = model = compilation.GetSemanticModel(tree);
        return model;
    }
    internal SourceModel(IEnumerable<KeyValuePair<string, string>> sources, IEnumerable<string>? bootstrapSources = null,
        IReadOnlyDictionary<string, string[]>? definesByPath = null)
    {
        bootstrapFiles = new(bootstrapSources ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Value,
            new CSharpParseOptions(preprocessorSymbols: definesByPath != null && definesByPath.TryGetValue(s.Key, out var symbols) ? symbols : Array.Empty<string>()), path: s.Key)).ToArray();
        foreach (var tree in trees)
        {
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            Require(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        }
        Roots = trees.Select(t => t.GetRoot()).ToArray();
        compilation = CSharpCompilation.Create("SourceGuard", trees);
    }
    internal static SourceModel Read(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bootstrapPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definesByPath = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string project in new[] { "APIShared.csproj", "bootstrap/APIShared.DependencyBootstrap.csproj" })
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "msbuild", Path.GetFullPath(Path.Combine(root, project)), "-nologo", "-getItem:Compile", "-getProperty:DefineConstants", "-p:Configuration=Release" }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Cannot evaluate runtime project.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Require(process.ExitCode == 0, "Cannot evaluate runtime sources: " + error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult());
            using var json = System.Text.Json.JsonDocument.Parse(output.GetAwaiter().GetResult());
            string[] defines = (json.RootElement.GetProperty("Properties").GetProperty("DefineConstants").GetString() ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var item in json.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()) {
                string path = item.GetProperty("FullPath").GetString()!;
                paths.Add(path);
                definesByPath[path] = definesByPath.TryGetValue(path, out var existing) ? existing.Union(defines).ToArray() : defines;
                if (project != "APIShared.csproj") bootstrapPaths.Add(path);
            }
        }
        Require(paths.Count > 0, "Runtime source selection is empty.");
        return new(paths.Select(path => KeyValuePair.Create(path, File.ReadAllText(path))), bootstrapPaths, definesByPath);
    }
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    internal IEnumerable<TypeDeclarationSyntax> Types => Roots.SelectMany(r => r.DescendantNodes().OfType<TypeDeclarationSyntax>());
    internal TypeDeclarationSyntax Type(string name) => Types.First(t => t.Identifier.ValueText == name);
    IEnumerable<TypeDeclarationSyntax> Parts(string name) => Types.Where(t => t.Identifier.ValueText == name);
    internal static string Text(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));
    static ExpressionSyntax Unwrap(ExpressionSyntax node) {
        while (node is ParenthesizedExpressionSyntax parentheses) node = parentheses.Expression;
        return node;
    }
    static bool Different(ExpressionSyntax condition, string left, string right) => Unwrap(condition) is BinaryExpressionSyntax binary &&
        binary.IsKind(SyntaxKind.NotEqualsExpression) &&
        (Text(Unwrap(binary.Left)) == left && Text(Unwrap(binary.Right)) == right || Text(Unwrap(binary.Left)) == right && Text(Unwrap(binary.Right)) == left);
    static bool Not(ExpressionSyntax condition, string variable) => Unwrap(condition) is PrefixUnaryExpressionSyntax unary &&
        unary.IsKind(SyntaxKind.LogicalNotExpression) && Text(Unwrap(unary.Operand)) == variable;
    internal sealed record Dependency(string GUID, string MinimumVersion);
    internal static IReadOnlyList<Dependency> DependencyDeclarations(string source, IEnumerable<string>? preprocessorSymbols = null)
    {
        // The public script also accepts declaration fragments in its fixtures.
        // Parse recovery retains their attribute/constant nodes; full production
        // syntax and compilation are checked independently by the build.
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: preprocessorSymbols));
        var root = tree.GetRoot();
        var semantic = CSharpCompilation.Create("MetadataGuard", new[] { tree }).GetSemanticModel(tree);
        string Value(ExpressionSyntax expression)
        {
            var value = semantic.GetConstantValue(expression);
            if (value.HasValue && value.Value is string text) return text;
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) return literal.Token.ValueText;
            if (expression is IdentifierNameSyntax id)
            {
                var scope = Owner(expression);
                var declarations = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v => v.Identifier.ValueText == id.Identifier.ValueText && Owner(v) == scope &&
                    (v.Ancestors().OfType<FieldDeclarationSyntax>().Any(f => f.Modifiers.Any(SyntaxKind.ConstKeyword)) ||
                     v.Ancestors().OfType<LocalDeclarationStatementSyntax>().Any(l => l.Modifiers.Any(SyntaxKind.ConstKeyword)))).ToArray();
                Require(declarations.Length == 1 && declarations[0].Initializer != null, "Unresolved dependency constant: " + expression);
                return Value(declarations[0].Initializer!.Value);
            }
            throw new InvalidOperationException("Unresolved dependency value: " + expression);
        }
        var dependencies = new List<Dependency>();
        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>().Where(a => a.Name.ToString().Split('.').Last() is "BepInDependency" or "BepInDependencyAttribute"))
        {
            var arguments = attribute.ArgumentList?.Arguments;
            Require(arguments.HasValue && arguments.Value.Count is 1 or 2, "Unsupported BepInDependency arguments.");
            string guid = Value(arguments!.Value[0].Expression), minimum = "";
            if (arguments.Value.Count == 2)
            {
                var argument = arguments.Value[1].Expression;
                if (argument is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "SoftDependency") continue;
                if (argument is not MemberAccessExpressionSyntax flag || flag.Name.Identifier.ValueText != "HardDependency") minimum = Value(argument);
            }
            dependencies.Add(new(guid, minimum));
        }
        return dependencies;
    }
    internal static string Name(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        _ => ""
    };
    internal static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxNode node) => node.DescendantNodes().OfType<InvocationExpressionSyntax>();
    static TypeDeclarationSyntax? Owner(SyntaxNode node) => node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
    internal static bool IsCall(InvocationExpressionSyntax call, string type, string name) => Name(call) == name &&
        (call.Expression is MemberAccessExpressionSyntax m && Text(m.Expression).Split('.').Last() == type ||
         call.Expression is IdentifierNameSyntax && Owner(call)?.Identifier.ValueText == type);
    MethodDeclarationSyntax? Target(InvocationExpressionSyntax call)
    {
        if (targets.TryGetValue(call, out var found)) return found;
        var info = Semantic(call.SyntaxTree).GetSymbolInfo(call);
        var symbol = info.Symbol as IMethodSymbol;
        var candidates = info.CandidateSymbols.OfType<IMethodSymbol>().ToArray();
        if (symbol == null && candidates.Length == 1) symbol = candidates[0];
        return targets[call] = symbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as MethodDeclarationSyntax;
    }
    static IEnumerable<SyntaxNode> Immediate(SyntaxNode node) => node.DescendantNodes(n =>
        n == node || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    internal void CheckWheat()
    {
        var plugin = Parts("APISharedPlugin").ToArray();
        var dependency = plugin.SelectMany(t => t.AttributeLists).SelectMany(a => a.Attributes).Any(a =>
            a.Name.ToString().Split('.').Last() is "BepInDependency" or "BepInDependencyAttribute" &&
            a.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } guid &&
            Semantic(a.SyntaxTree).GetConstantValue(guid) is { HasValue: true, Value: "fixes" } &&
            a.ArgumentList.Arguments.Any(x => Text(x.Expression).EndsWith("DependencyFlags.SoftDependency", StringComparison.Ordinal)));
        Require(dependency, "Wheat requires the optional Fixes load-order dependency.");
        var awake = plugin.SelectMany(t => t.Members).OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Awake");
        var reached = new HashSet<InvocationExpressionSyntax>();
        var followed = new HashSet<InvocationExpressionSyntax>();
        var helperMethods = Roots.SelectMany(r => r.DescendantNodes().OfType<MethodDeclarationSyntax>()).ToArray();
        var wheatHelpers = helperMethods.Where(m => Calls(m).Any(c => IsCall(c, "FixesWheatStartup", "Initialize"))).ToHashSet();
        bool changed;
        do {
            changed = false;
            foreach (var method in helperMethods)
                if (!wheatHelpers.Contains(method) && Calls(method).Any(c => Target(c) is { } target && wheatHelpers.Contains(target)))
                    changed |= wheatHelpers.Add(method);
        } while (changed);
        var active = new HashSet<MethodDeclarationSyntax>();
        int Flow(SyntaxNode? node, int attempts)
        {
            if (node == null) return attempts;
            if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) return attempts;
            if (node is IfStatementSyntax branch)
            {
                attempts = Flow(branch.Condition, attempts);
                int yes = Flow(branch.Statement, attempts), no = Flow(branch.Else?.Statement, attempts);
                Require(yes == no, "Wheat startup is conditional or has inconsistent attempts.");
                return yes;
            }
            if (node is ReturnStatementSyntax) Require(attempts == 1, "Startup can return without the wheat attempt.");
            if (node is InvocationExpressionSyntax call)
            {
                // Evaluate receiver/arguments first, following C# evaluation order.
                foreach (var child in call.ChildNodes()) attempts = Flow(child, attempts);
                if (IsCall(call, "FixesWheatStartup", "Initialize"))
                {
                    reached.Add(call);
                    Require(++attempts == 1, "Wheat initialization can be attempted twice in startup.");
                }
                else
                {
                    if (Name(call) == "InitializeManaged" || Name(call) == "Initialize" && Text(call.Expression).Contains("runtime."))
                        Require(attempts == 1, "API publication can precede the wheat startup attempt.");
                    var target = Target(call);
                    // Follow local helper chains owning the wheat attempt, even across types/files.
                    if (target != null && wheatHelpers.Contains(target))
                    {
                        followed.Add(call);
                        Require(active.Add(target), "Recursive startup helper requires an explicit audit.");
                        attempts = Flow((SyntaxNode?)target.Body ?? target.ExpressionBody, attempts);
                        active.Remove(target);
                    }
                }
                return attempts;
            }
            if (node is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.AddAssignmentExpression) &&
                Text(assignment.Left).EndsWith("LibraryLoaded", StringComparison.Ordinal))
                Require(attempts == 1, "Native startup callback can be published before wheat initialization.");
            // Loops, switches and exception paths containing a wheat call need a deliberate audit,
            // rather than guessing execution counts from lexical ordering.
            if (node is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax or TryStatementSyntax)
                Require(!Calls(node).Any(c => IsCall(c, "FixesWheatStartup", "Initialize") || Target(c) is { } t && wheatHelpers.Contains(t)),
                    "Nonlinear wheat startup flow requires an explicit audit.");
            Require(node is not (GotoStatementSyntax or LabeledStatementSyntax), "Unstructured startup jumps require an explicit audit.");
            foreach (var child in node.ChildNodes()) attempts = Flow(child, attempts);
            return attempts;
        }
        Require(Flow(awake.Body, 0) == 1, "Startup must attempt wheat initialization before API publication.");
        var allCalls = Roots.SelectMany(Calls).Where(c => IsCall(c, "FixesWheatStartup", "Initialize")).ToArray();
        Require(allCalls.Length > 0 && allCalls.All(reached.Contains), "Wheat initialization has a non-startup or deferred caller.");
        Require(Roots.SelectMany(Calls).Where(c => Target(c) is { } t && wheatHelpers.Contains(t)).All(followed.Contains),
            "A wheat startup helper has a later/deferred caller.");
        foreach (var call in Roots.SelectMany(Calls).Where(c => c.Expression is MemberAccessExpressionSyntax m && Text(m.Expression).EndsWith("FixesWheatMemory", StringComparison.Ordinal)))
            Require(Owner(call)?.Identifier.ValueText == "FixesWheatStartup", "Wheat memory access escaped its startup owner.");
        var wheatCalls = Parts("FixesWheatStartup").SelectMany(Calls).ToArray();
        Require(wheatCalls.Any(c => IsCall(c, "Interlocked", "Exchange")), "Missing one-attempt wheat guard.");
        Require(wheatCalls.Any(c => IsCall(c, "Monitor", "TryEnter") && c.ArgumentList.Arguments.Count > 1 &&
            c.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax timeout && timeout.Token.ValueText == "0"), "Missing nonblocking engine lock.");
        var startupGuards = helperMethods.Where(m => Calls(m).Any(c => IsCall(c, "FixesWheatContract", "IsStartup"))).ToHashSet();
        do {
            changed = false;
            foreach (var method in helperMethods)
                if (!startupGuards.Contains(method) && Calls(method).Any(c => Target(c) is { } target && startupGuards.Contains(target))) changed |= startupGuards.Add(method);
        } while (changed);
        foreach (var writer in wheatCalls.Where(c => IsCall(c, "FixesWheatMemory", "WriteOperand"))) {
            var block = writer.Ancestors().OfType<BlockSyntax>().First();
            Require(Calls(block).Any(c => c.SpanStart < writer.SpanStart && Target(c) is { } target && startupGuards.Contains(target)),
                "Wheat operand writer lost its startup-context recheck.");
        }
        foreach (var type in Types.Where(t => t.Identifier.ValueText.StartsWith("FixesWheat", StringComparison.Ordinal)))
            Require(!type.Members.OfType<MethodDeclarationSyntax>().Any(m => Lifecycle.Contains(m.Identifier.ValueText)) &&
                !type.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.IsKind(SyntaxKind.AddAssignmentExpression)),
                "Wheat correction acquired a later callback or lifecycle entry.");
    }
    static readonly HashSet<string> Lifecycle = new() { "OnDestroy", "OnDisable", "OnApplicationQuit", "Update", "LateUpdate", "FixedUpdate" };
    internal void CheckWheatMemory()
    {
        var writers = Parts("FixesWheatMemory").SelectMany(Calls).Where(c => c.Expression is MemberAccessExpressionSyntax a &&
            Text(a.Expression).EndsWith("Marshal", StringComparison.Ordinal) && Name(c).StartsWith("Write", StringComparison.Ordinal)).ToArray();
        // One byte-writing site is the explicitly audited exception, not a feature count.
        Require(writers.Length == 1 && Name(writers[0]) == "WriteByte", "Wheat exception must contain exactly one byte writer.");
        var write = writers[0];
        var guarded = write.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault();
        Require(guarded?.Finally != null, "Wheat writer must restore protection/cache in finally.");
        var restore = Calls(guarded!.Finally!).SingleOrDefault(c => Name(c) == "VirtualProtect");
        var flush = Calls(guarded.Finally!).SingleOrDefault(c => Name(c) == "FlushInstructionCache");
        Require(restore != null && flush != null, "Wheat writer lost protection/cache restoration.");
        static bool One(ExpressionSyntax e) => e is CastExpressionSyntax cast ? One(cast.Expression) :
            e is LiteralExpressionSyntax literal && Convert.ToInt64(literal.Token.Value) == 1;
        Require(One(restore!.ArgumentList.Arguments[1].Expression) && One(flush!.ArgumentList.Arguments[2].Expression), "Wheat restoration escaped its one-byte range.");
        var method = write.Ancestors().OfType<MethodDeclarationSyntax>().First();
        var protect = Calls(method).FirstOrDefault(c => Name(c) == "VirtualProtect" && c.SpanStart < guarded.SpanStart);
        Require(protect != null && One(protect.ArgumentList.Arguments[1].Expression), "Wheat write lost its one-byte protection preflight.");
        var protectionGate = protect!.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Require(protectionGate != null && Unwrap(protectionGate.Condition) is PrefixUnaryExpressionSyntax negate && negate.IsKind(SyntaxKind.LogicalNotExpression) && Unwrap(negate.Operand) == protect &&
            protectionGate.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any(), "Denied memory protection does not prevent the write.");
        var old = protect!.ArgumentList.Arguments[3].Expression;
        string oldName = old is DeclarationExpressionSyntax declaration ? Text(declaration.Designation) : Text(old);
        Require(Text(restore!.ArgumentList.Arguments[2].Expression) == oldName, "Wheat restoration does not use the observed original protection.");
        var observed = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v =>
            v.Initializer?.Value is InvocationExpressionSyntax c && Name(c) == "Check" && c.SpanStart < protect.SpanStart &&
            c.ArgumentList.Arguments.Count == 3 && One(c.ArgumentList.Arguments[1].Expression) && c.ArgumentList.Arguments[2].Expression.IsKind(SyntaxKind.TrueLiteralExpression));
        Require(observed != null && guarded.Block.DescendantNodes().OfType<IfStatementSyntax>().Any(i =>
            Different(i.Condition, oldName, observed.Identifier.ValueText) && i.SpanStart < write.SpanStart &&
            i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any()), "Wheat write lacks the observed-protection race check.");
        Require(method.DescendantNodes().OfType<IfStatementSyntax>().Any(i => i.SpanStart > guarded.Span.End &&
            i.Condition is BinaryExpressionSyntax compare && compare.IsKind(SyntaxKind.NotEqualsExpression) && Text(compare.Right) == oldName &&
            compare.Left is InvocationExpressionSyntax check && Name(check) == "Check" &&
            i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any()), "Wheat writer lost post-write protection verification.");
        var pointer = Text(write.ArgumentList.Arguments[0].Expression);
        Require(Text(protect.ArgumentList.Arguments[0].Expression) == pointer && Text(restore.ArgumentList.Arguments[0].Expression) == pointer &&
            Text(flush!.ArgumentList.Arguments[1].Expression) == pointer, "Wheat protection/cache targets differ from its byte writer.");
        var restored = restore.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        var flushed = flush.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        Require(restored != null && flushed != null && guarded.Finally!.DescendantNodes().OfType<IfStatementSyntax>().Any(i =>
            Unwrap(i.Condition) is BinaryExpressionSyntax disjunction && disjunction.IsKind(SyntaxKind.LogicalOrExpression) &&
            (Not(disjunction.Left, Text(restored.Left)) && Not(disjunction.Right, Text(flushed.Left)) || Not(disjunction.Right, Text(restored.Left)) && Not(disjunction.Left, Text(flushed.Left))) &&
            i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any()),
            "Wheat restoration/cache failure is not rejected.");
        foreach (var type in Types.Where(t => t.Identifier.ValueText.StartsWith("FixesWheat", StringComparison.Ordinal) && t.Identifier.ValueText != "FixesWheatMemory"))
            Require(!Calls(type).Any(c => Name(c) is "VirtualProtect" or "FlushInstructionCache" ||
                c.Expression is MemberAccessExpressionSyntax a && Text(a.Expression).EndsWith("Marshal", StringComparison.Ordinal) && Name(c).StartsWith("Write", StringComparison.Ordinal)),
                "Wheat executable writer escaped its memory owner.");
    }
    internal void CheckLifecycle()
    {
        foreach (var node in Roots.SelectMany(r => r.DescendantNodes()).Where(n => n is NameSyntax or MemberAccessExpressionSyntax))
        {
            string name = Text(node);
            Require(name is not ("JavaScriptSerializer" or "DataContractJsonSerializer" or "JsonUtility") &&
                !name.StartsWith("System.Text.Json", StringComparison.Ordinal) && !name.StartsWith("Newtonsoft.Json", StringComparison.Ordinal),
                "Forbidden runtime serializer usage: " + node.GetLocation().GetLineSpan());
        }
        foreach (var group in Types.GroupBy(t => Semantic(t.SyntaxTree).GetDeclaredSymbol(t)?.ToDisplayString() ?? t.Identifier.ValueText))
        {
            bool plugin = group.Any(t => t.BaseList?.Types.Any(b => Text(b.Type).EndsWith("BaseUnityPlugin", StringComparison.Ordinal)) == true);
            foreach (var method in group.SelectMany(t => t.Members.OfType<MethodDeclarationSyntax>()))
            {
                string name = method.Identifier.ValueText;
                Require(!plugin || name is not ("Update" or "LateUpdate" or "FixedUpdate" or "OnGUI" or "OnRenderObject" or "OnPostRender"),
                    "Short-lived plugin component owns a frame callback: " + method.GetLocation().GetLineSpan());
                if (name is "OnDestroy" or "OnDisable" or "OnApplicationPause")
                    Require(!Calls(method).Any(c => Name(c) is "Dispose" or "DisposeRuntime" or "Stop" or "Undo" or "Unpatch" or "UnpatchAll" or "UnHook") &&
                        !method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)),
                        "Startup cleanup reaches teardown/unsubscription: " + method.GetLocation().GetLineSpan());
            }
        }
        foreach (var call in Roots.SelectMany(Calls))
            Require(Name(call) is not ("StartCoroutine" or "InvokeRepeating"), "Component scheduling needs a persistent publisher: " + call.GetLocation().GetLineSpan());
    }
    static readonly HashSet<string> Mutations = new() { "Dispose", "Undo", "UnHook", "Disable", "DisableAll" };
    static bool HookType(string text) => text.Contains("Hook", StringComparison.Ordinal) || text.Contains("NativeDetour", StringComparison.Ordinal) || text.Contains("ImGuiShutdownCompiler", StringComparison.Ordinal);
    bool HookReceiver(InvocationExpressionSyntax call)
    {
        ExpressionSyntax? receiver = call.Expression is MemberAccessExpressionSyntax m ? m.Expression :
            call.Expression is MemberBindingExpressionSyntax ? call.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression : null;
        if (receiver == null) return false;
        var semantic = Semantic(call.SyntaxTree);
        var info = semantic.GetTypeInfo(receiver);
        if (HookType(info.Type?.ToDisplayString() ?? "")) return true;
        // Missing external game references can leave an error symbol. Resolve declarations,
        // not local variable spellings, for both direct and collection/indexed receivers.
        foreach (var id in receiver.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbol = semantic.GetSymbolInfo(id).Symbol;
            var declaredType = symbol switch { IFieldSymbol f => f.Type, ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, _ => null };
            if (HookType(declaredType?.ToDisplayString() ?? "")) return true;
        }
        return false;
    }
    internal void CheckHooks()
    {
        var audioMethods = Parts("SoundRuntime").SelectMany(t => t.Members).OfType<MethodDeclarationSyntax>().ToArray();
        var audioFlags = Parts("SoundRuntime").SelectMany(t => t.Members).OfType<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Type is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.BoolKeyword))
            .SelectMany(f => f.Declaration.Variables).Select(v => v.Identifier.ValueText).ToHashSet();
        // Infer the private publication flag from the rollback gate; its spelling is not a contract.
        var barriers = audioMethods.Where(m => Calls(m).Any(c => Mutations.Contains(Name(c)) && HookReceiver(c)))
            .SelectMany(m => m.Body?.Statements.OfType<IfStatementSyntax>() ?? Enumerable.Empty<IfStatementSyntax>())
            .Where(i => Unwrap(i.Condition) is IdentifierNameSyntax id && audioFlags.Contains(id.Identifier.ValueText) &&
                i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any())
            .Select(i => Text(Unwrap(i.Condition))).Distinct().ToArray();
        string? barrier = barriers.SingleOrDefault();
        foreach (var root in Roots)
        foreach (var call in Calls(root))
        {
            string name = Name(call), owner = Owner(call)?.Identifier.ValueText ?? "";
            bool hookReceiver = HookReceiver(call);
            if (bootstrapFiles.Contains(call.SyntaxTree.FilePath) && hookReceiver)
                Require(name is not ("Dispose" or "Undo" or "Apply" or "Disable" or "Enable"), "Bootstrap hook mutation after constructor publication: " + call.GetLocation().GetLineSpan());
            if (call.Expression is MemberAccessExpressionSyntax access && access.Expression is MemberAccessExpressionSyntax handle &&
                handle.Name.Identifier.ValueText == "Hook")
                Require(name is not ("Enable" or "Disable"), "Published handle acquired an executable toggle: " + call.GetLocation().GetLineSpan());
            // Marshal.Write* also writes ordinary owned data (gatehouse state, buffers).
            // Its mere presence is not evidence of an executable-memory mutation.
            if (name is "VirtualProtect" or "FlushInstructionCache" || IsCall(call, "CodePatch", "Write"))
                Require(owner == "FixesWheatMemory", "Executable writer outside the narrow wheat boundary: " + call.GetLocation().GetLineSpan());
            if (!Mutations.Contains(name) || !hookReceiver) continue;
            var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            // Other managed installers have legacy candidate cleanup in installation catches.
            // Preserve that pre-existing guardrail scope; a catch is NOT runtime safety evidence.
            // Their partial-publication behavior is recorded as an audit limitation.
            bool legacyCandidateCatch = call.Ancestors().OfType<CatchClauseSyntax>().Any() &&
                owner is not ("SoundRuntime" or "NativeHookPublicationState" or "ScriptExtenderFixesRuntime" or "WorkshopCleanupRuntime" or "DependencyBootstrapPatcher");
            bool guardedRollback = owner == "NativeHookPublicationState" && method?.Body?.Statements.OfType<IfStatementSyntax>().Any(i =>
                Text(Unwrap(i.Condition)) == "HasStarted" && i.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any() && i.SpanStart < call.SpanStart) == true ||
                owner == "SoundRuntime" && barrier != null && method?.Body?.Statements.OfType<IfStatementSyntax>().Any(i =>
                Text(Unwrap(i.Condition)) == barrier && i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any() && i.SpanStart < call.SpanStart) == true;
            bool guardedCaller = owner == "ScriptExtenderFixesRuntime" && method != null && Roots.SelectMany(Calls).Where(c => Target(c) == method).Any(c =>
                c.Ancestors().OfType<IfStatementSyntax>().Any(i => i.Condition is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.EqualsExpression) &&
                    Text(b.Right) == "0" && i.Statement.Span.Contains(c.Span)));
            Require(guardedRollback || guardedCaller || legacyCandidateCatch,
                "Hook teardown outside guarded candidate rollback: " + call.GetLocation().GetLineSpan());
            // Guard checks here are syntactic. Backend behavior tests prove that publication
            // attempts cannot be undone, including a throwing first Enable.
            if (owner == "NativeHookPublicationState")
                Require(method!.Body!.Statements.OfType<IfStatementSyntax>().Any(i => Text(Unwrap(i.Condition)) == "HasStarted" && i.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any() && i.SpanStart < call.SpanStart),
                    "Candidate disposal is not guarded against publication.");
            else if (owner == "SoundRuntime")
                Require(barrier != null && method!.Body!.Statements.OfType<IfStatementSyntax>().Any(i => Text(Unwrap(i.Condition)) == barrier && i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any() && i.SpanStart < call.SpanStart),
                    "Audio rollback lacks its publication guard.");
        }
        // Publication sequence is independent of the number of native audio hooks.
        foreach (var method in audioMethods)
        {
            var enable = Calls(method).Where(c => Name(c) == "Enable" && HookReceiver(c)).ToArray();
            if (enable.Length == 0) continue;
            var publish = Immediate(method).OfType<AssignmentExpressionSyntax>().SingleOrDefault(a => Text(a.Left) == barrier && a.Right.IsKind(SyntaxKind.TrueLiteralExpression));
            Require(publish != null && enable.All(c => c.SpanStart > publish.SpanStart), "Audio publication barrier follows a hook write.");
            foreach (var ready in Calls(method).Where(c => Name(c) == "SetReady" && c.ArgumentList.Arguments.Any(a => a.Expression.IsKind(SyntaxKind.TrueLiteralExpression))))
                Require(enable.All(c => c.SpanStart < ready.SpanStart), "Audio readiness precedes a hook write.");
        }
        foreach (var type in Types.Where(t => t.Identifier.ValueText is "WorkshopCleanupRuntime" or "SoundRuntime" or "ScriptExtenderFixesRuntime" or "DependencyBootstrapPatcher"))
            Require(!type.Members.OfType<MethodDeclarationSyntax>().Any(m => Lifecycle.Contains(m.Identifier.ValueText)), "Process hooks acquired component lifecycle teardown: " + type.Identifier);
        foreach (var type in Types.Where(t => bootstrapFiles.Contains(t.SyntaxTree.FilePath)))
            Require(!type.Members.OfType<MethodDeclarationSyntax>().Any(m => Lifecycle.Contains(m.Identifier.ValueText)), "Bootstrap acquired component lifecycle teardown.");
    }
    internal void CheckWorkshop()
    {
        var startup = Parts("WorkshopStartup").ToArray();
        Require(startup.SelectMany(Calls).Any(c => IsCall(c, "Interlocked", "Exchange")), "Workshop confirmation lost its one-attempt guard.");
        Require(startup.SelectMany(Calls).Any(c => IsCall(c, "WorkshopStartupGate", "Run")), "Workshop startup bypasses its authorization gate.");
        Require(!startup.SelectMany(t => t.Members).OfType<MethodDeclarationSyntax>().Any(m => m.Modifiers.Any(SyntaxKind.AsyncKeyword) && m.ReturnType.ToString() == "void"), "Workshop startup cannot be async void.");
        var runtime = Parts("WorkshopRuntime").ToArray();
        Require(runtime.SelectMany(t => t.Members).OfType<FieldDeclarationSyntax>().Any(f => f.Modifiers.Any(SyntaxKind.StaticKeyword) && f.Declaration.Type.ToString() == "IDisposable"), "Workshop subscription needs a process root.");
        Require(runtime.SelectMany(Calls).Any(c => IsCall(c, "WorkshopStartup", "Run")), "Workshop runtime no longer invokes startup coordination.");
        Require(runtime.SelectMany(Calls).Any(c => Name(c) == "Validate") && runtime.SelectMany(Calls).Any(c => Name(c) == "Quit"), "Workshop update handoff lacks authorization validation/quit.");
        Require(!Types.Any(t => t.Identifier.ValueText == "WorkshopViewModel") && !runtime.SelectMany(Calls).Any(c => Name(c) == "RegisterLobbyModSettings"), "Removed Workshop UI is registered again.");
        Require(!Parts("WorkshopSteamUpload").SelectMany(Calls).Any(c => Name(c) == "UploadWorkshopMap"), "Workshop publisher acquired the vanilla upload singleton.");
    }
}
