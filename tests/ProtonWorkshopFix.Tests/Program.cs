using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using System.Linq.Expressions;
using APIShared.ProtonWorkshopFix;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using APIShared.Workshop.Proton;
using BepInEx.Logging;

internal static class Program
{
    private static string[] references;
    private static int checks;
    private static void Require(bool value, string message)
    {
        checks++; if (!value) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { checks++; return; }
        throw new InvalidOperationException("Accepted invalid contract: " + message);
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string root in references) { string path = Path.Combine(root, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
        return null;
    }
    public static int Main(string[] args)
    {
        try
        {
            string game = args.Length > 0 ? args[0] : throw new ArgumentException("Expected the selected game directory.");
            references = new[] { Path.Combine(game, "BepInEx/core"), Path.Combine(game, "BepInEx/plugins/000shcdese"),
                Path.Combine(game, "Stronghold Crusader Definitive Edition_Data/Managed") };
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Run(game);
            Console.WriteLine("PASS: " + checks + " Proton patcher contracts and retry decisions; no runtime hooks installed or game methods invoked.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= Resolve; }
    }
    private static void Run(string game)
    {
        if (!WinePathInterop.Instance.IsWine)
        {
            int listeners = Logger.Listeners.Count;
            ProtonWorkshopPatcher.Finish();
            Require(Logger.Listeners.Count == listeners && !PatcherRuntime.IsActive(), "Native Windows activated the preloader or hooks.");
        }
        AssemblyTiming(); LauncherPaths(); ParentHandlerPaths(); RetryDecisions(); RetryFiles();
        var assembly = Assembly.LoadFrom(Path.Combine(references[1], "SHCDESE.dll"));
        var contract = ExtenderContract.Validate(assembly);
        Require(contract.AttemptLimit > 0, "Invalid attempt limit.");
        Require(contract.PrepareCounter(false) != null, "Counter transformation did not compile.");
        Require(contract.PrepareUnix(false) != null, "Unix transformation did not compile.");
        Console.WriteLine("PASS: selected Extender signatures, control flow and transformed desktop delegates; Unity Mono JIT remains a separate test.");
        var parent = contract.CounterMethod.DeclaringType.GetMethod("LaunchUpdaterAndExit", BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var kind in new[] { ExceptionHandlerType.Fault, ExceptionHandlerType.Filter })
        using (var copy = new DynamicMethodDefinition(parent))
        {
            copy.Definition.Body.ExceptionHandlers[0].HandlerType = kind;
            string[] before = copy.Definition.Body.Instructions.Select(i => i.ToString()).ToArray();
            Reject(() => ExtenderContract.ValidateParentLauncher(copy.Definition, contract.ManagerTypeName), "unaudited parent " + kind);
            Require(before.SequenceEqual(copy.Definition.Body.Instructions.Select(i => i.ToString())), "Rejected parent handler kind mutated IL.");
        }
        foreach (string mutation in new[] { "read", "write", "cap", "consent", "branch", "handler" })
        using (var copy = new DynamicMethodDefinition(contract.CounterMethod))
        using (var il = new ILContext(copy.Definition))
        {
            var read = il.Body.Instructions.Single(i => ExtenderIl.Calls(i, contract.ManagerTypeName, "ReadDeployAttempts"));
            var write = il.Body.Instructions.Single(i => ExtenderIl.Calls(i, contract.ManagerTypeName, "WriteDeployAttempts"));
            if (mutation == "read") read.OpCode = OpCodes.Nop;
            if (mutation == "write") write.OpCode = OpCodes.Nop;
            if (mutation == "cap") { read.Next.Next.Next.OpCode = OpCodes.Ldc_I4; read.Next.Next.Next.Operand = contract.AttemptLimit + 1; }
            if (mutation == "consent") il.Body.Instructions.Single(i => ExtenderIl.Calls(i, "SHCDESE.Interop.MinWinAPI", "MessageBoxA")).OpCode = OpCodes.Nop;
            if (mutation == "branch") il.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Br, write));
            if (mutation == "handler") il.Body.ExceptionHandlers[0].TryStart = read.Next;
            var before = il.Body.Instructions.ToArray();
            Reject(() => ExtenderIl.ModifyCounter(il, contract.AttemptLimit, contract.ManagerTypeName), mutation);
            Require(before.SequenceEqual(il.Body.Instructions), "Rejected transformation mutated instructions.");
        }
        using (var copy = new DynamicMethodDefinition(contract.CounterMethod))
        using (var il = new ILContext(copy.Definition))
        {
            var original = il.Body.Instructions.ToArray(); var handlers = il.Body.ExceptionHandlers.ToArray();
            ExtenderIl.ModifyCounter(il, contract.AttemptLimit, contract.ManagerTypeName);
            Require(original.All(il.Body.Instructions.Contains), "Counter transform removed original instructions.");
            Require(handlers.SequenceEqual(il.Body.ExceptionHandlers), "Counter transform replaced handlers.");
        }
        using (var copy = new DynamicMethodDefinition(contract.UnixMethod))
        using (var il = new ILContext(copy.Definition))
        {
            var original = il.Body.Instructions.ToArray(); var handlers = il.Body.ExceptionHandlers.ToArray();
            ExtenderIl.ModifyUnix(il, contract);
            Require(original.All(il.Body.Instructions.Contains), "Unix transform removed original instructions.");
            Require(handlers.SequenceEqual(il.Body.ExceptionHandlers), "Unix transform replaced handlers.");
            Require(il.Body.Instructions[1].Operand == original[0], "Inactive Linux hook cannot reach the original body.");
        }
        Require(typeof(ProtonWorkshopPatcher).Assembly.GetReferencedAssemblies().All(a => a.Name != "APIShared" && a.Name != "APIShared.Content" && a.Name != "SHCDESE"),
            "Independent patcher tests acquired a runtime dependency.");
    }
    private static void AssemblyTiming()
    {
        var own = typeof(Program).Assembly;
        int early = 0, late = 0;
        var before = new AssemblyObservation(own.GetName().Name, a => { if (a == own) early++; }, _ => { });
        before.Start(); before.Start();
        Require(early == 1, "Already-loaded assembly was missed or delivered twice.");
        string name = "ProtonLateFixture" + Guid.NewGuid().ToString("N");
        var after = new AssemblyObservation(name, _ => late++, _ => { });
        after.Start();
        AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
        Require(late == 1, "Late assembly was missed before plugin execution.");
        int contained = 0;
        var failing = new AssemblyObservation(own.GetName().Name, _ => { throw new InvalidOperationException("fixture callback"); }, _ => contained++);
        failing.Start();
        Require(contained == 1, "Assembly observer did not contain callback failures.");
    }
    private static void LauncherPaths()
    {
        // Execute only this owned fixture, never a transformed game/Extender method.
        var type = typeof(LauncherFixture);
        var method = type.GetMethod("Unix", BindingFlags.NonPublic | BindingFlags.Instance);
        var contextType = method.GetParameters()[0].ParameterType.GetElementType();
        var contract = new ExtenderContract {
            UnixMethod = method, ManagerTypeName = type.FullName.Replace('+', '/'),
            Getters = new[] { "StagingDirectory", "TargetDirectory", "DeleteManifest", "GameExe", "GamePid" }
                .Select(n => contextType.GetProperty(n).GetGetMethod()).ToArray()
        };
        using (var copy = new DynamicMethodDefinition(method))
        {
            using (var il = new ILContext(copy.Definition))
            {
                ExtenderIl.ModifyUnix(il, contract);
                foreach (var instruction in il.Body.Instructions)
                {
                    if (ExtenderIl.Calls(instruction, typeof(PatcherRuntime).FullName, "IsActive"))
                        instruction.Operand = il.Module.ImportReference(typeof(LauncherFixtureProbe).GetMethod("IsActive"));
                    if (ExtenderIl.Calls(instruction, typeof(ExtenderDeployment).FullName, "Launch"))
                        instruction.Operand = il.Module.ImportReference(typeof(LauncherFixtureProbe).GetMethod("Launch"));
                }
            }
            var delegateType = Expression.GetDelegateType(type, method.GetParameters()[0].ParameterType, typeof(bool));
            var compiled = copy.Generate().CreateDelegate(delegateType);
            object context = Activator.CreateInstance(contextType, new object[] { "stage", "target", "deletes", "game.exe", 71 });
            var owner = new LauncherFixture();
            LauncherFixtureProbe.Active = false;
            Require(!(bool)compiled.DynamicInvoke(owner, context) && owner.OriginalCalls == 1 && LauncherFixtureProbe.Calls == 0,
                "Inactive transformed launcher did not preserve original path.");
            LauncherFixtureProbe.Active = true;
            Require((bool)compiled.DynamicInvoke(owner, context) && owner.OriginalCalls == 1 && LauncherFixtureProbe.Calls == 1,
                "Active transformed launcher executed original body or missed the replacement.");
            Require(ReferenceEquals(LauncherFixtureProbe.Owner, owner) && LauncherFixtureProbe.Values == "stage|target|deletes|game.exe|71",
                "Private by-ref context getters changed callback arguments.");
        }
    }
    private static void RetryDecisions()
    {
        bool active = true, available = true, preflight = true, claimFails = false;
        int probes = 0, consumed = 0;
        var gate = new RetryController(3, _ => { probes++; return preflight; }, () => available, () => {
            if (claimFails) throw new IOException("fixture write denied"); if (!available) return false; available = false; consumed++; return true;
        }, () => active, _ => { });
        object owner = new object();
        active = false;
        Require(gate.AfterRead(3, owner) == 3 && probes == 0, "Inactive hook performed Linux work.");
        active = true;
        Require(gate.AfterRead(1, owner) == 1 && probes == 0, "Ordinary attempt was changed.");
        Require(gate.BeforeWrite(2, owner) == 2 && consumed == 0, "Ordinary attempt consumed retry.");
        preflight = false;
        Require(gate.AfterRead(3, owner) == 3, "Failed preflight released blocked counter.");
        preflight = true;
        Require(gate.AfterRead(5, owner) == 2 && consumed == 0, "Blocked counter was not conditionally released.");
        Require(gate.AfterRead(5, owner) == 2 && consumed == 0, "Declining consumed the retry.");
        Require(gate.BeforeWrite(3, owner) == 6 && consumed == 1 && gate.CanLaunch(owner), "Confirmed retry lost raw count or ownership.");
        Require(gate.AfterRead(6, owner) == 6 && consumed == 1, "Restart released consumed retry.");
        var restarted = new RetryController(3, _ => true, () => available, () => false, () => true, _ => { });
        Require(restarted.AfterRead(6, new object()) == 6, "New runtime bypassed persistent consumed state.");
        available = true; claimFails = true;
        Require(gate.AfterRead(3, owner) == 2, "Failed-claim setup did not release temporarily.");
        Require(gate.BeforeWrite(3, owner) == 3 && !gate.CanLaunch(owner), "Marker write failure did not stop updater.");
        claimFails = false;
        Require(gate.AfterRead(int.MaxValue, owner) == 2, "Saturated setup failed.");
        Require(gate.BeforeWrite(3, owner) == int.MaxValue, "Attempt count overflowed.");
    }
    private static void ParentHandlerPaths()
    {
        foreach (Type type in new[] { typeof(ParentFinallyFixture), typeof(ParentCatchKillFixture) })
        {
            // Compile genuine C# exception control flow, but inspect metadata only:
            // neither this method nor its Process.Kill instruction is ever executed.
            var method = type.GetMethod("LaunchUpdaterAndExit", BindingFlags.NonPublic | BindingFlags.Instance);
            using (var copy = new DynamicMethodDefinition(method))
            {
                var body = copy.Definition.Body;
                string name = type.FullName.Replace('+', '/');
                var unix = body.Instructions.Single(i => ExtenderIl.Calls(i, name, "LaunchUpdaterAndExit_Unix"));
                var kill = body.Instructions.Single(i => ExtenderIl.Calls(i, "System.Diagnostics.Process", "Kill"));
                Require(unix.Next.OpCode == OpCodes.Brtrue || unix.Next.OpCode == OpCodes.Brtrue_S,
                    "Parent regression fixture does not retain the real ternary success guard.");
                Require(body.ExceptionHandlers.Any(h => body.Instructions.IndexOf(kill) >= body.Instructions.IndexOf(h.HandlerStart) &&
                    body.Instructions.IndexOf(kill) < (h.HandlerEnd == null ? body.Instructions.Count : body.Instructions.IndexOf(h.HandlerEnd))),
                    "Parent regression fixture does not put its sole Kill inside the handler.");
                string[] original = body.Instructions.Select(i => i.ToString()).ToArray();
                var handlers = body.ExceptionHandlers.ToArray();
                Reject(() => ExtenderContract.ValidateParentLauncher(copy.Definition, name), type.Name);
                Require(original.SequenceEqual(body.Instructions.Select(i => i.ToString())) && handlers.SequenceEqual(body.ExceptionHandlers),
                    "Rejected parent handler contract changed the method.");
            }
        }
    }
    private static void RetryFiles()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "retry-fixture-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            Require(PatcherFiles.RetryAvailable(root), "New fixture already consumed retry.");
            Require(PatcherFiles.ClaimRetry(root), "Exclusive retry claim failed.");
            Require(!PatcherFiles.RetryAvailable(root) && !PatcherFiles.ClaimRetry(root), "Retry claim repeated.");
            File.WriteAllText(Path.Combine(root, "APIShared.ProtonWorkshopFix", "retry-v1.state"), "");
            Require(!PatcherFiles.RetryAvailable(root), "Truncated record granted retry.");
            bool escaped = false;
            try { PatcherFiles.Inside(root, "../outside"); }
            catch (InvalidDataException) { escaped = true; }
            Require(escaped, "Escaping retry path was accepted.");
        }
        finally
        {
            if (!PatcherFiles.SamePath(Path.GetDirectoryName(root), AppDomain.CurrentDomain.BaseDirectory))
                throw new InvalidOperationException("Fixture cleanup escaped its owned parent.");
            Directory.Delete(root, true);
        }
    }
}

internal sealed class LauncherFixture
{
    internal int OriginalCalls;
    private readonly struct UpdaterContext
    {
        public string StagingDirectory { get; }
        public string TargetDirectory { get; }
        public string DeleteManifest { get; }
        public string GameExe { get; }
        public int GamePid { get; }
        public UpdaterContext(string staging, string target, string deletes, string exe, int pid)
        { StagingDirectory = staging; TargetDirectory = target; DeleteManifest = deletes; GameExe = exe; GamePid = pid; }
    }
    private bool Unix(in UpdaterContext context) { OriginalCalls++; return context.GamePid == -1; }
}

public static class LauncherFixtureProbe
{
    public static bool Active;
    public static int Calls;
    public static object Owner;
    public static string Values;
    public static bool IsActive() => Active;
    public static bool Launch(object owner, string staging, string target, string deletes, string exe, int pid)
    { Calls++; Owner = owner; Values = string.Join("|", staging, target, deletes, exe, pid.ToString()); return true; }
}

internal sealed class ParentFinallyFixture
{
    private readonly struct UpdaterContext { }
    private static bool IsWine() => Environment.Is64BitProcess;
    private bool LaunchUpdaterAndExit_Unix(in UpdaterContext context) => false;
    private bool LaunchUpdaterAndExit_Win32(in UpdaterContext context) => false;
    private void LaunchUpdaterAndExit()
    {
        var context = new UpdaterContext();
        try
        {
            bool launched = IsWine() ? LaunchUpdaterAndExit_Unix(context) : LaunchUpdaterAndExit_Win32(context);
            if (!launched) { Console.WriteLine("fixture failure"); return; }
            Console.WriteLine("fixture success");
        }
        finally { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
    }
}

internal sealed class ParentCatchKillFixture
{
    private readonly struct UpdaterContext { }
    private static bool IsWine() => Environment.Is64BitProcess;
    private bool LaunchUpdaterAndExit_Unix(in UpdaterContext context) => false;
    private bool LaunchUpdaterAndExit_Win32(in UpdaterContext context) => false;
    private void LaunchUpdaterAndExit()
    {
        var context = new UpdaterContext();
        try
        {
            bool launched = IsWine() ? LaunchUpdaterAndExit_Unix(context) : LaunchUpdaterAndExit_Win32(context);
            if (!launched) { Console.WriteLine("fixture failure"); return; }
            Console.WriteLine("fixture success");
        }
        catch (Exception) { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
    }
}
