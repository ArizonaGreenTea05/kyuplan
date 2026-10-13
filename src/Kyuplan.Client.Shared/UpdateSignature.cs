using System.Security.Cryptography;

namespace Kyuplan.Client;

/// <summary>
/// The proof that an installer comes from the publisher of the program and not from a server that has been taken over: the release signs the
/// installer with a private key (ECDSA P-256, SHA-256, DER signature, e.g. <c>openssl dgst -sha256 -sign</c>) and the program carries the public key.
/// It needs no certificate authority, so it works for software that cannot be Authenticode-signed.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Whether <paramref name="signaturePath"/> holds a signature of the installer's bytes made with the private key of <paramref name="publicKeyPem"/>.</summary>
    public static bool IsValid(string installerPath, string signaturePath, string publicKeyPem)
    {
        try
        {
            if (!File.Exists(installerPath) || !File.Exists(signaturePath)) return false;

            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            using var installer = File.OpenRead(installerPath);
            return key.VerifyData(installer, File.ReadAllBytes(signaturePath), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or ArgumentException)
        {
            return false;
        }
    }
}
