// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.IO.Pipes;

namespace PinentryForWindows.Ui;

internal static class UiHelper {
  public static async Task<int> RunAsync(string readHandle, string writeHandle) {
    await using var requests = new AnonymousPipeClientStream(PipeDirection.In, readHandle);
    await using var responses = new AnonymousPipeClientStream(PipeDirection.Out, writeHandle);
    try {
      var request = await UiWire.ReadRequestAsync(requests, CancellationToken.None);
      using var result = NativeCredentialPrompt.Show(request);
      if (result is null) {
        await UiWire.WriteCancelledAsync(responses, CancellationToken.None);
      }
      else {
        await UiWire.WriteResultAsync(responses, result.Bytes, result.Remember, CancellationToken.None);
      }

      return 0;
    }
    catch (Exception) {
      try { await UiWire.WriteErrorAsync(responses, CancellationToken.None); }
      catch (Exception) {
        /* The parent has already closed the private pipe. */
      }

      return 1;
    }
  }
}
