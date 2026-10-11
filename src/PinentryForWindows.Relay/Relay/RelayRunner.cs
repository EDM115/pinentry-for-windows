// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using PinentryForWindows.Relay.Security;
using PinentryForWindows.Relay.Transport;

namespace PinentryForWindows.Relay.Relay;

[SupportedOSPlatform("linux")]
internal static class RelayRunner {
  private const int PORT = 44986;
  private static readonly TimeSpan _connectionBudget = TimeSpan.FromSeconds(15);

  public static async Task RunAsync(RelayIdentity identity, CancellationToken ct) {
    using var clientCertificate = identity.LoadClientCertificate();
    var elapsed = Stopwatch.StartNew();
    Socket? socket = null;
    while (socket is null &&
           elapsed.Elapsed < _connectionBudget) {
      ct.ThrowIfCancellationRequested();
      try {
        var remaining = _connectionBudget - elapsed.Elapsed;
        socket = VsockConnector.Connect(PORT,
          remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), ct);
      }
      catch (Exception error) when
        (error is SocketException or TimeoutException && elapsed.Elapsed < _connectionBudget) {
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
      }
    }

    if (socket is null) {
      throw new TimeoutException("Broker was not available within the connection budget.");
    }

    using (socket) {
      await using var network = new VsockStream(socket);
      using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
      handshake.CancelAfter(TimeSpan.FromSeconds(10));
      await using var secure = await RelayTls.AuthenticateAsync(network, identity,
        clientCertificate, handshake.Token);
      await AssuanRelay.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), secure, ct);
    }
  }
}
