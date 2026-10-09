using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("APISharedTests")]

[assembly: InternalsVisibleTo("BugfixesAndQoL")]
[assembly: InternalsVisibleTo("MoatMove")]

[assembly: AssemblyVersion(APIShared.APISharedPlugin.PluginVersion)]
[assembly: AssemblyFileVersion(APIShared.APISharedPlugin.PluginVersion)]
[assembly: AssemblyInformationalVersion(APIShared.APISharedPlugin.PluginVersion)]
