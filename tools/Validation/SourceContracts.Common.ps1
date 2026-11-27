# The parser comes from the required .NET SDK, with no game/workspace dependency.
# Run it separately: Windows PowerShell cannot load the SDK's .NET assemblies.
function Invoke-ApiSourceContracts {
    param([Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][ValidateSet('wheat','hooks','workshop','lifecycle','interop','dependencies')][string]$Group,
        [string[]]$AdditionalSourceFiles = @())
    & dotnet run --project (Join-Path $PSScriptRoot '../SourceContracts/SourceContracts.csproj') --configuration Release --verbosity quiet -- $Root $Group @AdditionalSourceFiles
    if ($LASTEXITCODE -ne 0) { throw "APIShared $Group source guardrail failed." }
}
