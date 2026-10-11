// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using PinentryForWindows.Platform.Windows;

namespace PinentryForWindows.Tests;

public sealed class CredentialManagerTests {
  [Theory]
  [InlineData((int)CredentialManager.PromptForWindowsCredentialsFlag.CredUiWinGeneric, 0x1)]
  [InlineData((int)CredentialManager.PromptForWindowsCredentialsFlag.CredUiWinCheckbox, 0x2)]
  [InlineData((int)CredentialManager.PromptForWindowsCredentialsFlag.CredUiWinInCredOnly, 0x20)]
  public void Prompt_flags_match_Windows_API_values(int flag, int expected) {
    flag.ShouldBe(expected);
  }

  [Fact]
  public void Save_checkbox_prompt_uses_valid_Windows_flags() {
    // The opt-in GETPIN path must use 0x23, not 0x25 (which contains the invalid 0x4 flag).
    var flags = CredentialManager.PromptForWindowsCredentialsFlag.CredUiWinGeneric |
                CredentialManager.PromptForWindowsCredentialsFlag.CredUiWinInCredOnly |
                CredentialManager.PromptForWindowsCredentialsFlag.CredUiWinCheckbox;

    ((int)flags).ShouldBe(0x23);
  }
}
