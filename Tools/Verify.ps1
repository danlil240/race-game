# Foundation verification entry point.
#
#   -Mode CoreOffline      Compile and run the shared Core tests with the
#                          offline runner (no NuGet restore, no network).
#   -Mode Core             Run `dotnet test` on Tools/CoreTests with
#                          --no-restore. Requires a previously completed
#                          restore; fails honestly if assets are missing.
#   -Mode CheckEnvironment Verify the pinned Unity editor and required
#                          modules are installed; nonzero on any gap.
#   -Mode Setup            Run FoundationProjectSetup via the pinned editor.
#   -Mode EditMode         Run Unity EditMode tests; results XML + log under
#                          Logs\.
#   -Mode PlayMode         Run Unity PlayMode tests; results XML + log under
#                          Logs\.
#   -Mode Windows          Build the Windows Mono development player.
#   -Mode WindowsOrdinary  Build the Windows Mono ordinary player: same
#                          content and backend without the development flag,
#                          so validation endpoints are compiled out.
#   -Mode Android          Export Android, then build with bundled Gradle
#                          --offline. Missing dependencies fail without download.
#   -Mode VisualBenchmarkSetup
#                          Run FoundationProjectSetup + VisualBenchmarkSetup:
#                          create/validate the visual-1 benchmark scene and
#                          definition under Generated\VisualBenchmark.
#   -Mode WindowsVisualBenchmark
#                          Windows Mono development player of the benchmark
#                          scene -> Builds\Windows-VisualBenchmark.
#   -Mode WindowsVisualBenchmarkOrdinary
#                          Same benchmark content without the development
#                          flag -> Builds\Windows-VisualBenchmark-Ordinary.
#   -Mode AndroidVisualBenchmark / -Mode AndroidVisualBenchmarkOrdinary
#                          Offline wrapper with -Presentation VisualBenchmark;
#                          output under Builds\Android-VisualBenchmark[-Ordinary].
#   -Mode FullContentSetup Validate the authored three-world full-content
#                          inventory under Generated\FullContent; fails the
#                          gate honestly while Tasks 5-8 content is missing.
#   -Mode WindowsFullContent[Ordinary]
#                          Windows Mono full-content player flavors ->
#                          Builds\Windows-FullContent[-Ordinary] with a
#                          contentStage=complete manifest.
#   -Mode WindowsFullContentPreview / -Mode AndroidFullContentPreview
#                          Development-only intermediate checks: generic
#                          catalog validation over authored content only,
#                          Builds\<Platform>-FullContent-Preview roots and a
#                          contentStage=partial manifest — never a candidate.
#   -Mode AndroidFullContent / -Mode AndroidFullContentOrdinary
#                          Offline wrapper with -Presentation FullContent;
#                          output under Builds\Android-FullContent[-Ordinary].
#   -Mode AndroidFamilyRelease
#                          Offline wrapper with -Presentation FullContent
#                          -Configuration Release: unsigned release Gradle
#                          output, zipalign + release-key signing via
#                          Tools/Sign-AndroidRelease.ps1, certificate and
#                          metadata verification, published under
#                          Builds\Android-FamilyRelease. Signing secrets
#                          come from the FAMILY_RACING_* process env only.
#   -Mode WindowsFamilyRelease
#                          Windows Mono ordinary player stamped with the
#                          release identity (FoundationBuild.
#                          WindowsFamilyRelease): the FullContent
#                          production gate runs first and validation
#                          endpoints are compiled out; output under
#                          Builds\Windows-FamilyRelease.
#
# All Unity modes use the pinned editor only; each writes its own log under
# Logs\ and propagates failures. Android keeps distinct export/build logs
# and a result.json under Logs\android-offline\<run-id>.
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("CoreOffline", "Core", "CheckEnvironment", "Setup", "EditMode", "PlayMode", "Windows", "WindowsOrdinary", "Android",
        "VisualBenchmarkSetup", "WindowsVisualBenchmark", "WindowsVisualBenchmarkOrdinary",
        "AndroidVisualBenchmark", "AndroidVisualBenchmarkOrdinary",
        "FullContentSetup", "WindowsFullContent", "WindowsFullContentOrdinary",
        "WindowsFullContentPreview",
        "AndroidFullContent", "AndroidFullContentOrdinary", "AndroidFullContentPreview",
        "AndroidFamilyRelease", "WindowsFamilyRelease")]
    [string]$Mode,

    [string]$EditorRoot = "C:\Program Files\Unity\Hub\Editor",
    [string]$NunitFrameworkPath = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$PinnedEditorVersion = "6000.3.26f1"

function Find-Dotnet {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
        $dotnet = "C:\Program Files\dotnet\dotnet.exe"
    }
    return $dotnet
}

switch ($Mode) {
    "Android" {
        & (Join-Path $repo "Tools\Build-AndroidOffline.ps1") -ProjectPath $repo -EditorRoot $EditorRoot
        exit $LASTEXITCODE
    }

    { $_ -in "AndroidVisualBenchmark", "AndroidVisualBenchmarkOrdinary" } {
        $configuration = if ($Mode -eq "AndroidVisualBenchmarkOrdinary") { "Ordinary" } else { "Development" }
        & (Join-Path $repo "Tools\Build-AndroidOffline.ps1") -ProjectPath $repo -EditorRoot $EditorRoot `
            -Presentation VisualBenchmark -Configuration $configuration
        exit $LASTEXITCODE
    }

    { $_ -in "AndroidFullContent", "AndroidFullContentOrdinary" } {
        $configuration = if ($Mode -eq "AndroidFullContentOrdinary") { "Ordinary" } else { "Development" }
        & (Join-Path $repo "Tools\Build-AndroidOffline.ps1") -ProjectPath $repo -EditorRoot $EditorRoot `
            -Presentation FullContent -Configuration $configuration
        exit $LASTEXITCODE
    }

    # The signed family release flavor: Release is allowed only with
    # FullContent (the wrapper enforces the gate). Signing credentials
    # arrive through the FAMILY_RACING_* process environment.
    "AndroidFamilyRelease" {
        & (Join-Path $repo "Tools\Build-AndroidOffline.ps1") -ProjectPath $repo -EditorRoot $EditorRoot `
            -Presentation FullContent -Configuration Release
        exit $LASTEXITCODE
    }

    # Windows release flavor: ordinary Mono player stamped with the
    # release identity; the FullContent production gate runs inside the
    # editor entry point, so an incomplete inventory fails here honestly.
    "WindowsFamilyRelease" {
        & (Join-Path $repo "Tools\Build-Windows.ps1") -ProjectPath $repo -EditorRoot $EditorRoot `
            -Configuration FamilyRelease
        exit $LASTEXITCODE
    }

    # The preview is development-only; there is deliberately no ordinary
    # preview flavor.
    "AndroidFullContentPreview" {
        & (Join-Path $repo "Tools\Build-AndroidOffline.ps1") -ProjectPath $repo -EditorRoot $EditorRoot `
            -Presentation FullContentPreview -Configuration Development
        exit $LASTEXITCODE
    }

    "CoreOffline" {
        $runnerArgs = @{ EditorRoot = $EditorRoot }
        if ($NunitFrameworkPath) { $runnerArgs.NunitFrameworkPath = $NunitFrameworkPath }
        & (Join-Path $repo "Tools\OfflineTestRunner\Run-OfflineTests.ps1") @runnerArgs
        exit $LASTEXITCODE
    }

    "Core" {
        $dotnet = Find-Dotnet
        if (-not $dotnet) { Write-Error "dotnet executable not found."; exit 1 }
        & $dotnet test (Join-Path $repo "Tools\CoreTests\FamilyRacing.CoreTests.csproj") --no-restore --nologo
        exit $LASTEXITCODE
    }

    { $_ -in "Setup", "EditMode", "PlayMode", "Windows", "WindowsOrdinary",
        "VisualBenchmarkSetup", "WindowsVisualBenchmark", "WindowsVisualBenchmarkOrdinary",
        "FullContentSetup", "WindowsFullContent", "WindowsFullContentOrdinary",
        "WindowsFullContentPreview" } {
        $editorExe = Join-Path $EditorRoot "$PinnedEditorVersion\Editor\Unity.exe"
        if (-not (Test-Path $editorExe)) {
            Write-Error "Pinned editor $PinnedEditorVersion not found at $editorExe."
            exit 1
        }
        New-Item -ItemType Directory -Force -Path (Join-Path $repo "Logs") | Out-Null
        $lower = $Mode.ToLowerInvariant()
        $log = Join-Path $repo "Logs\verify-unity-$lower.log"
        $unityArgs = @("-batchmode", "-projectPath", $repo, "-logFile", $log)
        switch ($Mode) {
            "Setup" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationProjectSetup.Create", "-quit"
            }
            "Windows" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.Windows", "-quit"
            }
            "WindowsOrdinary" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.WindowsOrdinary", "-quit"
            }
            "VisualBenchmarkSetup" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.VisualBenchmarkSetup", "-quit"
            }
            "WindowsVisualBenchmark" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.WindowsVisualBenchmark", "-quit"
            }
            "WindowsVisualBenchmarkOrdinary" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.WindowsVisualBenchmarkOrdinary", "-quit"
            }
            "FullContentSetup" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.FullContentSetup", "-quit"
            }
            "WindowsFullContent" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.WindowsFullContent", "-quit"
            }
            "WindowsFullContentOrdinary" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.WindowsFullContentOrdinary", "-quit"
            }
            "WindowsFullContentPreview" {
                $unityArgs += "-executeMethod", "FamilyRacing.EditorTools.FoundationBuild.WindowsFullContentPreview", "-quit"
            }
            "EditMode" {
                $unityArgs += "-runTests", "-testPlatform", "EditMode",
                    "-testResults", (Join-Path $repo "Logs\verify-editmode-results.xml")
            }
            "PlayMode" {
                $unityArgs += "-runTests", "-testPlatform", "playmode",
                    "-testResults", (Join-Path $repo "Logs\verify-playmode-results.xml")
            }
        }
        Write-Host "Verify: Unity $Mode -> $log"
        & $editorExe @unityArgs
        exit $LASTEXITCODE
    }

    "CheckEnvironment" {
        $script:missing = 0
        function Report([bool]$ok, [string]$label, [string]$action) {
            if ($ok) {
                Write-Host "OK       $label"
            } else {
                Write-Host "MISSING  $label"
                Write-Host "         -> $action"
                $script:missing++
            }
        }

        $editorDir = Join-Path $EditorRoot $PinnedEditorVersion
        $editorExe = Join-Path $editorDir "Editor\Unity.exe"
        Report (Test-Path $editorExe) "Pinned editor $PinnedEditorVersion ($editorExe)" `
            "Install Unity $PinnedEditorVersion through Unity Hub, or pass -EditorRoot if it lives elsewhere."

        # Windows IL2CPP adds win64*il2cpp* build variations; a generic
        # Editor\Data\il2cpp directory exists in every editor install.
        $variations = Join-Path $editorDir "Editor\Data\PlaybackEngines\WindowsStandaloneSupport\Variations"
        $il2cpp = (Test-Path $variations) -and `
            [bool](Get-ChildItem $variations -Directory -Filter "win64*il2cpp*" -ErrorAction SilentlyContinue)
        Report $il2cpp "Windows Build Support (IL2CPP) for $PinnedEditorVersion" `
            "In Unity Hub, add the 'Windows Build Support (IL2CPP)' module to $PinnedEditorVersion."

        $androidPlayer = Join-Path $editorDir "Editor\Data\PlaybackEngines\AndroidPlayer"
        Report (Test-Path $androidPlayer) "Android Build Support for $PinnedEditorVersion" `
            "In Unity Hub, add the 'Android Build Support' module to $PinnedEditorVersion."
        Report (Test-Path (Join-Path $androidPlayer "SDK\platform-tools\adb.exe")) "Android SDK platform-tools (adb.exe) for $PinnedEditorVersion" `
            "In Unity Hub, reinstall 'Android Build Support' including the Android SDK & NDK tools for $PinnedEditorVersion."
        Report (Test-Path (Join-Path $androidPlayer "NDK\ndk-build.cmd")) "Android NDK (ndk-build.cmd) for $PinnedEditorVersion" `
            "In Unity Hub, reinstall 'Android Build Support' including the Android SDK & NDK tools for $PinnedEditorVersion."
        Report (Test-Path (Join-Path $androidPlayer "OpenJDK\bin\java.exe")) "Android OpenJDK (java.exe) for $PinnedEditorVersion" `
            "In Unity Hub, reinstall 'Android Build Support' including OpenJDK for $PinnedEditorVersion."

        # An executable alone is not proof of an SDK: require dotnet
        # --list-sdks to succeed and list a stable 10.x SDK.
        $dotnet = Find-Dotnet
        $sdkOk = $false
        if ($dotnet) {
            $sdkLines = & $dotnet --list-sdks 2>$null
            $sdkOk = ($LASTEXITCODE -eq 0) -and `
                [bool]($sdkLines | Where-Object { $_ -match '^10\.\d+\.\d+ \[' -and $_ -notmatch '-' })
        }
        Report $sdkOk ".NET 10 SDK (dotnet --list-sdks)" "Install the .NET 10 SDK."

        Write-Host "Note: -Mode Core needs a successful 'dotnet restore' (project.assets.json); -Mode CoreOffline does not."

        if ($script:missing -gt 0) {
            Write-Host "CheckEnvironment: $script:missing prerequisite(s) missing."
            exit 1
        }
        Write-Host "CheckEnvironment: checked file prerequisites found; Unity license, import, build, and device checks are still required."
        exit 0
    }
}
