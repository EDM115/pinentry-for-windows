// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Diagnostics;
using PinentryForWindows.Core.Contracts;
using PinentryForWindows.Ui;

namespace PinentryForWindows.Tests.Ui;

public sealed class UiWireTests {
  [Fact]
  public async Task Secret_result_preserves_binary_bytes_and_remember_choice() {
    using var stream = new MemoryStream();
    byte[] expected = [0, (byte)'%', (byte)'\n', 0xff, (byte)'A'];
    await UiWire.WriteResultAsync(stream, expected, true, CancellationToken.None);
    stream.Position = 0;

    using var result = await UiWire.ReadResultAsync(stream, CancellationToken.None);
    Assert.NotNull(result);
    Assert.True(result.Remember);
    Assert.True(result.Secret.Expose().SequenceEqual(expected));
  }

  [Fact]
  public async Task Oversized_child_response_is_rejected_before_read() {
    byte[] oversizedHeader = [(byte)'P', (byte)'F', (byte)'R', (byte)'1', 1, 0, 0x01, 0x10];
    using var stream = new MemoryStream(oversizedHeader);
    await Assert.ThrowsAsync<InvalidDataException>(async () =>
      await UiWire.ReadResultAsync(stream, CancellationToken.None));
  }

  [Fact]
  public async Task Cancelled_child_does_not_return_a_password() {
    using var stream = new MemoryStream();
    await UiWire.WriteCancelledAsync(stream, CancellationToken.None);
    stream.Position = 0;
    Assert.Null(await UiWire.ReadResultAsync(stream, CancellationToken.None));
  }

  [Fact]
  public async Task Prompt_description_is_bounded_before_launching_ui() {
    using var stream = new MemoryStream();
    var request = new PromptRequest("Title", new string('X', 5000), "User", "Password:", false, 0);
    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
      await UiWire.WriteRequestAsync(stream, request, CancellationToken.None));
  }

  [Fact]
  public async Task Prompt_request_preserves_getpin_timestamp_across_the_helper_pipe() {
    using var stream = new MemoryStream();
    var timestamp = Stopwatch.GetTimestamp();
    var request = new PromptRequest("Title", "Description", "User", "Password:", false, 0, timestamp);
    await UiWire.WriteRequestAsync(stream, request, CancellationToken.None);
    stream.Position = 0;

    var decoded = await UiWire.ReadRequestAsync(stream, CancellationToken.None);
    Assert.Equal(timestamp, decoded.GetPinTimestamp);
    Assert.Equal("Description", decoded.Description);
  }
}
