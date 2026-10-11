// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PinentryForWindows.Broker;

internal sealed class BrokerIdentity(string directory) {
  private string PrivatePath => Path.Combine(directory, "broker.key.dpapi");
  private string PublicPath => Path.Combine(directory, "broker.cer");
  private string ClientsPath => Path.Combine(directory, "clients");

  public string PublicCertificatePath => PublicPath;

  public X509Certificate2 GetOrCreateServerCertificate() {
    Directory.CreateDirectory(directory);
    if (File.Exists(PrivatePath) != File.Exists(PublicPath)) {
      throw new InvalidDataException("Broker identity files are incomplete.");
    }

    if (!File.Exists(PrivatePath)) {
      using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
      var request = new CertificateRequest("CN=pfw-broker", key, HashAlgorithmName.SHA256);
      request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
      request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

      using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddYears(1));
      var pkcs12 = certificate.Export(X509ContentType.Pkcs12, string.Empty);

      try {
        var protectedBytes = ProtectedData.Protect(pkcs12, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PrivatePath, protectedBytes);
        File.WriteAllBytes(PublicPath, certificate.Export(X509ContentType.Cert));
      }
      finally { CryptographicOperations.ZeroMemory(pkcs12); }
    }

    var encrypted = File.ReadAllBytes(PrivatePath);
    var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
    try {
      // Schannel cannot use an ephemeral imported private key for TLS on Windows.
      // Without PersistKeySet, the temporary user key is removed with the certificate handle.
      var certificate = X509CertificateLoader.LoadPkcs12(clear, string.Empty,
        X509KeyStorageFlags.UserKeySet);

      if (certificate.HasPrivateKey) {
        return certificate;
      }

      certificate.Dispose();
      throw new InvalidDataException("Broker certificate has no private key.");
    }
    finally { CryptographicOperations.ZeroMemory(clear); }
  }

  public void ApproveClient(string certificatePath, string origin) {
    if (origin.Length is < 5 or > 80 ||
        !origin.StartsWith("wsl:", StringComparison.Ordinal) ||
        origin.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (':' or '-' or '_' or '.'))) {
      throw new ArgumentException("Invalid origin identifier.", nameof(origin));
    }

    using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
    CheckValidity(certificate);
    Directory.CreateDirectory(ClientsPath);
    var fingerprint = Fingerprint(certificate);
    var publicPath = Path.Combine(ClientsPath, fingerprint + ".cer");
    var originPath = Path.Combine(ClientsPath, fingerprint + ".origin");

    if (File.Exists(publicPath) ||
        File.Exists(originPath)) {
      throw new InvalidOperationException("Client is already paired; revoke it before replacing.");
    }

    File.WriteAllBytes(publicPath, certificate.Export(X509ContentType.Cert));
    File.WriteAllText(originPath, origin, Encoding.UTF8);
  }

  public string? OriginFor(X509Certificate2 certificate) {
    var fingerprint = Fingerprint(certificate);
    var publicPath = Path.Combine(ClientsPath, fingerprint + ".cer");
    var originPath = Path.Combine(ClientsPath, fingerprint + ".origin");

    if (!File.Exists(publicPath) ||
        !File.Exists(originPath)) {
      return null;
    }

    try {
      CheckValidity(certificate);
      var pinned = File.ReadAllBytes(publicPath);
      if (!CryptographicOperations.FixedTimeEquals(pinned, certificate.RawData)) {
        return null;
      }

      var origin = File.ReadAllText(originPath, Encoding.UTF8);
      return origin.Length is >= 5 and <= 80 && origin.StartsWith("wsl:", StringComparison.Ordinal)
        ? origin + "#" + fingerprint
        : null;
    }
    catch (Exception error) when (error is IOException or CryptographicException or InvalidDataException) {
      return null;
    }
  }

  public void RevokeClient(X509Certificate2 certificate) {
    var fingerprint = Fingerprint(certificate);
    var publicPath = Path.Combine(ClientsPath, fingerprint + ".cer");
    var originPath = Path.Combine(ClientsPath, fingerprint + ".origin");

    if (File.Exists(originPath)) {
      File.Delete(originPath);
    }

    if (File.Exists(publicPath)) {
      File.Delete(publicPath);
    }
  }

  public IReadOnlyList<string> PairedDistributions() {
    if (!Directory.Exists(ClientsPath)) {
      return [];
    }

    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in Directory.EnumerateFiles(ClientsPath, "*.origin")) {
      if (!File.Exists(Path.ChangeExtension(file, ".cer"))) {
        continue;
      }

      try {
        var origin = File.ReadAllText(file, Encoding.UTF8);
        if (origin.StartsWith("wsl:", StringComparison.Ordinal) &&
            origin.Length is >= 5 and <= 80) {
          names.Add(origin[4..]);
        }
      }
      catch (IOException) {
        /* Ignore a concurrently revoked pairing. */
      }
    }

    return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
  }

  public void Remove() {
    if (Directory.Exists(ClientsPath)) {
      foreach (var originPath in Directory.EnumerateFiles(ClientsPath, "*.origin")) {
        var fingerprint = Path.GetFileNameWithoutExtension(originPath);

        if (fingerprint.Length != 64 ||
            !fingerprint.All(Uri.IsHexDigit)) {
          continue;
        }

        File.Delete(originPath);
        var certificatePath = Path.Combine(ClientsPath, fingerprint + ".cer");

        if (File.Exists(certificatePath)) {
          File.Delete(certificatePath);
        }
      }

      if (!Directory.EnumerateFileSystemEntries(ClientsPath).Any()) {
        Directory.Delete(ClientsPath);
      }
    }

    if (File.Exists(PublicPath)) {
      File.Delete(PublicPath);
    }

    if (File.Exists(PrivatePath)) {
      File.Delete(PrivatePath);
    }
  }

  private static string Fingerprint(X509Certificate2 certificate)
    => Convert.ToHexString(SHA256.HashData(certificate.RawData));

  private static void CheckValidity(X509Certificate2 certificate) {
    var now = DateTime.UtcNow;
    if (certificate.NotBefore.ToUniversalTime() > now ||
        certificate.NotAfter.ToUniversalTime() <= now) {
      throw new InvalidDataException("Certificate is outside its validity period.");
    }
  }
}
