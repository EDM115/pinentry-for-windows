// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using PinentryForWindows.Core.Contracts;
using PinentryForWindows.Ui;

namespace PinentryForWindows.Tests.Ui;

public sealed class OriginLabelUiTests {
  [Fact]
  public async Task Wsl_request_cannot_hide_its_authenticated_origin_in_the_window_title() {
    var inner = new CaptureUi();
    var ui = new OriginLabelUi(inner, "wsl:archlinux#ABCDEF");
    await ui.PromptAsync(new PromptRequest("Pinentry", "Unlock key", "User", "Password:",
      false, 0), CancellationToken.None);

    Assert.Equal("WSL archlinux — Pinentry", inner.LastTitle);
    Assert.DoesNotContain("ABCDEF", inner.LastTitle);
  }

  private sealed class CaptureUi : IPinentryUi {
    public string LastTitle { get; private set; } = string.Empty;

    public ValueTask<PasswordPromptResult?> PromptAsync(PromptRequest request, CancellationToken ct) {
      LastTitle = request.Title;
      return ValueTask.FromResult<PasswordPromptResult?>(null);
    }

    public ValueTask<bool> ConfirmAsync(DialogRequest request, CancellationToken ct)
      => ValueTask.FromResult(true);

    public ValueTask MessageAsync(DialogRequest request, CancellationToken ct)
      => ValueTask.CompletedTask;
  }
}
