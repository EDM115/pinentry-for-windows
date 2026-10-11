// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Diagnostics;
using PinentryForWindows.Broker;

namespace PinentryForWindows.Tests.Transport;

public sealed class ProcessIntegrationTests {
  [Fact]
  public async Task Local_process_handles_assuan_without_text_reencoding() {
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var executable = Path.Combine(root, "src", "PinentryForWindows", "bin", "Debug",
      "net10.0-windows10.0.26100.0", "pfw.exe");
    Assert.True(File.Exists(executable), "Build the Windows executable before running process tests.");

    using var process = Process.Start(new ProcessStartInfo(executable) {
      UseShellExecute = false,
      WorkingDirectory = root,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    }) ?? throw new InvalidOperationException("Could not start pinentry.");

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    Assert.StartsWith("OK", await process.StandardOutput.ReadLineAsync(deadline.Token));
    await process.StandardInput.WriteLineAsync("SETTITLE Test%20Pinentry");
    Assert.StartsWith("OK", await process.StandardOutput.ReadLineAsync(deadline.Token));
    await process.StandardInput.WriteLineAsync("GETINFO version");
    Assert.Equal("D " + typeof(BrokerIdentity).Assembly.GetName().Version,
      await process.StandardOutput.ReadLineAsync(deadline.Token));
    Assert.StartsWith("OK", await process.StandardOutput.ReadLineAsync(deadline.Token));
    await process.StandardInput.WriteLineAsync("GETINFO flavor");
    Assert.Equal("D pinentry-for-windows", await process.StandardOutput.ReadLineAsync(deadline.Token));
    Assert.StartsWith("OK", await process.StandardOutput.ReadLineAsync(deadline.Token));
    await process.StandardInput.WriteLineAsync("BYE");
    Assert.StartsWith("OK", await process.StandardOutput.ReadLineAsync(deadline.Token));

    await process.WaitForExitAsync(deadline.Token);
    Assert.Equal(0, process.ExitCode);
    Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync(deadline.Token));
  }
}
