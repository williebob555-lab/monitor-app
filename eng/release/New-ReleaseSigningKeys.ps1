#Requires -Version 7.0
<#
One-time setup for preview/release signing. Run on YOUR machine (not in CI). It:
  1. generates the update-manifest signing key pair (ECDSA P-256),
  2. generates an Android release keystore with keytool, when keytool is available,
  3. writes everything to an output folder OUTSIDE the repository, owner-only,
  4. prints the exact `gh` commands to store them: secrets in the `release` environment, the two public values as repository variables.
It never uploads anything and never prints a private value. Review the printed commands, run them yourself, and then
move the output folder into your password manager / encrypted backup and delete the plain files.
Back up the keystore: if it is lost, installed Android builds can no longer be updated in place.

Usage:  pwsh eng/release/New-ReleaseSigningKeys.ps1 -Repository owner/repo [-OutputDirectory <dir outside the repo>]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')] [string] $Repository,
    [string] $OutputDirectory = (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'MonitorAppReleaseKeys'),
    [string] $KeyId = ('preview-' + (Get-Date -Format 'yyyy-MM')),
    [string] $AndroidKeyAlias = 'monitorapp',
    [string] $AndroidDistinguishedName = 'CN=Monitor App, O=Self hosted'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..')).TrimEnd('\', '/')
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if ($resolvedOutput.Equals($repositoryRoot, $comparison) -or
    $resolvedOutput.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, $comparison)) {
    throw "Refusing to write private keys inside the repository folder ($repositoryRoot). Choose another -OutputDirectory."
}
if ((Test-Path -LiteralPath $resolvedOutput) -and (Get-ChildItem -LiteralPath $resolvedOutput -Force | Select-Object -First 1)) {
    throw "$resolvedOutput is not empty; refusing to overwrite existing key material."
}
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
if ($IsWindows) {
    # Owner-only access: drop inheritance, grant the current user full control.
    & icacls.exe $resolvedOutput /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null
}
else {
    & chmod 700 $resolvedOutput
}

function Write-SecretFile([string] $Name, [string] $Value) {
    $path = Join-Path $resolvedOutput $Name
    [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false))
    if (-not $IsWindows) { & chmod 600 $path }
    return $path
}

# 1. Update-manifest signing key (same format the release tool and the node expect).
$ecdsa = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
$privateB64 = [Convert]::ToBase64String($ecdsa.ExportPkcs8PrivateKey())
$publicB64 = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
$ecdsa.Dispose()
$signingFile = Write-SecretFile 'MONITOR_UPDATE_SIGNING_KEY_PKCS8_BASE64.txt' $privateB64
$publicFile = Write-SecretFile 'MONITOR_UPDATE_PUBLIC_KEY_SPKI_BASE64.txt' $publicB64
Remove-Variable privateB64

$commands = [System.Collections.Generic.List[string]]::new()
$commands.Add("Get-Content -Raw -LiteralPath `"$signingFile`" | gh secret set MONITOR_UPDATE_SIGNING_KEY_PKCS8_BASE64 --env release --repo $Repository")
$commands.Add("gh variable set MONITOR_UPDATE_PUBLIC_KEY_SPKI_BASE64 --repo $Repository --body '$publicB64'")
$commands.Add("gh variable set MONITOR_UPDATE_SIGNING_KEY_ID --repo $Repository --body '$KeyId'")

# 2. Android keystore.
$keytool = Get-Command keytool -ErrorAction SilentlyContinue
if ($keytool) {
    $randomBytes = [byte[]]::new(24)
    [Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
    # Alphanumeric password: safe for keytool/MSBuild without quoting.
    $password = ([Convert]::ToBase64String($randomBytes) -replace '[^A-Za-z0-9]', 'x')
    $keystorePath = Join-Path $resolvedOutput 'monitorapp-release.jks'
    $env:MONITOR_KEYTOOL_PASSWORD = $password
    try {
        & $keytool.Source -genkeypair -keystore $keystorePath -alias $AndroidKeyAlias -keyalg RSA -keysize 4096 -validity 10000 `
            -dname $AndroidDistinguishedName -storepass:env MONITOR_KEYTOOL_PASSWORD -keypass:env MONITOR_KEYTOOL_PASSWORD
        if ($LASTEXITCODE -ne 0) { throw "keytool failed ($LASTEXITCODE)." }
    }
    finally {
        Remove-Item Env:\MONITOR_KEYTOOL_PASSWORD -ErrorAction SilentlyContinue
    }
    if (-not $IsWindows) { & chmod 600 $keystorePath }
    $keystoreB64File = Write-SecretFile 'ANDROID_KEYSTORE_BASE64.txt' ([Convert]::ToBase64String([IO.File]::ReadAllBytes($keystorePath)))
    $passwordFile = Write-SecretFile 'ANDROID_KEYSTORE_PASSWORD.txt' $password
    $keyPasswordFile = Write-SecretFile 'ANDROID_KEY_PASSWORD.txt' $password
    $aliasFile = Write-SecretFile 'ANDROID_KEY_ALIAS.txt' $AndroidKeyAlias
    $commands.Add("Get-Content -Raw -LiteralPath `"$keystoreB64File`" | gh secret set ANDROID_KEYSTORE_BASE64 --env release --repo $Repository")
    $commands.Add("Get-Content -Raw -LiteralPath `"$passwordFile`" | gh secret set ANDROID_KEYSTORE_PASSWORD --env release --repo $Repository")
    $commands.Add("Get-Content -Raw -LiteralPath `"$aliasFile`" | gh secret set ANDROID_KEY_ALIAS --env release --repo $Repository")
    $commands.Add("Get-Content -Raw -LiteralPath `"$keyPasswordFile`" | gh secret set ANDROID_KEY_PASSWORD --env release --repo $Repository")
}
else {
    Write-Warning 'keytool was not found (install a JDK, e.g. winget install Microsoft.OpenJDK.21) so no Android keystore was created. Re-run in a new folder after installing it; the update signing key above is already usable.'
}

Write-Host ''
Write-Host "Key material written to: $resolvedOutput (outside the repository)."
Write-Host 'Nothing was uploaded. Review, then run these commands yourself (gh must be logged in with admin rights on the repo,'
Write-Host 'and the `release` environment must already exist):'
Write-Host ''
$commands | ForEach-Object { Write-Host "  $_" }
Write-Host ''
Write-Host 'Afterwards: copy the folder to encrypted backup, then delete the plain files. Never commit them.'
