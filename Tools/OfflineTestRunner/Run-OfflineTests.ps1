# Offline runner for the shared Core tests while NuGet restore is unavailable.
# Compiles Assets/FamilyRacing/Core/**/*.cs + Tests/EditMode/*.cs (unchanged)
# with the local SDK C# compiler against the .NET 10 reference pack and a
# nunit.framework.dll (defaults to the one bundled with an installed Unity
# editor), then executes them with the local reflection runner. No network use.
param(
    [string]$EditorRoot = "C:\Program Files\Unity\Hub\Editor",
    [string]$NunitFrameworkPath = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outDir = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
    $dotnet = "C:\Program Files\dotnet\dotnet.exe"
}
if (-not $dotnet) { throw "dotnet executable not found on PATH or at C:\Program Files\dotnet." }
$dotnetRoot = Split-Path $dotnet -Parent

function Get-LatestVersionDir([string]$root, [string]$prefix) {
    if (-not (Test-Path $root)) { return $null }
    Get-ChildItem $root -Directory |
        Where-Object { $_.Name.StartsWith($prefix) } |
        Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending |
        Select-Object -First 1
}

$sdkDir = Get-LatestVersionDir (Join-Path $dotnetRoot "sdk") "10."
if (-not $sdkDir) { throw "No .NET 10 SDK found under $dotnetRoot\sdk." }
$csc = Join-Path $sdkDir.FullName "Roslyn\bincore\csc.dll"
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

$refPackRoot = Join-Path $dotnetRoot "packs\Microsoft.NETCore.App.Ref"
$refPack = Get-LatestVersionDir $refPackRoot "10."
if (-not $refPack) { throw "No .NET 10 Microsoft.NETCore.App.Ref pack under $refPackRoot." }
$refDir = Get-ChildItem (Join-Path $refPack.FullName "ref") -Directory |
    Where-Object { $_.Name.StartsWith("net10.") } |
    Sort-Object { try { [version]($_.Name -replace '^net','') } catch { [version]"0.0" } } -Descending |
    Select-Object -First 1
if (-not $refDir) { throw "No net10.0 reference assemblies under $($refPack.FullName)\ref." }
$refs = Get-ChildItem (Join-Path $refDir.FullName "*.dll") | ForEach-Object { "/r:" + $_.FullName }

if ($NunitFrameworkPath) {
    if (-not (Test-Path $NunitFrameworkPath)) {
        throw "Explicit -NunitFrameworkPath does not exist: $NunitFrameworkPath"
    }
    $nunit = Get-Item $NunitFrameworkPath
} else {
    $nunit = Get-ChildItem $EditorRoot -Directory | ForEach-Object {
        Get-ChildItem (Join-Path $_.FullName "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.ext.nunit") -Recurse -Filter "nunit.framework.dll" -File -ErrorAction SilentlyContinue
    } | Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $nunit) {
    throw "nunit.framework.dll not found under $EditorRoot. Pass -NunitFrameworkPath to point at a local NUnit assembly."
}
$refs += "/r:" + $nunit.FullName

$sources = @()
$sources += Get-ChildItem (Join-Path $repo "Assets\FamilyRacing\Core") -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$sources += Get-ChildItem (Join-Path $repo "Assets\FamilyRacing\Tests\EditMode") -Filter *.cs | ForEach-Object { $_.FullName }
$sources += Join-Path $PSScriptRoot "Runner.cs"

$out = Join-Path $outDir "FamilyRacing.OfflineTests.dll"
Copy-Item $nunit.FullName (Join-Path $outDir "nunit.framework.dll") -Force

$compileArgs = @($csc, "/nologo", "/noconfig", "/nostdlib", "/target:exe", "/langversion:9", "/out:$out") + $refs + $sources
# Worktree paths can push this complete source list past Windows' 32K
# process command-line limit. Roslyn response files keep the invocation
# bounded and preserve paths containing spaces.
$responsePath = Join-Path $outDir "compile.rsp"
$responseArgs = $compileArgs | Select-Object -Skip 1 | Where-Object { $_ -ne '/noconfig' } | ForEach-Object {
    '"' + $_.Replace('"', '\"') + '"'
}
[System.IO.File]::WriteAllLines($responsePath, [string[]]$responseArgs,
    (New-Object System.Text.UTF8Encoding($false)))
& $dotnet $csc /noconfig "@$responsePath"
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (csc exit $LASTEXITCODE)." }

$sharedDir = Get-LatestVersionDir (Join-Path $dotnetRoot "shared\Microsoft.NETCore.App") "10."
if (-not $sharedDir) { throw "No .NET 10 runtime under $dotnetRoot\shared\Microsoft.NETCore.App." }
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = "net10.0"
        framework = @{ name = "Microsoft.NETCore.App"; version = $sharedDir.Name }
    }
} | ConvertTo-Json -Depth 4
Set-Content (Join-Path $outDir "FamilyRacing.OfflineTests.runtimeconfig.json") $runtimeConfig

& $dotnet $out
exit $LASTEXITCODE
