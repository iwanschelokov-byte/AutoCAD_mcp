<#
.SYNOPSIS
    Authenticode-sign a built artifact.

.DESCRIPTION
    Resolves a signing certificate in this order:

      1. -PfxPath / -PfxPassword parameters
      2. AUTOCADMCP_PFX_BASE64 + AUTOCADMCP_PFX_PASSWORD environment variables
         (how CI supplies the real certificate)
      3. A local code-signing certificate whose subject matches -Subject
         (what build/setup-signing.ps1 creates for development)

    Unsigned plugins still load in AutoCAD; signing exists so SmartScreen and
    corporate policy stop flagging the installer. If no certificate can be found
    this exits non-zero rather than pretending it signed something.

.EXAMPLE
    .\build\sign.ps1 -Path .\autocad-plugin\dist\net48\AutoCADMCPPlugin.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,

    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$Subject = 'AutoCAD MCP Dev',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

function Resolve-SigningCert {
    if ($PfxPath) {
        if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
        $sec = if ($PfxPassword) { ConvertTo-SecureString $PfxPassword -AsPlainText -Force } else { $null }
        return Get-PfxCertificate -FilePath $PfxPath -Password $sec
    }

    if ($env:AUTOCADMCP_PFX_BASE64) {
        Write-Host '    Using certificate from AUTOCADMCP_PFX_BASE64'
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "autocadmcp-signing-$PID.pfx"
        try {
            [System.IO.File]::WriteAllBytes($tmp, [Convert]::FromBase64String($env:AUTOCADMCP_PFX_BASE64))
            $sec = if ($env:AUTOCADMCP_PFX_PASSWORD) {
                ConvertTo-SecureString $env:AUTOCADMCP_PFX_PASSWORD -AsPlainText -Force
            } else { $null }
            return Get-PfxCertificate -FilePath $tmp -Password $sec
        }
        finally {
            if (Test-Path $tmp) { Remove-Item $tmp -Force }
        }
    }

    $local = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
             Where-Object { $_.Subject -like "*$Subject*" } |
             Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($local) {
        Write-Host "    Using local development certificate: $($local.Subject)"
        return $local
    }

    throw "No signing certificate found. Run build\setup-signing.ps1 for a dev certificate, " +
          "or set AUTOCADMCP_PFX_BASE64 / AUTOCADMCP_PFX_PASSWORD."
}

$cert = Resolve-SigningCert
$failed = @()

foreach ($p in $Path) {
    if (-not (Test-Path $p)) {
        Write-Warning "  skip (not found): $p"
        continue
    }

    $result = Set-AuthenticodeSignature -FilePath $p -Certificate $cert `
                                        -TimestampServer $TimestampUrl `
                                        -HashAlgorithm SHA256 -ErrorAction Continue

    if ($result.Status -eq 'Valid') {
        Write-Host "    signed: $(Split-Path $p -Leaf)" -ForegroundColor Green
    } else {
        # A dev certificate that is not in a trusted root reports UnknownError;
        # the signature is still applied, which is all we need locally.
        Write-Warning "  $(Split-Path $p -Leaf): $($result.Status) - $($result.StatusMessage)"
        if ($result.SignerCertificate -eq $null) { $failed += $p }
    }
}

if ($failed.Count -gt 0) {
    throw "Signing failed for: $($failed -join ', ')"
}
