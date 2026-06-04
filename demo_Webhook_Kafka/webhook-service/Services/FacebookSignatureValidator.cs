using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using webhook_service.Models;

namespace webhook_service.Services;

public sealed class FacebookSignatureValidator : IFacebookSignatureValidator
{
    private readonly string _appSecret;
    private const string Sha256Prefix = "sha256=";
    private const string Sha1Prefix = "sha1=";

    public FacebookSignatureValidator(IOptions<FacebookOptions> options)
    {
        _appSecret = options.Value.AppSecret;
    }

    public bool IsValidSignature(IHeaderDictionary headers, string payload)
    {
        if (string.IsNullOrWhiteSpace(_appSecret))
        {
            return false;
        }

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var keyBytes = Encoding.UTF8.GetBytes(_appSecret);

        var signature256 = headers["X-Hub-Signature-256"].ToString();
        if (TryValidateSignature(payloadBytes, keyBytes, signature256, Sha256Prefix, HashAlgorithmName.SHA256))
        {
            return true;
        }

        var signatureLegacy = headers["X-Hub-Signature"].ToString();
        return TryValidateSignature(payloadBytes, keyBytes, signatureLegacy, Sha1Prefix, HashAlgorithmName.SHA1);
    }

    private static bool TryValidateSignature(
        byte[] payloadBytes,
        byte[] keyBytes,
        string signatureHeader,
        string prefix,
        HashAlgorithmName algorithm)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || !signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var incomingHash = signatureHeader[prefix.Length..].Trim();
        if (incomingHash.Length == 0)
        {
            return false;
        }

        using HMAC hmac = algorithm == HashAlgorithmName.SHA256
            ? new HMACSHA256(keyBytes)
            : new HMACSHA1(keyBytes);

        var hash = hmac.ComputeHash(payloadBytes);
        var expectedHash = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedHash),
            Encoding.UTF8.GetBytes(incomingHash.ToLowerInvariant()));
    }
}