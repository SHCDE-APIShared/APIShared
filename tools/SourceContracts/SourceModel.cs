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
        foreach (var writer in wheatCalls.Where(c => IsCall(c, "FixesWheatMemory", "WriteRange"))) {
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
        static bool ToNative(InvocationExpressionSyntax c) => c.Expression is MemberAccessExpressionSyntax a &&
            Text(a.Expression).EndsWith("Marshal", StringComparison.Ordinal) &&
            (Name(c).StartsWith("Write", StringComparison.Ordinal) ||
             Name(c) == "Copy" && c.ArgumentList.Arguments.Count == 4 &&
             c.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax zero && zero.Token.ValueText == "0");
        var writers = Parts("FixesWheatMemory").SelectMany(Calls).Where(ToNative).ToArray();
        Require(writers.Length == 1 && Name(writers[0]) == "Copy", "Wheat must have one bounded native range writer.");
        var write = writers[0];
        var method = write.Ancestors().OfType<MethodDeclarationSyntax>().First();
        var parameters = method.ParameterList.Parameters;
        string protectName = parameters.Single(p => p.Type is IdentifierNameSyntax n && n.Identifier.ValueText == "ProtectMemory").Identifier.ValueText;
        string flushName = parameters.Single(p => p.Type is GenericNameSyntax n && n.Identifier.ValueText == "Func").Identifier.ValueText;
        string pointer = Text(write.ArgumentList.Arguments[2].Expression), length = Text(write.ArgumentList.Arguments[3].Expression);
        string RangeSize(ExpressionSyntax node) => Text(node is CastExpressionSyntax cast ? cast.Expression : node);
        bool SameRange(InvocationExpressionSyntax call) => Text(call.ArgumentList.Arguments[0].Expression) == pointer &&
            RangeSize(call.ArgumentList.Arguments[1].Expression) == length;
        var guarded = write.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault(t => t.Finally != null);
        Require(guarded != null, "Wheat writer must restore protection/cache in finally.");
        var restore = Calls(guarded!.Finally!).SingleOrDefault(c => Name(c) == protectName);
        var flush = Calls(guarded.Finally!).SingleOrDefault(c => Name(c) == flushName);
        Require(restore != null && flush != null && SameRange(restore) && SameRange(flush), "Wheat restoration/cache escaped the write range.");
        Require(flush!.Ancestors().OfType<FinallyClauseSyntax>().Any(f => f != guarded.Finally), "Restore failure must still synchronize the instruction cache.");
        var protect = Calls(method).FirstOrDefault(c => Name(c) == protectName && c.SpanStart < guarded.SpanStart);
        Require(protect != null && SameRange(protect), "Missing bounded protection preflight.");
        var protectionGate = protect!.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Require(protectionGate != null && Unwrap(protectionGate.Condition) is PrefixUnaryExpressionSyntax negate &&
            negate.IsKind(SyntaxKind.LogicalNotExpression) && Unwrap(negate.Operand) == protect &&
            protectionGate.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any(), "Denied protection does not prevent writing.");
        var old = protect.ArgumentList.Arguments[3].Expression;
        string oldName = old is DeclarationExpressionSyntax declaration ? Text(declaration.Designation) : Text(old);
        Require(Text(restore!.ArgumentList.Arguments[2].Expression) == oldName, "Restore must use the original protection.");
        var observed = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v =>
            v.Initializer?.Value is InvocationExpressionSyntax c && Name(c) == "Check" && c.SpanStart < protect.SpanStart &&
            RangeSize(c.ArgumentList.Arguments[1].Expression) == length &&
            c.ArgumentList.Arguments[2].Expression.IsKind(SyntaxKind.TrueLiteralExpression));
        Require(observed != null && guarded.Block.DescendantNodes().OfType<IfStatementSyntax>().Any(i =>
            Different(i.Condition, oldName, observed.Identifier.ValueText) && i.SpanStart < write.SpanStart &&
            i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any()), "Missing observed-protection race check.");
        Require(method.DescendantNodes().OfType<IfStatementSyntax>().Any(i => i.SpanStart > guarded.Span.End &&
            i.Condition is BinaryExpressionSyntax compare && compare.IsKind(SyntaxKind.NotEqualsExpression) &&
            Text(compare.Right) == oldName && compare.Left is InvocationExpressionSyntax check && Name(check) == "Check" &&
            i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any()), "Missing post-write protection check.");
        var restored = restore.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        var flushed = flush.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        Require(restored != null && flushed != null && guarded.Finally!.DescendantNodes().OfType<IfStatementSyntax>().Any(i =>
            Unwrap(i.Condition) is BinaryExpressionSyntax either && either.IsKind(SyntaxKind.LogicalOrExpression) &&
            (Not(either.Left, Text(restored.Left)) && Not(either.Right, Text(flushed.Left)) ||
             Not(either.Right, Text(restored.Left)) && Not(either.Left, Text(flushed.Left))) &&
            i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any()), "Restoration/cache failure is not rejected.");
        Require(Calls(method).Count(c => IsCall(c, "FixesWheatAnalysis", "RequireEqual") && c.SpanStart < write.SpanStart) >= 2 &&
            Calls(method).Any(c => IsCall(c, "FixesWheatAnalysis", "RequireEqual") && c.SpanStart > guarded.Span.End),
            "Missing preimage/recheck/read-back validation.");
        Require(method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(v =>
            v.Identifier.ValueText == length && v.Initializer?.Value is MemberAccessExpressionSyntax member &&
            member.Name.Identifier.ValueText == "Length"), "Write length must come from the approved byte range.");
        var bounded = method.Body!.Statements.OfType<IfStatementSyntax>().First();
        Require(bounded.SpanStart < protect.SpanStart && bounded.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any() &&
            bounded.Condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().Any(b =>
                b.IsKind(SyntaxKind.GreaterThanExpression) && b.Left is MemberAccessExpressionSyntax m &&
                m.Name.Identifier.ValueText == "Length" && b.Right is LiteralExpressionSyntax n && n.Token.ValueText == "256"),
            "Missing maximum validated-stub write bound.");
        foreach (var call in Roots.SelectMany(Calls).Where(c => IsCall(c, "FixesWheatMemory", "WriteRangeCore") || Name(c) == "WriteRangeCore" && Owner(c)?.Identifier.ValueText == "FixesWheatMemory"))
            Require(Owner(call)?.Identifier.ValueText == "FixesWheatMemory" &&
                call.ArgumentList.Arguments.Any(a => Text(a.Expression) == "VirtualProtect") &&
                Calls(call).Any(c => Name(c) == "FlushInstructionCache"), "Production range writer must use real OS protection/cache operations.");
        foreach (var type in Types.Where(t => t.Identifier.ValueText.StartsWith("FixesWheat", StringComparison.Ordinal) && t.Identifier.ValueText != "FixesWheatMemory"))
            Require(!Calls(type).Any(c => Name(c) is "VirtualProtect" or "FlushInstructionCache" || ToNative(c)),
                "Executable writer escaped its memory owner.");
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
    // Named initialization owner, with semantic call-target checks rather than a file-wide
    // exception. The allowed span is derived from the actual guarded publication/disposal
    // calls; the PowerShell consumer uses these exact locations as its limited allowance.
    internal HashSet<InvocationExpressionSyntax> UnitVoiceHookAllowances()
    {
        static bool Returns(StatementSyntax statement) => statement is ReturnStatementSyntax ||
            statement is BlockSyntax block && block.Statements.Count == 1 && block.Statements[0] is ReturnStatementSyntax;
        var allowed = new HashSet<InvocationExpressionSyntax>();
        var parts = Parts("UnitVoiceNativeRuntime").ToArray();
        if (parts.Length == 0) return allowed;
        var methods = parts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>().ToArray();
        var initialize = methods.SingleOrDefault(m => m.Identifier.ValueText == "Initialize");
        Require(initialize?.Body != null, "Unit voice publication lost its named initializer.");
        var publishers = methods.Where(m => Calls(m).Any(c => Name(c) == "Enable" && HookReceiver(c))).ToArray();
        Require(publishers.Length == 1, "Unit voice hooks require one audited publication helper.");
        var publisher = publishers[0];
        Require(publisher.Modifiers.Any(SyntaxKind.PrivateKeyword) && publisher.Modifiers.Any(SyntaxKind.StaticKeyword) &&
            publisher.ParameterList.Parameters.Count == 1 && publisher.Body != null,
            "Unit voice publication must remain a private static entry helper.");
        string parameter = publisher.ParameterList.Parameters[0].Identifier.ValueText;
        var enables = Calls(publisher).Where(c => Name(c) == "Enable" && HookReceiver(c)).ToArray();
        Require(enables.Length == 1, "A unit voice entry must have one publication attempt.");
        var enable = enables[0];
        var barriers = Immediate(publisher).OfType<AssignmentExpressionSyntax>().Where(a =>
            a.Left is MemberAccessExpressionSyntax member && Text(member.Expression) == parameter &&
            a.Right.IsKind(SyntaxKind.TrueLiteralExpression) && a.SpanStart < enable.SpanStart &&
            a.Parent is ExpressionStatementSyntax statement && statement.Parent == publisher.Body).ToArray();
        Require(barriers.Length == 1, "Unit voice entry publication barrier must precede Enable, including its failure path.");
        var barrier = barriers[0];
        string memberName = ((MemberAccessExpressionSyntax)barrier.Left).Name.Identifier.ValueText;
        var entryType = parts.SelectMany(p => p.Members).OfType<TypeDeclarationSyntax>().SingleOrDefault(t =>
            t.Identifier.ValueText == Text(publisher.ParameterList.Parameters[0].Type!));
        Require(entryType != null && entryType.Modifiers.Any(SyntaxKind.PrivateKeyword), "Unit voice entry ownership must remain private.");
        Require(entryType.Members.OfType<FieldDeclarationSyntax>().Any(f =>
            !f.Modifiers.Any(SyntaxKind.StaticKeyword) && f.Declaration.Type is PredefinedTypeSyntax type && type.Keyword.IsKind(SyntaxKind.BoolKeyword) &&
            f.Declaration.Variables.Any(v => v.Identifier.ValueText == memberName)), "Unit voice publication state must be an instance Boolean field.");
        Require(publisher.Body.Statements.FirstOrDefault() == barrier.Parent,
            "Unit voice publication cannot branch or call before publishing its barrier.");

        var once = initialize.Body.Statements.OfType<IfStatementSyntax>().FirstOrDefault(statement =>
            Unwrap(statement.Condition) is BinaryExpressionSyntax condition && condition.IsKind(SyntaxKind.NotEqualsExpression) && Text(condition.Right) == "0" &&
            Unwrap(condition.Left) is InvocationExpressionSyntax call && IsCall(call, "Interlocked", "Exchange") &&
            call.ArgumentList.Arguments.Count == 2 && Text(call.ArgumentList.Arguments[1].Expression) == "1" &&
            Returns(statement.Statement));
        Require(once != null && initialize.Body.Statements.FirstOrDefault() == once, "Unit voice initialization must be attempted once before doing any work.");
        var onceCall = (InvocationExpressionSyntax)Unwrap(((BinaryExpressionSyntax)Unwrap(once.Condition)).Left);
        string attempted = Text(onceCall.ArgumentList.Arguments[0].Expression);
        foreach (var assignment in parts.SelectMany(p => p.DescendantNodes()).OfType<AssignmentExpressionSyntax>())
        {
            Require(Text(assignment.Left) != attempted, "Unit voice initialization attempt state can be reset.");
            if (assignment.Left is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == memberName)
                Require(ReferenceEquals(assignment, barrier), "Unit voice entry publication state can be reset or bypassed.");
        }
        var ready = Calls(initialize).Where(call => IsCall(call, "Volatile", "Write") && call.ArgumentList.Arguments.Count == 2 &&
            Text(call.ArgumentList.Arguments[1].Expression) == "1").ToArray();
        Require(ready.Length == 1, "Unit voice readiness must have one initialization publication.");
        var callers = Roots.SelectMany(Calls).Where(call => Target(call) == publisher).ToArray();
        Require(callers.Length > 0 && callers.All(call => call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == initialize &&
            !call.Ancestors().Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) &&
            call.SpanStart > once.SpanStart && call.SpanStart < ready[0].SpanStart),
            "Unit voice Enable acquired a deferred/later caller or follows readiness.");
        foreach (var reference in parts.SelectMany(p => p.DescendantNodes()).OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == publisher.Identifier.ValueText))
            Require(reference.Parent is InvocationExpressionSyntax direct && direct.Expression == reference ||
                reference.Parent is MemberAccessExpressionSyntax access && access.Parent is InvocationExpressionSyntax memberCall && memberCall.Expression == access,
                "Unit voice publisher escaped as a method group.");
        var roots = parts.SelectMany(p => p.Members).OfType<FieldDeclarationSyntax>().Where(f =>
            f.Modifiers.Any(SyntaxKind.StaticKeyword) && f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) &&
            f.Declaration.Type is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Any(t => Text(t) == entryType.Identifier.ValueText))
            .SelectMany(f => f.Declaration.Variables).Select(v => v.Identifier.ValueText).ToHashSet();
        Require(roots.Count > 0, "Published unit voice entries lost their process root.");
        foreach (var call in parts.SelectMany(Calls))
        {
            if ((Name(call) is "Clear" or "Remove" or "RemoveAt" or "RemoveAll" or "RemoveRange") && call.Expression is MemberAccessExpressionSyntax access)
                Require(!roots.Contains(Text(access.Expression)), "Published unit voice roots can be removed.");
            if (call.ArgumentList.Arguments.Any(a => a.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) && Text(a.Expression) == attempted))
                Require(ReferenceEquals(call, onceCall) || IsCall(call, "Volatile", "Read"), "Unit voice attempted state has a later writer.");
        }
        allowed.Add(enable);
        foreach (var call in parts.SelectMany(Calls).Where(c => Name(c) == "Dispose" && HookReceiver(c)))
        {
            var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            Require(method?.Body != null && method.Modifiers.Any(SyntaxKind.PrivateKeyword),
                "Unit voice candidate disposal escaped its private guard.");
            var candidates = method.ParameterList.Parameters.Where(p => Text(p.Type!) == entryType.Identifier.ValueText).ToArray();
            Require(candidates.Length == 1, "Unit voice candidate disposal must guard one owned entry.");
            string candidate = candidates[0].Identifier.ValueText;
            Require(method.Body.Statements.FirstOrDefault() is IfStatementSyntax guard &&
                Unwrap(guard.Condition) is MemberAccessExpressionSyntax published && Text(published.Expression) == candidate &&
                published.Name.Identifier.ValueText == memberName && Returns(guard.Statement) &&
                guard.SpanStart < call.SpanStart,
                "Published unit voice entry can be disposed after a successful or throwing Enable.");
            allowed.Add(call);
        }
        Require(!methods.Any(m => Lifecycle.Contains(m.Identifier.ValueText)), "Unit voice process hooks acquired lifecycle teardown.");
        return allowed;
    }
    // Exact CALL-site writer provenance. Backend tests execute E8/relay ABI and partial failure;
    // this guard only permits the single startup writer once its call chain/barrier is proved.
    internal HashSet<InvocationExpressionSyntax> ConstructionGoldCallAllowances()
    {
        var allowed = new HashSet<InvocationExpressionSyntax>();
        var patchParts = Parts("ConstructionGoldCallPatch").ToArray();
        if (patchParts.Length == 0) return allowed;
        var runtimeParts = Parts("HumanConstructionRuntime").ToArray();
        var methods = runtimeParts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>().ToArray();
        var initializer = methods.SingleOrDefault(m => m.Identifier.ValueText == "InitializeGoldRefundCalls");
        var entry = methods.SingleOrDefault(m => m.Identifier.ValueText == "Initialize");
        Require(initializer?.Body != null && initializer.Modifiers.Any(SyntaxKind.PrivateKeyword) &&
            initializer.Modifiers.Any(SyntaxKind.StaticKeyword) && entry?.Body != null,
            "Gold CALL publication lost its private startup initializer.");
        var guards = initializer.Body.Statements.OfType<IfStatementSyntax>().Where(i =>
            i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any()).ToArray();
        static IEnumerable<ExpressionSyntax> OrTerms(ExpressionSyntax condition) => Unwrap(condition) is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.LogicalOrExpression)
            ? OrTerms(binary.Left).Concat(OrTerms(binary.Right)) : new[]{Unwrap(condition)};
        Require(guards.Any(g => ReferenceEquals(g, initializer.Body.Statements.First()) &&
            OrTerms(g.Condition).Any(n => Not(n,"NativeStartupPatchWindow.IsOpen")) &&
            OrTerms(g.Condition).Any(n => Different(n,"hash","ApiSharedRuntime.SupportedHash")) &&
            OrTerms(g.Condition).Any(n => n is BinaryExpressionSyntax compare && compare.IsKind(SyntaxKind.NotEqualsExpression) &&
                Unwrap(compare.Left) is InvocationExpressionSyntax checksum && IsCall(checksum,"ApiSharedRuntime","ComputeSha256") &&
                checksum.ArgumentList.Arguments.Any(a => Text(a.Expression).StartsWith("context.Memory.Slice(",StringComparison.Ordinal)) &&
                Unwrap(compare.Right) is LiteralExpressionSyntax literal && literal.Token.ValueText.Length==64)),
            "Gold CALL publication requires startup-window, image identity and function-byte guards before work.");
        static bool ByteGuard(ForStatementSyntax loop) => loop.Statement is IfStatementSyntax check &&
            Unwrap(check.Condition) is PrefixUnaryExpressionSyntax not && not.IsKind(SyntaxKind.LogicalNotExpression) &&
            Unwrap(not.Operand) is InvocationExpressionSyntax equal && Name(equal)=="SequenceEqual" &&
            equal.Expression is MemberAccessExpressionSyntax access && Text(access.Expression)=="context.Memory.Slice(GoldCallRvas[i],5)" &&
            equal.ArgumentList.Arguments.Count==1 && Text(equal.ArgumentList.Arguments[0].Expression)=="GoldCallBytes[i]" &&
            check.Statement is ThrowStatementSyntax && loop.Declaration?.Variables.Count==1 &&
            Text(loop.Declaration.Variables[0])=="i=0" &&
            (Text(loop.Condition!)=="i<GoldCallRvas.Length" || Text(loop.Condition!)=="i<2") &&
            loop.Incrementors.Count==1 && Text(loop.Incrementors[0])=="i++";
        Require(initializer.Body.Statements.OfType<ForStatementSyntax>().Any(ByteGuard),
            "Both gold CALL instruction snapshots must be checked before publication.");
        var all = Roots.SelectMany(Calls).ToArray();
        static MethodDeclarationSyntax? Containing(InvocationExpressionSyntax call) => call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        static bool Direct(InvocationExpressionSyntax call) => !call.Ancestors().Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
        void OnlyDirectCaller(MethodDeclarationSyntax method, MethodDeclarationSyntax caller)
        {
            var calls = all.Where(c => Target(c)==method).ToArray();
            Require(calls.Length==1 && Containing(calls[0])==caller && Direct(calls[0]),
                "Gold CALL writer acquired a late, repeated or deferred caller: "+method.Identifier);
            bool RefersTo(IdentifierNameSyntax reference)
            {
                SyntaxNode expression=reference.Parent is MemberAccessExpressionSyntax member && member.Name==reference ? member : reference;
                var info=Semantic(reference.SyntaxTree).GetSymbolInfo(expression);
                return (info.Symbol==null ? info.CandidateSymbols : info.CandidateSymbols.Add(info.Symbol))
                    .OfType<IMethodSymbol>().Any(symbol=>symbol.DeclaringSyntaxReferences.Any(r=>r.GetSyntax()==method));
            }
            foreach(var reference in Roots.SelectMany(r=>r.DescendantNodes().OfType<IdentifierNameSyntax>()).Where(n=>n.Identifier.ValueText==method.Identifier.ValueText && RefersTo(n)))
                Require(reference.Parent is InvocationExpressionSyntax direct && direct.Expression==reference ||
                    reference.Parent is MemberAccessExpressionSyntax access && access.Parent is InvocationExpressionSyntax member && member.Expression==access,
                    "Gold CALL publisher escaped as a method group.");
        }
        OnlyDirectCaller(initializer,entry);
        var patchMethods=patchParts.SelectMany(p=>p.Members).OfType<MethodDeclarationSyntax>().ToArray();
        var writes=patchParts.SelectMany(Calls).Where(c=>IsCall(c,"CodePatch","Write")).ToArray();
        Require(writes.Length==1,"Gold CALL patch requires exactly one audited writer.");
        var writer=writes[0];var publisher=Containing(writer);
        Require(publisher?.Body!=null && publisher.Identifier.ValueText=="Publish" &&
            !publisher.Modifiers.Any(SyntaxKind.StaticKeyword) &&
            writer.ArgumentList.Arguments.Count==2 && Text(writer.ArgumentList.Arguments[0].Expression)=="sites[i]" &&
            Text(writer.ArgumentList.Arguments[1].Expression)=="calls[i]",
            "Gold CALL writer escaped its prepared pair publisher.");
        OnlyDirectCaller(publisher,initializer);
        var publishCall=all.Single(c=>Target(c)==publisher);
        Require(publishCall.Expression is MemberAccessExpressionSyntax,"Gold CALL publication must use its process-held candidate.");
        string candidate=Text(((MemberAccessExpressionSyntax)publishCall.Expression).Expression);
        var fields=runtimeParts.SelectMany(p=>p.Members).OfType<FieldDeclarationSyntax>().ToArray();
        Require(fields.Any(f=>f.Modifiers.Any(SyntaxKind.StaticKeyword) && Text(f.Declaration.Type)=="ConstructionGoldCallPatch" &&
            f.Declaration.Variables.Any(v=>v.Identifier.ValueText==candidate)),"Gold CALL candidate lost its process root.");
        var begin=Calls(initializer).Where(c=>Name(c)=="Begin" && c.ArgumentList.Arguments.Count==3 &&
            Text(c.ArgumentList.Arguments[2].Expression)==candidate).ToArray();
        Require(begin.Length==1 && Direct(begin[0]) && begin[0].SpanStart<publishCall.SpanStart &&
            begin[0].Expression is MemberAccessExpressionSyntax,"Gold CALL publication barrier must root the candidate before writing.");
        string gate=Text(((MemberAccessExpressionSyntax)begin[0].Expression).Expression);
        Require(fields.Any(f=>f.Modifiers.Any(SyntaxKind.StaticKeyword) && f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) &&
            Text(f.Declaration.Type)=="NativeHookPublicationState" && f.Declaration.Variables.Any(v=>v.Identifier.ValueText==gate)),
            "Gold CALL publication requires a process-held immutable publication gate.");
        var publishTry=publishCall.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault();
        Require(publishTry!=null && publishTry.Block.Statements.Count==2 &&
            begin[0].Parent is ExpressionStatementSyntax beginStatement && beginStatement.Parent==publishTry.Parent &&
            beginStatement.SpanStart<publishTry.SpanStart &&
            publishTry.Block.Statements[0].Span.Contains(publishCall.Span) &&
            Calls(publishTry.Block.Statements[1]).Any(c=>Text(c.Expression)==gate+".Complete") &&
            publishTry.Catches.Count==1 && Calls(publishTry.Catches[0]).Any(c=>Text(c.Expression)==gate+".Fail") &&
            publishTry.Catches[0].Block.Statements.Last() is ThrowStatementSyntax,
            "Gold CALL second-write failure must retain a failed publication, never roll it back.");
        var prepares=Calls(initializer).Where(c=>IsCall(c,"ConstructionGoldCallPatch","Prepare")).ToArray();
        Require(prepares.Length==1 && Direct(prepares[0]) && prepares[0].SpanStart<begin[0].SpanStart &&
            prepares[0].Parent is AssignmentExpressionSyntax assign && Text(assign.Left)==candidate,
            "Gold CALL preparation must complete before its publication barrier.");
        Require(initializer.Body.Statements.OfType<ForStatementSyntax>().Any(loop=>loop.SpanStart<prepares[0].SpanStart && ByteGuard(loop)),
            "Gold CALL byte guards moved behind preparation/publication.");
        // Only the unpublished-cleanup callback may clear rooted fields. There is no native
        // undo/dispose path in this feature, including either write's exception path.
        foreach(var assignment in runtimeParts.SelectMany(p=>p.DescendantNodes().OfType<AssignmentExpressionSyntax>()).Where(a=>Text(a.Left)==candidate))
            Require(assignment.Right==prepares[0] || assignment.Ancestors().OfType<InvocationExpressionSyntax>().Any(c=>
                Text(c.Expression)==gate+".RollbackUnpublished"),"Published gold CALL root can be reset.");
        foreach(var call in patchParts.SelectMany(Calls).Concat(Calls(initializer)))
            Require(Name(call) is not ("Disable" or "Undo" or "Dispose" or "Free" or "VirtualFree"),"Published gold CALL relay acquired teardown.");
        Require(!patchMethods.Any(m=>Lifecycle.Contains(m.Identifier.ValueText)),"Gold CALL publisher acquired lifecycle teardown.");
        allowed.Add(writer);return allowed;
    }
    // The host barrier changes one CALL during the same retained publication as the receive detour.
    internal HashSet<InvocationExpressionSyntax> ResyncDataCallAllowances()
    {
        var allowed = new HashSet<InvocationExpressionSyntax>();
        var parts = Parts("MultiplayerResyncDataNativeRuntime").ToArray();
        if (parts.Length == 0) return allowed;
        var writers = parts.SelectMany(Calls).Where(call => IsCall(call, "CodePatch", "Write")).ToArray();
        Require(writers.Length == 1, "Resync data requires exactly one host CALL writer.");
        var writer = writers[0];
        var initializer = writer.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        static IEnumerable<ExpressionSyntax> OrTerms(ExpressionSyntax condition) =>
            Unwrap(condition) is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.LogicalOrExpression)
                ? OrTerms(binary.Left).Concat(OrTerms(binary.Right)) : new[] { Unwrap(condition) };
        Require(initializer?.Identifier.ValueText == "Initialize" &&
            initializer.Body?.Statements.FirstOrDefault() is IfStatementSyntax startup && startup.Statement is ThrowStatementSyntax &&
            OrTerms(startup.Condition).Any(n => Not(n, "NativeStartupPatchWindow.IsOpen")) &&
            OrTerms(startup.Condition).Any(n => Different(n, "hash", "ApiSharedRuntime.SupportedHash")),
            "Resync data CALL must be guarded by startup-window and image checks.");
        var callback = writer.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault();
        Require(callback?.Parent is ArgumentSyntax argument && argument.Parent is ArgumentListSyntax arguments &&
            arguments.Arguments.Count == 5 && arguments.Arguments[4] == argument &&
            arguments.Parent is InvocationExpressionSyntax publish && IsCall(publish, "NativeHookPublication", "Enable") &&
            !publish.Ancestors().Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) &&
            callback.Block is BlockSyntax block && block.Statements.Count == 3 &&
            Text(block.Statements[0]) == "NativeIndirectEntry.VerifyBytes(module+HostCallRva,HostCall);" &&
            block.Statements[1] is ExpressionStatementSyntax writeStatement && writeStatement.Expression == writer &&
            Text(block.Statements[2]) == "ValidatePublished();" &&
            writer.ArgumentList.Arguments.Count == 2 &&
            Text(writer.ArgumentList.Arguments[0].Expression) == "Address(module+HostCallRva)" &&
            Text(writer.ArgumentList.Arguments[1].Expression) == "patchedCall",
            "Resync data CALL must be verified and written within retained startup publication to the same address.");
        allowed.Add(writer);
        return allowed;
    }

    internal void CheckHooks()
    {
        var unitVoiceAllowances = UnitVoiceHookAllowances();
        var goldCallAllowances = ConstructionGoldCallAllowances();
        var resyncCallAllowances = ResyncDataCallAllowances();
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
            var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            // The shared managed installer creates its local hook with ManualApply.
            // Its first Apply and catch-local rollback both precede returning the handle.
            bool safeIlCandidate = owner == "SafeIlHook" && method?.Identifier.ValueText == "TryInstall" &&
                method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
                    Text(a.Left) == "native" && a.Right is ObjectCreationExpressionSyntax creation &&
                    Text(creation.Type) == "ILHook" && creation.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(option =>
                        Text(option.Left) == "ManualApply" && option.Right.IsKind(SyntaxKind.TrueLiteralExpression))) &&
                (Text(call.Expression) == "native.Apply" ||
                    (name == "Dispose" && call.Parent is ConditionalAccessExpressionSyntax conditional &&
                        Text(conditional.Expression) == "native" && call.Ancestors().OfType<CatchClauseSyntax>().Any()));
            if (bootstrapFiles.Contains(call.SyntaxTree.FilePath) && hookReceiver)
                Require(safeIlCandidate || name is not ("Dispose" or "Undo" or "Apply" or "Disable" or "Enable"),
                    "Bootstrap hook mutation outside candidate installation: " + call.GetLocation().GetLineSpan());
            if (call.Expression is MemberAccessExpressionSyntax access && access.Expression is MemberAccessExpressionSyntax handle &&
                handle.Name.Identifier.ValueText == "Hook")
                Require(name is not ("Enable" or "Disable") || unitVoiceAllowances.Contains(call), "Published handle acquired an executable toggle: " + call.GetLocation().GetLineSpan());
            // Marshal.Write* also writes ordinary owned data (gatehouse state, buffers).
            // Its mere presence is not evidence of an executable-memory mutation.
            bool preparedNativeCode = owner is "PlagueNativeCode" or "ProductionGoodsReceivedNativeCode";
            if (name is "VirtualProtect" or "FlushInstructionCache" || IsCall(call, "CodePatch", "Write") ||
                owner == "ProductionGoodsReceivedNativeCode" && IsCall(call, "Marshal", "Copy"))
                Require(owner == "FixesWheatMemory" || goldCallAllowances.Contains(call) || resyncCallAllowances.Contains(call) ||
                    (preparedNativeCode && call.Ancestors().OfType<ConstructorDeclarationSyntax>().Any()),
                    "Executable writer outside an audited preparation boundary: " + call.GetLocation().GetLineSpan());
            // These two named preparation boundaries allocate private RW memory, write it
            // once in the constructor, then protect RX and flush before publication. Receipt
            // AdapterAddress exposes only a published helper; no future code writes exist.
            // This does not permit runtime patching, or freeing a published helper after a
            // later source-installation failure. The publication barrier is checked below.
            if (preparedNativeCode && name == "VirtualFree")
                Require(call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Body?.Statements
                    .OfType<IfStatementSyntax>().Any(i => Text(Unwrap(i.Condition)) == "published" &&
                        i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any() && i.SpanStart < call.SpanStart) == true,
                    "Published native preparation code can be freed: " + call.GetLocation().GetLineSpan());
            if (!Mutations.Contains(name) || !hookReceiver) continue;
            // Other managed installers have legacy candidate cleanup in installation catches.
            // Preserve that pre-existing guardrail scope; a catch is NOT runtime safety evidence.
            // Their partial-publication behavior is recorded as an audit limitation.
            bool legacyCandidateCatch = call.Ancestors().OfType<CatchClauseSyntax>().Any() &&
                owner is not ("SoundRuntime" or "NativeHookPublicationState" or "ScriptExtenderFixesRuntime" or "WorkshopCleanupRuntime" or "DependencyBootstrapPatcher" or "UnitVoiceNativeRuntime");
            bool guardedRollback = owner == "NativeHookPublicationState" && method?.Body?.Statements.OfType<IfStatementSyntax>().Any(i =>
                Text(Unwrap(i.Condition)) == "HasStarted" && i.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any() && i.SpanStart < call.SpanStart) == true ||
                owner == "SoundRuntime" && barrier != null && method?.Body?.Statements.OfType<IfStatementSyntax>().Any(i =>
                Text(Unwrap(i.Condition)) == barrier && i.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any() && i.SpanStart < call.SpanStart) == true;
            bool guardedCaller = owner == "ScriptExtenderFixesRuntime" && method != null && Roots.SelectMany(Calls).Where(c => Target(c) == method).Any(c =>
                c.Ancestors().OfType<IfStatementSyntax>().Any(i => i.Condition is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.EqualsExpression) &&
                    Text(b.Right) == "0" && i.Statement.Span.Contains(c.Span)));
            Require(guardedRollback || guardedCaller || legacyCandidateCatch || unitVoiceAllowances.Contains(call),
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
}
