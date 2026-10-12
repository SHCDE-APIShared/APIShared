param([Parameter(Mandatory)][string]$BootstrapDll,[Parameter(Mandatory)][string]$GameDir,[switch]$PreloadForeign)
$ErrorActionPreference='Stop'
trap { Write-Host $_.ScriptStackTrace; break }
# An isolated PS 5.1 process executes the generated bootstrap's real readiness method.
# No Unity component, Steam operation or game startup is invoked.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
public static class BootstrapTestResolver {
    public static string Game;
    private static readonly HashSet<string> resolving = new HashSet<string>();
    public static Assembly Resolve(object sender, ResolveEventArgs args) {
        if (!resolving.Add(args.Name)) return null;
        try {
            string name = new AssemblyName(args.Name).Name + ".dll";
            foreach (string directory in new[] { "BepInEx/core", "BepInEx/plugins/000shcdese", "Stronghold Crusader Definitive Edition_Data/Managed" }) {
                string path = Path.Combine(Game, directory, name);
                if (File.Exists(path)) return Assembly.Load(File.ReadAllBytes(path));
            }
            return null;
        } finally { resolving.Remove(args.Name); }
    }
}
'@
[BootstrapTestResolver]::Game=$GameDir
$resolver=[Delegate]::CreateDelegate([ResolveEventHandler],[BootstrapTestResolver].GetMethod('Resolve'))
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
$bep=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx/core/BepInEx.dll')))
$bootstrap=[Reflection.Assembly]::LoadFrom($BootstrapDll).GetType('WorkshopBootstrap',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic,Public'
$bootstrap.GetField('log',$flags).SetValue($null,[BepInEx.Logging.ManualLogSource]::new('Bootstrap readiness test'))
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('ApiBootstrapReadiness-'+[guid]::NewGuid().ToString('N'))
$plugins=Join-Path $fixture 'plugins'; $central=Join-Path $plugins 'APIShared_Serp/APIShared.dll'; $foreign=Join-Path $plugins 'Foreign/APIShared.dll'
foreach($path in @($central,$foreign)) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllBytes($path,[IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx/plugins/APIShared_Serp/APIShared.dll')))
}
$paths=$bep.GetType('BepInEx.Paths')
$paths.GetProperty('PluginPath',$flags).SetValue($null,$plugins,$null)
$configuration=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($configuration)
$paths.GetProperty('ConfigPath',$flags).SetValue($null,$configuration,$null)
$paths.GetProperty('BepInExConfigPath',$flags).SetValue($null,(Join-Path $configuration 'BepInEx.cfg'),$null)
$infos=[BepInEx.Bootstrap.Chainloader]::PluginInfos
if($null -eq $infos) { throw 'Isolated BepInEx register was not initialized.' }
$infos.Clear()
$method=$bootstrap.GetMethod('CentralAPISharedReady',$flags)
function Set-Api([string]$Location,[string]$Version) {
    $info=[BepInEx.PluginInfo]::new()
    $info.GetType().GetProperty('Location').SetValue($info,$Location,$null)
    $info.GetType().GetProperty('Metadata').SetValue($info,[BepInEx.BepInPlugin]::new('APIShared_Serp','APIShared',$Version),$null)
    $infos['APIShared_Serp']=$info
}
if($PreloadForeign) {
    $null=[Reflection.Assembly]::LoadFrom($foreign)
    Set-Api $central '0.7.1'
    if($method.Invoke($null,@())) { throw 'Already-loaded foreign assembly was accepted as central.' }
} else {
    if($method.Invoke($null,@())) { throw 'Missing APIShared accepted.' }
    foreach($version in @('0.7.1','0.8.0')) { Set-Api $foreign $version; if($method.Invoke($null,@())) { throw 'Foreign selected plugin accepted.' } }
    Set-Api $central '0.4.10'; if($method.Invoke($null,@())) { throw 'Outdated central metadata accepted.' }
    Set-Api $central '0.7.1'; if(-not $method.Invoke($null,@())) { throw 'Valid central APIShared rejected.' }
}
Write-Host ('PASS: actual generated bootstrap readiness; foreign-preloaded='+$PreloadForeign+'; fixtures='+$fixture)
