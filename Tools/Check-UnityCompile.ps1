# Compile against the installed Unity assemblies without launching the Editor.
# This is a syntax/type check; it cannot execute Unity APIs or import assets.
param([string]$Editor = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor')
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskOutput = Join-Path $taskRoot 'Logs\combat-compile'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskReferences = @()
$taskReferences += Get-ChildItem -LiteralPath (Join-Path $Editor 'Data\Managed\UnityEngine') -Filter '*.dll'
$taskReferences += Get-ChildItem -LiteralPath (Join-Path $Editor 'Data\Managed') -Filter 'UnityEditor*.dll'
$taskReferences += Get-Item -LiteralPath (Join-Path $Editor 'Data\Managed\Newtonsoft.Json.dll')
$taskReferences += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Library\ScriptAssemblies') -Filter 'Unity*.dll' | Where-Object { $_.Name -notmatch 'CodeGen|\.CodeGen\.' }
$taskReferences += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Library\PackageCache') -Filter 'nunit.framework.dll' -Recurse | Select-Object -First 1
$taskReferenceXml = ($taskReferences | Sort-Object FullName -Unique | ForEach-Object {
    '<Reference Include="' + $_.BaseName + '"><HintPath>' + [System.Security.SecurityElement]::Escape($_.FullName) + '</HintPath></Reference>'
}) -join "`n"
$taskSources = @('Core','Runtime','Editor','Tests') | ForEach-Object {
    '<Compile Include="' + $taskRoot + '\Assets\FamilyRacing\' + $_ + '\**\*.cs" />'
}
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><AllowUnsafeBlocks>true</AllowUnsafeBlocks><DefineConstants>UNITY_EDITOR;UNITY_STANDALONE_WIN;UNITY_6000_0_OR_NEWER;ENABLE_INPUT_SYSTEM;DEVELOPMENT_BUILD;UNITY_INCLUDE_TESTS</DefineConstants><NoWarn>CS0618;CS0649;CS0169;CS0108;CS0414</NoWarn></PropertyGroup>
<ItemGroup>$($taskSources -join "`n")</ItemGroup>
<ItemGroup>$taskReferenceXml</ItemGroup>
</Project>
"@
$taskProjectPath = Join-Path $taskOutput 'UnityCompile.csproj'
Set-Content -LiteralPath $taskProjectPath -Value $taskProject -Encoding utf8
dotnet build $taskProjectPath --nologo -v quiet -p:RestoreIgnoreFailedSources=true
exit $LASTEXITCODE
