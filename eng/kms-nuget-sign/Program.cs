// kms-nuget-sign: author-sign a NuGet package with a certificate whose private key is held in
// Google Cloud KMS. Produces the same primary signature (RFC 3161 timestamped, signing-cert-v2
// and commitment-type attributes, intermediate certificate embedded) that `dotnet nuget sign`
// produces on Windows, without needing the certificate in a Windows store.
//
//   kms-nuget-sign --package X.nupkg --certificate BalsoftGmbH.cer \
//                  --key projects/p/locations/l/keyRings/r/cryptoKeys/k/cryptoKeyVersions/1 \
//                  [--timestamper http://timestamp.digicert.com] [--output signed/X.nupkg]
//
// Authentication: Google application default credentials (gcloud auth application-default login),
// the account needs roles/cloudkms.signer on the key.

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using NuGet.Common;
using NuGet.Packaging.Signing;
using CryptoHashAlgorithmName = System.Security.Cryptography.HashAlgorithmName;

var arguments = ParseArguments(args);
var packagePath = Required(arguments, "package");
var certificatePath = Required(arguments, "certificate");
var keyName = CryptoKeyVersionName.Parse(Required(arguments, "key"));
var timestamper = new Uri(arguments.GetValueOrDefault("timestamper", "http://timestamp.digicert.com"));
var outputPath = arguments.GetValueOrDefault("output", packagePath);

var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
var kms = await KeyManagementServiceClient.CreateAsync();
using var publicKey = certificate.GetRSAPublicKey() ?? throw new InvalidOperationException("The certificate has no RSA public key.");
using var kmsKey = new KmsRsa(kms, keyName, publicKey);

Console.WriteLine($"Signing {Path.GetFileName(packagePath)} as {certificate.Subject} (SHA-256 {Convert.ToHexString(certificate.GetCertHash(CryptoHashAlgorithmName.SHA256))})");

var intermediates = await DownloadIssuerChainAsync(certificate);
var provider = new KmsSignatureProvider(kmsKey, intermediates, timestamper);
var request = new AuthorSignPackageRequest(certificate, NuGet.Common.HashAlgorithmName.SHA256);

var tempOutput = outputPath == packagePath ? packagePath + ".signed" : outputPath;
var options = SigningOptions.CreateFromFilePaths(packagePath, tempOutput, overwrite: true, provider, NullLogger.Instance);
await SigningUtility.SignAsync(options, request, CancellationToken.None);
options.Dispose();
if (tempOutput != outputPath)
{
    File.Move(tempOutput, outputPath, overwrite: true);
}

Console.WriteLine($"Signed and timestamped: {outputPath}");
return 0;

static string Required(Dictionary<string, string> arguments, string name) =>
    arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"--{name} is required.");

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }

        result[args[i][2..]] = args[i + 1];
    }

    return result;
}

/// <summary>Follows the certificate's Authority Information Access URLs up to the root so the signature carries the chain.</summary>
static async Task<X509Certificate2Collection> DownloadIssuerChainAsync(X509Certificate2 leaf)
{
    var collection = new X509Certificate2Collection();
    using var http = new HttpClient();
    var current = leaf;
    for (var depth = 0; depth < 5; depth++)
    {
        var aia = current.Extensions.OfType<X509AuthorityInformationAccessExtension>().FirstOrDefault();
        var issuerUrl = aia?.EnumerateCAIssuersUris().FirstOrDefault();
        if (issuerUrl is null)
        {
            break;
        }

        var bytes = await http.GetByteArrayAsync(issuerUrl);
        var issuer = LoadIssuer(bytes);
        if (issuer is null)
        {
            Console.WriteLine($"Chain: could not parse {issuerUrl}, stopping here (the chain is completed by the verifier)");
            break;
        }

        collection.Add(issuer);
        Console.WriteLine($"Chain: {issuer.Subject}");
        if (issuer.Subject == issuer.Issuer)
        {
            break;
        }

        current = issuer;
    }

    return collection;
}

/// <summary>CA Issuers URLs serve either a bare DER certificate or a PKCS#7 bundle; accept both.</summary>
static X509Certificate2? LoadIssuer(byte[] bytes)
{
    try
    {
        return X509CertificateLoader.LoadCertificate(bytes);
    }
    catch (CryptographicException)
    {
        try
        {
            var cms = new SignedCms();
            cms.Decode(bytes);
            return cms.Certificates.Count > 0 ? cms.Certificates[0] : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>RSA whose private operations run inside Google Cloud KMS. Only PKCS#1 v1.5 with SHA-256, which is what the key is configured for.</summary>
sealed class KmsRsa : RSA
{
    private readonly KeyManagementServiceClient _client;
    private readonly CryptoKeyVersionName _key;
    private readonly RSA _publicKey;

    public KmsRsa(KeyManagementServiceClient client, CryptoKeyVersionName key, RSA publicKey)
    {
        _client = client;
        _key = key;
        _publicKey = publicKey;
        KeySizeValue = publicKey.KeySize;
    }

    public override RSAParameters ExportParameters(bool includePrivateParameters) =>
        includePrivateParameters
            ? throw new CryptographicException("The private key is held in Google Cloud KMS and cannot be exported.")
            : _publicKey.ExportParameters(false);

    public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();

    public override byte[] SignHash(byte[] hash, CryptoHashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (hashAlgorithm != CryptoHashAlgorithmName.SHA256 || padding != RSASignaturePadding.Pkcs1)
        {
            throw new NotSupportedException($"The KMS key signs SHA-256 with PKCS#1 v1.5 only (requested {hashAlgorithm.Name} / {padding.Mode}).");
        }

        var response = _client.AsymmetricSign(new AsymmetricSignRequest
        {
            CryptoKeyVersionName = _key,
            Digest = new Digest { Sha256 = ByteString.CopyFrom(hash) },
        });
        return response.Signature.ToByteArray();
    }

    public override bool VerifyHash(byte[] hash, byte[] signature, CryptoHashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        _publicKey.VerifyHash(hash, signature, hashAlgorithm, padding);

    protected override byte[] HashData(byte[] data, int offset, int count, CryptoHashAlgorithmName hashAlgorithm)
    {
        using var hash = IncrementalHash.CreateHash(hashAlgorithm);
        hash.AppendData(data, offset, count);
        return hash.GetHashAndReset();
    }

    protected override byte[] HashData(Stream data, CryptoHashAlgorithmName hashAlgorithm)
    {
        using var stream = new MemoryStream();
        data.CopyTo(stream);
        return HashData(stream.ToArray(), 0, (int)stream.Length, hashAlgorithm);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _publicKey.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Builds the NuGet primary (author) signature with the KMS-backed key and timestamps it (RFC 3161, own HTTP round trip).</summary>
sealed class KmsSignatureProvider : ISignatureProvider
{
    private static readonly Oid TimeStampTokenAttribute = new("1.2.840.113549.1.9.16.2.14"); // id-smime-aa-timeStampToken
    private readonly RSA _privateKey;
    private readonly X509Certificate2Collection _chain;
    private readonly Uri _timestamper;

    public KmsSignatureProvider(RSA privateKey, X509Certificate2Collection chain, Uri timestamper)
    {
        _privateKey = privateKey;
        _chain = chain;
        _timestamper = timestamper;
    }

    public async Task<PrimarySignature> CreatePrimarySignatureAsync(SignPackageRequest request, SignatureContent signatureContent, ILogger logger, CancellationToken token)
    {
        var signer = new CmsSigner(SubjectIdentifierType.SubjectKeyIdentifier, request.Certificate, _privateKey)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.Certificates.AddRange(_chain);
        signer.SignedAttributes.Add(new Pkcs9SigningTime());
        signer.SignedAttributes.Add(AttributeUtility.CreateCommitmentTypeIndication(request.SignatureType));
        signer.SignedAttributes.Add(AttributeUtility.CreateSigningCertificateV2(request.Certificate, request.SignatureHashAlgorithm));

        var cms = new SignedCms(new ContentInfo(signatureContent.GetBytes()), detached: false);
        cms.ComputeSignature(signer);

        var signerInfo = cms.SignerInfos[0];
        var nonce = RandomNumberGenerator.GetBytes(16);
        nonce[0] &= 0x7F; // keep the INTEGER positive
        var tsRequest = Rfc3161TimestampRequest.CreateFromSignerInfo(signerInfo, CryptoHashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: nonce);

        using var http = new HttpClient();
        using var content = new ByteArrayContent(tsRequest.Encode());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        using var response = await http.PostAsync(_timestamper, content, token);
        response.EnsureSuccessStatusCode();
        var tsToken = tsRequest.ProcessResponse(await response.Content.ReadAsByteArrayAsync(token), out _);
        Console.WriteLine($"Timestamp: {tsToken.TokenInfo.Timestamp:u} by {tsToken.AsSignedCms().Certificates[0].Subject}");

        signerInfo.AddUnsignedAttribute(new AsnEncodedData(TimeStampTokenAttribute, tsToken.AsSignedCms().Encode()));
        return PrimarySignature.Load(cms.Encode());
    }

    public Task<PrimarySignature> CreateRepositoryCountersignatureAsync(RepositorySignPackageRequest request, PrimarySignature primarySignature, ILogger logger, CancellationToken token) =>
        throw new NotSupportedException("Only author signatures are produced.");
}
