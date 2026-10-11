// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Diagnostics;
using System.Security.Cryptography;
using AssuanLibrary.Protocol;
using AssuanLibrary.Server;
using PinentryForWindows.Core.Contracts;
using PinentryForWindows.Core.Protocol;

namespace PinentryForWindows.Core.Commands;

internal sealed class GetPinCommand : ICommandHandler<PinentrySessionState> {
  public string Name => "GETPIN";
  public string Description => "Ask for a passphrase";

  public async ValueTask HandleAsync(CommandContext<PinentrySessionState> context, CancellationToken ct) {
    var receivedAt = Stopwatch.GetTimestamp();
    var state = context.State;
    var error = state.Error;
    state.Error = string.Empty;

    if (state.CacheEligible &&
        error.Length == 0) {
      SecretBuffer? cached = null;
      try { cached = await state.Cache.ReadAsync(state.Origin, state.KeyGrip!, ct); }
      catch (Exception) when (!ct.IsCancellationRequested) {
        /* Cache failure falls back to input. */
      }

      if (cached is not null) {
        using (cached) {
          await context.SendStatusAsync("PASSWORD_FROM_CACHE", ReadOnlyMemory<byte>.Empty, ct);
          await SendSecretAsync(context, cached, ct);
        }

        return;
      }
    }

    var description = error.Length == 0 ? state.Description : error + Environment.NewLine + state.Description;
    var request = new PromptRequest(state.Title, description, state.UserLabel,
      state.PromptLabel, state.CacheEligible, state.TimeoutSeconds, receivedAt);
    using var first = await PromptAsync(state.Ui, request, ct);

    if (first is null) {
      throw HandlerException.Remote(83886179, "cancelled");
    }

    if (state.Repeat) {
      var repeatRequest = request with {
        Title = state.Title + " - Repeat", Description = state.RepeatDescription, OfferSave = false,
        GetPinTimestamp = Stopwatch.GetTimestamp()
      };

      using var second = await PromptAsync(state.Ui, repeatRequest, ct);

      if (second is null) {
        throw HandlerException.Remote(83886179, "cancelled");
      }

      if (!CryptographicOperations.FixedTimeEquals(first.Secret.Expose(), second.Secret.Expose())) {
        throw HandlerException.Remote(83886194, "PIN not repeated");
      }

      await context.SendStatusAsync("PIN_REPEATED", ReadOnlyMemory<byte>.Empty, ct);
    }

    await SendSecretAsync(context, first.Secret, ct);

    if (state.CacheEligible &&
        first.Remember) {
      var copy = first.Secret.Expose().ToArray();
      try { await state.Cache.WriteAsync(state.Origin, state.KeyGrip!, copy, ct); }
      catch (Exception) when (!ct.IsCancellationRequested) {
        /* A successful input does not require caching. */
      }
      finally { CryptographicOperations.ZeroMemory(copy); }
    }
  }

  private static async ValueTask SendSecretAsync(CommandContext<PinentrySessionState> context, SecretBuffer secret,
  CancellationToken ct) {
    var bytes = secret.Expose().ToArray();
    try { await context.SendDataAsync(bytes, ct); }
    finally { CryptographicOperations.ZeroMemory(bytes); }
  }

  private static async ValueTask<PasswordPromptResult?> PromptAsync(IPinentryUi ui, PromptRequest request,
  CancellationToken ct) {
    try { return await ui.PromptAsync(request, ct); }
    catch (TimeoutException) { throw HandlerException.Remote(83886142, "timeout"); }
  }
}
