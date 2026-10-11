// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Diagnostics;
using System.IO.Pipes;
using PinentryForWindows.Core.Contracts;

namespace PinentryForWindows.Ui;

internal sealed class UiCoordinator : IPinentryUi {
  public ValueTask<PasswordPromptResult?> PromptAsync(PromptRequest request, CancellationToken ct) {
    var task = Task.Run(() => {
      using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
      if (request.TimeoutSeconds > 0) {
        deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
      }

      try { return UiGate.Run(() => RunWorker(request, deadline.Token), deadline.Token); }
      catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested) {
        throw new TimeoutException("Pinentry prompt timed out.");
      }
    }, ct);
    return new ValueTask<PasswordPromptResult?>(task);
  }

  public ValueTask<bool> ConfirmAsync(DialogRequest request, CancellationToken ct)
    => new(Task.Run(() => UiGate.Run(() => WindowsDialogService.Show(request, ct), ct), ct));

  public ValueTask MessageAsync(DialogRequest request, CancellationToken ct)
    => new(Task.Run(() => UiGate.Run(() => WindowsDialogService.Show(request, ct), ct), ct));

  private static PasswordPromptResult? RunWorker(PromptRequest request, CancellationToken ct) {
    using var toChild = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
    using var fromChild = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
    var start = new ProcessStartInfo(executable) {
      UseShellExecute = false,
      CreateNoWindow = true,
      WorkingDirectory = Environment.CurrentDirectory
    };

    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
      start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pfw.dll"));
    }

    start.ArgumentList.Add("--ui-helper");
    start.ArgumentList.Add(toChild.GetClientHandleAsString());
    start.ArgumentList.Add(fromChild.GetClientHandleAsString());
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start UI helper.");
    toChild.DisposeLocalCopyOfClientHandle();
    fromChild.DisposeLocalCopyOfClientHandle();

    using var registration = ct.Register(() => {
      try {
        if (!child.HasExited) {
          child.Kill(true);
        }
      }
      catch (InvalidOperationException) {
        /* Child has exited. */
      }
    });

    try {
      UiWire.WriteRequestAsync(toChild, request, ct).AsTask().GetAwaiter().GetResult();
      var result = UiWire.ReadResultAsync(fromChild, ct).AsTask().GetAwaiter().GetResult();
      child.WaitForExit(TimeSpan.FromSeconds(5));
      return result;
    }
    finally {
      if (!child.HasExited) {
        child.Kill(true);
      }

      child.WaitForExit();
    }
  }
}
