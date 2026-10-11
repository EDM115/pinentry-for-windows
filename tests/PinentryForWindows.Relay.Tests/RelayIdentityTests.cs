// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PinentryForWindows.Relay.Security;
using Xunit;

namespace PinentryForWindows.Relay.Tests;

public sealed class RelayIdentityTests {
  [Fact]
  public void Client_credentials_are_private_and_broker_pin_rejects_an_impostor() {
    if (!OperatingSystem.IsLinux()) {
      return;
    }

    var directory = Path.Combine("/tmp", "pfw-relay-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var identity = new RelayIdentity(directory);
      identity.Create();
      using var client = identity.LoadClientCertificate();
      Assert.True(client.HasPrivateKey);
      Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(identity.PrivateKeyPath) &
                                      (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                       UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));

      using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
      var firstRequest = new CertificateRequest("CN=pfw-broker", firstKey, HashAlgorithmName.SHA256);
      using var trusted = firstRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddDays(1));
      var publicPath = Path.Combine(directory, "server.cer");
      File.WriteAllBytes(publicPath, trusted.Export(X509ContentType.Cert));
      identity.TrustBroker(publicPath);
      Assert.True(identity.IsTrustedBroker(trusted));

      using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
      var secondRequest = new CertificateRequest("CN=pfw-broker", secondKey, HashAlgorithmName.SHA256);
      using var impostor = secondRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow.AddDays(1));
      Assert.False(identity.IsTrustedBroker(impostor));
      identity.Remove();
      Assert.False(File.Exists(identity.PrivateKeyPath));
      Assert.False(File.Exists(identity.PublicCertificatePath));
    }
    finally {
      if (Directory.Exists(directory)) {
        Directory.Delete(directory, true);
      }
    }
  }
}
