using System;
using System.Linq;

// -p:MathOnly=true executes the real cold compiler/registry contracts without mapping or
// executing game/native code. The ordinary executable suite still owns native verification.
internal static partial class Program
{
    private const string NativeHash="FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
    private static int checks;
    private static void Check(bool condition,string message)
    { if(!condition) throw new InvalidOperationException(message); checks++; }
    private static int Main(string[] args)
    {
        if(args.Length==1 && args[0]=="--surface")
        {
            foreach(string line in ApiCompatibility.ApiSignatures.PublicSurface(typeof(APIShared.Economy.WorkerProductionModifiers).Assembly
                .GetExportedTypes().Where(type=>type.Namespace=="APIShared.Economy" && type.Name.StartsWith("WorkerProduction",StringComparison.Ordinal))))
                Console.WriteLine(line);
            return 0;
        }
        try { TestRegistry(); TestCompilerAndProfiles(); Console.WriteLine("PASS: "+checks+" production compiler/registry assertions; no native execution."); return 0; }
        catch(Exception failure) { Console.Error.WriteLine(failure); return 1; }
    }
}
