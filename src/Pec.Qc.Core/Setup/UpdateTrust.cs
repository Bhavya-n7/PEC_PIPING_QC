using System.Security.Cryptography;
using System.Text;

namespace Pec.Qc.Core.Setup;

/// <summary>
/// Who may publish an update. The update folder is on a share that many people can write to, and Setup is an executable that every PC would run, so an update is
/// only accepted when <c>latest.json</c> carries a valid signature (<c>latest.json.sig</c>, RSA-PSS with SHA-256) made with the release manager's private key, which is
/// never on the share. Every PC holds only the PUBLIC key (from office-defaults.json, put there by the release build). latest.json names the SHA-256 of Setup and of
/// payload.zip, and both files are checked against it before Setup is started. Without a public key the old behaviour remains (no check) and the app says so.
/// </summary>
public static class UpdateTrust
{
    public const string SignatureFile = "latest.json.sig";

    public static string Sha256Hex(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>True when the signature is a valid RSA-PSS SHA-256 signature of the bytes by the key (XML form of the public key). Anything malformed is false, never an exception.</summary>
    public static bool Verify(byte[] data, string signatureBase64, string publicKeyXml)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(publicKeyXml);
            return rsa.VerifyData(data, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException or System.Xml.XmlException) { return false; }
    }

    /// <summary>For the release manager's tools and the tests: a new key pair (private XML, public XML).</summary>
    public static (string PrivateXml, string PublicXml) NewKeyPair()
    {
        using var rsa = RSA.Create(3072);
        return (rsa.ToXmlString(true), rsa.ToXmlString(false));
    }

    public static string Sign(byte[] data, string privateKeyXml)
    {
        using var rsa = RSA.Create();
        rsa.FromXmlString(privateKeyXml);
        return Convert.ToBase64String(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    /// <summary>
    /// Checks the files that were copied for an update against the (verified) release file. With a public key configured a missing hash is a failure; without one, a
    /// hash that is present is still checked (it catches a damaged copy) and a missing one is accepted.
    /// </summary>
    public static string? CheckFiles(ReleaseInfo release, string setupFile, string payloadFile, bool keyConfigured)
    {
        string? One(string what, string file, string? expected)
        {
            if (string.IsNullOrWhiteSpace(expected)) return keyConfigured ? $"The release does not state the SHA-256 of {what}: the update was refused." : null;
            return string.Equals(Sha256Hex(file), expected.Trim(), StringComparison.OrdinalIgnoreCase) ? null : $"{what} does not match the published SHA-256: the update was refused (the file was changed or damaged).";
        }
        return One("PecQcSetup.exe", setupFile, release.SetupSha256) ?? One("payload.zip", payloadFile, release.PayloadSha256);
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
