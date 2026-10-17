# Signs one unsigned Android release APK with the family release key and
# verifies the result before publishing.
#
# Pipeline: validate input -> optional aapt metadata inspection ->
# zipalign -> apksigner sign -> apksigner verify --verbose --print-certs
# -> atomic publish. Any failure leaves -OutputApk untouched: signing
# stages into a sibling temp directory and the finished APK replaces the
# destination only after every check passes.
#
# Credentials arrive ONLY through the process environment:
#   FAMILY_RACING_KEYSTORE           path to the release keystore
#   FAMILY_RACING_KEY_ALIAS          key alias inside it
#   FAMILY_RACING_KEYSTORE_PASSWORD  keystore password
#   FAMILY_RACING_KEY_PASSWORD       key password
# They are forwarded to apksigner as `env:` references, so secret values
# never appear on a command line, in a file, or in this script's output.
# Logs carry public metadata only (paths, certificate fingerprints,
# package identity).
param(
    [Parameter(Mandatory = $true)]
    [string]$UnsignedApk,
    [Parameter(Mandatory = $true)]
    [string]$OutputApk,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedCertificateSha256,
    [string]$BuildToolsDir = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\build-tools\36.0.0',
    [string]$JavaExe = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin\java.exe',
    # Optional release-identity expectations; when any is supplied the
    # APK's aapt badging must match all of them and must not be
    # debuggable. The release wrapper always passes release.json values.
    [string]$ExpectedApplicationId = '',
    [string]$ExpectedVersionName = '',
    [int]$ExpectedVersionCode = 0,
    [int]$ExpectedMinSdk = 0,
    [int]$ExpectedTargetSdk = 0,
    # Forwarded to apksigner as --min-sdk-version so signing never depends
    # on manifest re-parsing; the release wrapper passes release.json's
    # androidMinSdkVersion.
    [int]$ApkMinSdkVersion = 0,
    [switch]$RequireArm64
)

$ErrorActionPreference = 'Stop'

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description missing: $Path"
    }
}

function Quote-NativeArgument([string]$Value) {
    # Windows CRT argument quoting: double backslashes before a quote and
    # before the closing quote. No shell interprets these arguments.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

# Runs one external tool and returns @{ Code; Text } — Text is stdout +
# stderr. Tool arguments are built from public values and env: name
# references only, so captured text never carries a secret.
function Invoke-SignTool([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = ($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' '
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $child = New-Object System.Diagnostics.Process
    $child.StartInfo = $start
    try {
        if (-not $child.Start()) { throw "Process did not start: $Executable" }
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        $child.WaitForExit()
        return @{ Code = $child.ExitCode; Text = $stdout.Result + "`r`n" + $stderr.Result }
    }
    finally { $child.Dispose() }
}

# Reads the ZIP entry list of the unsigned APK without extracting.
function Get-ApkEntries([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = $null
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        return @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        if ($null -ne $zip) { $zip.Dispose() }
    }
}

$exitCode = 1
$staging = $null
try {
    # --- Credentials: environment only, named on failure. ---------------
    $requiredEnv = @('FAMILY_RACING_KEYSTORE', 'FAMILY_RACING_KEY_ALIAS',
        'FAMILY_RACING_KEYSTORE_PASSWORD', 'FAMILY_RACING_KEY_PASSWORD')
    $missingEnv = @($requiredEnv | Where-Object {
        [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_)) })
    if ($missingEnv.Count -gt 0) {
        throw ("Missing signing environment variable(s): " + ($missingEnv -join ', ') +
            ". Set them in the process environment only; passwords never appear on a command line or in a file.")
    }
    $keystore = $env:FAMILY_RACING_KEYSTORE
    $keyAlias = $env:FAMILY_RACING_KEY_ALIAS
    Require-File $keystore 'Release keystore from FAMILY_RACING_KEYSTORE'

    # --- Input APK --------------------------------------------------------
    if (-not (Test-Path -LiteralPath $UnsignedApk -PathType Leaf)) {
        throw "Unsigned APK missing: $UnsignedApk"
    }
    $unsignedFull = (Resolve-Path -LiteralPath $UnsignedApk).Path
    if ((Get-Item -LiteralPath $unsignedFull).Length -eq 0) {
        throw "Unsigned APK is empty: $unsignedFull"
    }
    $outputFull = [IO.Path]::GetFullPath($OutputApk)
    if ($outputFull -eq $unsignedFull) {
        throw "OutputApk must differ from UnsignedApk."
    }
    $entries = $null
    try { $entries = Get-ApkEntries $unsignedFull }
    catch { throw "Unsigned APK is not a readable ZIP/APK: $unsignedFull ($($_.Exception.Message))" }
    # A signature block in the input means the export was already signed
    # (for example a debug-key fallback); release signing only ever sees
    # unsigned Gradle release output.
    $signatureEntries = @($entries | Where-Object { $_ -match '^META-INF/[^/]+\.(SF|RSA|DSA|EC)$' })
    if ($signatureEntries.Count -gt 0) {
        throw "Unsigned APK is already signed (found $($signatureEntries[0])); refusing a pre-signed or debug-key artifact."
    }
    if ($RequireArm64 -and @($entries | Where-Object { $_ -like 'lib/arm64-v8a/*' }).Count -eq 0) {
        throw "Unsigned APK carries no lib/arm64-v8a payload; the family release is ARM64-only."
    }

    # --- Toolchain --------------------------------------------------------
    $zipalign = Join-Path $BuildToolsDir 'zipalign.exe'
    $aapt = Join-Path $BuildToolsDir 'aapt.exe'
    $apksignerJar = Join-Path $BuildToolsDir 'lib\apksigner.jar'
    Require-File $zipalign 'Bundled zipalign'
    Require-File $apksignerJar 'Bundled apksigner'
    Require-File $JavaExe 'Bundled OpenJDK java'

    # --- Metadata inspection (before signing) ------------------------------
    $metadataExpected = ($ExpectedApplicationId -ne '' -or $ExpectedVersionName -ne '' -or
        $ExpectedVersionCode -gt 0 -or $ExpectedMinSdk -gt 0 -or $ExpectedTargetSdk -gt 0)
    if ($metadataExpected) {
        Require-File $aapt 'Bundled aapt (required for metadata verification)'
        $badgingRun = Invoke-SignTool $aapt @('dump', 'badging', $unsignedFull) (Split-Path $unsignedFull)
        if ($badgingRun.Code -ne 0) {
            Write-Host $badgingRun.Text.TrimEnd()
            throw "aapt dump badging exited $($badgingRun.Code) for $unsignedFull"
        }
        $badging = $badgingRun.Text
        if ($badging -match 'application-debuggable') {
            throw "APK manifest is debuggable (android:debuggable=true); a release package can never carry a debug flag."
        }
        $package = [regex]::Match($badging, "package: name='([^']+)' versionCode='(\d+)' versionName='([^']*)'")
        if (-not $package.Success) { throw "aapt badging did not report package identity for $unsignedFull" }
        if ($ExpectedApplicationId -ne '' -and $package.Groups[1].Value -ne $ExpectedApplicationId) {
            throw "applicationId '$($package.Groups[1].Value)' does not match expected '$ExpectedApplicationId'."
        }
        if ($ExpectedVersionCode -gt 0 -and [int]$package.Groups[2].Value -ne $ExpectedVersionCode) {
            throw "versionCode $($package.Groups[2].Value) does not match expected $ExpectedVersionCode."
        }
        if ($ExpectedVersionName -ne '' -and $package.Groups[3].Value -ne $ExpectedVersionName) {
            throw "versionName '$($package.Groups[3].Value)' does not match expected '$ExpectedVersionName'."
        }
        $minSdk = [regex]::Match($badging, "sdkVersion:'(\d+)'")
        $targetSdk = [regex]::Match($badging, "targetSdkVersion:'(\d+)'")
        if ($ExpectedMinSdk -gt 0 -and (-not $minSdk.Success -or [int]$minSdk.Groups[1].Value -ne $ExpectedMinSdk)) {
            throw "minSdk '$($minSdk.Groups[1].Value)' does not match expected $ExpectedMinSdk."
        }
        if ($ExpectedTargetSdk -gt 0 -and (-not $targetSdk.Success -or [int]$targetSdk.Groups[1].Value -ne $ExpectedTargetSdk)) {
            throw "targetSdk '$($targetSdk.Groups[1].Value)' does not match expected $ExpectedTargetSdk."
        }
        Write-Host "Sign: metadata matches $($package.Groups[1].Value) $($package.Groups[3].Value) ($($package.Groups[2].Value)), minSdk $($minSdk.Groups[1].Value), targetSdk $($targetSdk.Groups[1].Value), debuggable=false"
    }

    # --- Expected signer fingerprint ---------------------------------------
    $expectedDigest = ($ExpectedCertificateSha256 -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
    if ($expectedDigest.Length -ne 64) {
        throw "ExpectedCertificateSha256 must be a 64-hex-digit fingerprint."
    }

    # --- Stage -> align -> sign -> verify -----------------------------------
    $outputDir = Split-Path $outputFull -Parent
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    $staging = Join-Path $outputDir ('.staged-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $alignedApk = Join-Path $staging 'aligned.apk'
    $signedApk = Join-Path $staging 'signed.apk'

    Write-Host "Sign: zipalign -> staging"
    $aligned = Invoke-SignTool $zipalign @('-f', '-p', '4', $unsignedFull, $alignedApk) $staging
    if ($aligned.Text.Trim().Length -gt 0) { Write-Host $aligned.Text.TrimEnd() }
    if ($aligned.Code -ne 0) { throw "zipalign exited $($aligned.Code)." }
    Require-File $alignedApk 'Aligned APK from zipalign'

    Write-Host "Sign: apksigner sign (keystore $keystore, alias $keyAlias)"
    $signArgs = @('-jar', $apksignerJar, 'sign',
        '--ks', $keystore,
        '--ks-key-alias', $keyAlias,
        '--ks-pass', 'env:FAMILY_RACING_KEYSTORE_PASSWORD',
        '--key-pass', 'env:FAMILY_RACING_KEY_PASSWORD')
    if ($ApkMinSdkVersion -gt 0) { $signArgs += @('--min-sdk-version', [string]$ApkMinSdkVersion) }
    $signArgs += @('--out', $signedApk, $alignedApk)
    $signRun = Invoke-SignTool $JavaExe $signArgs $staging
    if ($signRun.Text.Trim().Length -gt 0) { Write-Host $signRun.Text.TrimEnd() }
    if ($signRun.Code -ne 0) { throw "apksigner sign exited $($signRun.Code)." }
    Require-File $signedApk 'Signed APK from apksigner'

    Write-Host "Sign: apksigner verify --verbose --print-certs"
    $verifyArgs = @('-jar', $apksignerJar, 'verify', '--verbose', '--print-certs')
    if ($ApkMinSdkVersion -gt 0) { $verifyArgs += @('--min-sdk-version', [string]$ApkMinSdkVersion) }
    $verifyArgs += $signedApk
    $verifyRun = Invoke-SignTool $JavaExe $verifyArgs $staging
    if ($verifyRun.Text.Trim().Length -gt 0) { Write-Host $verifyRun.Text.TrimEnd() }
    if ($verifyRun.Code -ne 0) { throw "apksigner verify exited $($verifyRun.Code)." }
    $scheme = [regex]::Match($verifyRun.Text, 'Verified using v[23] scheme .*: true')
    if (-not $scheme.Success) {
        throw "apksigner verify did not confirm a v2/v3 signature; a v1-only release signature is not accepted."
    }
    $digest = [regex]::Match($verifyRun.Text, 'Signer #1 certificate SHA-256 digest:\s*([0-9a-fA-F]+)')
    if (-not $digest.Success) {
        throw "apksigner verify did not report a signer certificate SHA-256 digest."
    }
    $actualDigest = $digest.Groups[1].Value.ToLowerInvariant()
    if ($actualDigest -ne $expectedDigest) {
        throw "Signed APK certificate SHA-256 $actualDigest does not match expected $expectedDigest; refusing to publish an artifact signed by the wrong key."
    }

    # --- Atomic publish ------------------------------------------------------
    # Same-directory staging makes the final move a same-volume rename:
    # the previous package is replaced only after every check passed.
    Move-Item -LiteralPath $signedApk -Destination $outputFull -Force
    $apkHash = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Sign: published $outputFull"
    Write-Host "Sign: sha256=$apkHash signer-cert-sha256=$actualDigest"
    $exitCode = 0
}
catch {
    Write-Host "Sign failed: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
