<#
.SYNOPSIS
  Creates the self-signed code-signing certificate ToneSnip's sideload MSIX is signed with.

.DESCRIPTION
  Run this once, on the machine that holds the release key. It creates a code-signing certificate whose
  subject matches the MSIX manifest's Publisher ("CN=merson316" -- they must be identical or the package
  will not install), then exports two files next to this script:

    tonesnip.pfx   the private key. NEVER commit it. Base64-encode it into the MSIX_PFX_BASE64 repository
                   secret and put the password into MSIX_PFX_PASSWORD:
                       [Convert]::ToBase64String([IO.File]::ReadAllBytes("tonesnip.pfx")) | Set-Clipboard
    tonesnip.cer   the public certificate. Commit it (packaging/tonesnip.cer) and attach it to releases;
                   users install it into Local Machine \ Trusted People before the first MSIX install.

  The certificate is also left in Cert:\CurrentUser\My. Note its thumbprint if you want to sign locally
  with signtool /sha1 instead of a PFX.

.PARAMETER Subject
  Certificate subject. Must equal the Publisher in packaging/Package.appxmanifest.

.PARAMETER Years
  Validity in years (default 3). Packages signed by an expired certificate still install as long as the
  signature carries a timestamp, but a fresh certificate needs re-distributing, so do not make this short.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\make-cert.ps1
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=merson316',
    [int]$Years = 3,
    [string]$OutDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$pfxPath = Join-Path $OutDir 'tonesnip.pfx'
$cerPath = Join-Path $OutDir 'tonesnip.cer'
foreach ($p in @($pfxPath, $cerPath)) {
    if (Test-Path $p) { throw "$p already exists; move it aside first (a new certificate invalidates the old one for users)." }
}

Write-Host "Creating a self-signed code-signing certificate for $Subject ..."
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -FriendlyName 'ToneSnip sideload' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears($Years) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')   # EKU: Code Signing

Write-Host "Thumbprint: $($cert.Thumbprint)"

$password = Read-Host -AsSecureString -Prompt 'Password for tonesnip.pfx (this becomes the MSIX_PFX_PASSWORD secret)'
if ($password.Length -eq 0) { throw 'An empty password is not accepted; the PFX must be protected.' }

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $password | Out-Null
Export-Certificate  -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

Write-Host ''
Write-Host "Wrote $pfxPath   (private key -- keep out of git; base64 it into the MSIX_PFX_BASE64 secret)"
Write-Host "Wrote $cerPath   (public cert -- commit as packaging/tonesnip.cer and attach to releases)"
Write-Host ''
Write-Host 'Users install the .cer once, from an elevated prompt:'
Write-Host '    certutil -addstore TrustedPeople tonesnip.cer'
