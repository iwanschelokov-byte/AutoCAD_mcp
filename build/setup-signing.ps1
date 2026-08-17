<#
.SYNOPSIS
    Create a self-signed code-signing certificate for local development.

.DESCRIPTION
    One-time setup so build-all.ps1 -Sign works on a developer machine without a
    real certificate.

    Trusting the certificate (-Trust) writes to the CurrentUser trusted-root
    store, which is a real trust decision - it makes anything signed with this
    key look trusted to *you*. It only affects this user account, and
    -Remove undoes it.

    This is NOT for distribution. Public releases must be signed with a real
    certificate supplied through AUTOCADMCP_PFX_BASE64 in CI.

.EXAMPLE
    .\build\setup-signing.ps1            # create the certificate
    .\build\setup-signing.ps1 -Trust     # create and trust it locally
    .\build\setup-signing.ps1 -Remove    # remove it again
#>
[CmdletBinding()]
param(
    [string]$Subject = 'AutoCAD MCP Dev',
    [int]$YearsValid = 3,
    [switch]$Trust,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$dn = "CN=$Subject"

function Get-DevCerts {
    @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
      Where-Object { $_.Subject -eq $dn }) +
    @(Get-ChildItem Cert:\CurrentUser\Root -ErrorAction SilentlyContinue |
      Where-Object { $_.Subject -eq $dn })
}

if ($Remove) {
    $found = Get-DevCerts
    if (-not $found) { Write-Host "No certificate matching '$dn' found."; return }
    foreach ($c in $found) {
        Write-Host "Removing $($c.Thumbprint) from $($c.PSParentPath.Split('::')[-1])"
        Remove-Item $c.PSPath -Force
    }
    Write-Host 'Done.' -ForegroundColor Green
    return
}

$existing = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $dn })
if ($existing) {
    Write-Host "Certificate already exists: $($existing[0].Thumbprint)" -ForegroundColor Yellow
    Write-Host 'Use -Remove first if you want to regenerate it.'
    $cert = $existing[0]
} else {
    Write-Host "Creating self-signed code-signing certificate '$dn'..."
    $cert = New-SelfSignedCertificate `
        -Subject $dn `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears($YearsValid)
    Write-Host "Created: $($cert.Thumbprint)" -ForegroundColor Green
}

if ($Trust) {
    $inRoot = Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
    if ($inRoot) {
        Write-Host 'Already trusted in CurrentUser\Root.'
    } else {
        Write-Host ''
        Write-Host 'About to add this certificate to your CurrentUser trusted roots.' -ForegroundColor Yellow
        Write-Host 'Anything signed with it will then appear trusted to this user account.' -ForegroundColor Yellow
        $answer = Read-Host 'Continue? (y/N)'
        if ($answer -notmatch '^(y|yes)$') {
            Write-Host 'Skipped trusting the certificate.'
        } else {
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store 'Root', 'CurrentUser'
            $store.Open('ReadWrite')
            $store.Add($cert)
            $store.Close()
            Write-Host 'Trusted in CurrentUser\Root.' -ForegroundColor Green
            Write-Host 'Undo with: .\build\setup-signing.ps1 -Remove' -ForegroundColor Gray
        }
    }
}

Write-Host ''
Write-Host 'Sign builds with:' -ForegroundColor Gray
Write-Host '  .\build\build-all.ps1 -Sign' -ForegroundColor Gray
