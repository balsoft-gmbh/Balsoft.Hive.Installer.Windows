<#
.SYNOPSIS
  Makes a code signing certificate whose private key lives in a CNG key storage provider
  (for example Google Cloud KMS) usable by `dotnet nuget sign`.

.DESCRIPTION
  `dotnet nuget sign` finds its certificate by SHA-256 fingerprint in a Windows certificate
  store and needs the private key reachable through that store entry. A hardware or cloud
  backed key has no PFX, so this script imports the public certificate into
  CurrentUser\My and sets its key provider information (CERT_KEY_PROV_INFO_PROP_ID) to the
  CNG provider and key name. Signing then happens inside the provider; the key never
  leaves it. Run once per machine (idempotent).

.EXAMPLE
  ./eng/Register-SigningCertificate.ps1 -Certificate C:\keys\Company.cer `
    -Provider "Google Cloud KMS Provider" `
    -KeyName "projects/p/locations/l/keyRings/r/cryptoKeys/k/cryptoKeyVersions/1"
#>
param(
    [Parameter(Mandatory)] [string] $Certificate,
    [Parameter(Mandatory)] [string] $Provider,
    [Parameter(Mandatory)] [string] $KeyName
)
$ErrorActionPreference = 'Stop'

$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($Certificate)
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
$store.Open('ReadWrite')
try {
    if (-not ($store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint)) { $store.Add($cert) }
    $stored = $store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint | Select-Object -First 1

    Add-Type -Namespace HiveSigning -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct CRYPT_KEY_PROV_INFO {
    public string pwszContainerName;
    public string pwszProvName;
    public uint dwProvType;
    public uint dwFlags;
    public uint cProvParam;
    public IntPtr rgProvParam;
    public uint dwKeySpec;
}
[DllImport("crypt32.dll", SetLastError = true)]
public static extern bool CertSetCertificateContextProperty(IntPtr pCertContext, uint dwPropId, uint dwFlags, ref CRYPT_KEY_PROV_INFO pvData);
'@ -ErrorAction SilentlyContinue

    $info = New-Object HiveSigning.Native+CRYPT_KEY_PROV_INFO
    $info.pwszContainerName = $KeyName
    $info.pwszProvName = $Provider
    $info.dwProvType = 0            # 0 = CNG key storage provider
    $info.dwFlags = 0               # user key
    $info.dwKeySpec = 2           # AT_SIGNATURE: cloud KSPs such as Google Cloud KMS reject key spec 0
    $CERT_KEY_PROV_INFO_PROP_ID = 2
    if (-not [HiveSigning.Native]::CertSetCertificateContextProperty($stored.Handle, $CERT_KEY_PROV_INFO_PROP_ID, 0, [ref]$info)) {
        throw "CertSetCertificateContextProperty failed: $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)"
    }
}
finally { $store.Close() }

$sha256 = $cert.GetCertHashString('SHA256')
Write-Host "Registered $($cert.Subject)"
Write-Host "SHA-256 fingerprint: $sha256"
