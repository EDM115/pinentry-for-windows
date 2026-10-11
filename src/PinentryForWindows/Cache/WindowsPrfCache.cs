// Copyright (c) Bruno Sales <me@baliestri.dev>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AssuanLibrary.Protocol;
using PinentryForWindows.Core.Contracts;

namespace PinentryForWindows.Cache;

internal sealed class WindowsPrfCache(string directory, IPrfProvider provider) : IPinentryCache {
  private const int MAX_SECRET = 4096;
  private static readonly byte[] _magic = "PFW3"u8.ToArray();
  private static readonly TimeSpan _lockBudget = TimeSpan.FromSeconds(10);
  private string CredentialPath => Path.Combine(directory, "hello.cred");
  private string EntriesPath => Path.Combine(directory, "entries");
  public bool IsAvailable => provider.IsAvailable;

  public async ValueTask<SecretBuffer?> ReadAsync(string origin, string key, CancellationToken ct) {
    if (!IsAvailable) {
      return null;
    }

    var path = EntryPath(origin, key);
    if (!File.Exists(path) ||
        !File.Exists(CredentialPath) ||
        new FileInfo(path).Length > 4162) {
      return null;
    }

    var encoded = await File.ReadAllBytesAsync(path, ct);
    if (encoded.Length < 66 ||
        !encoded.AsSpan(0, 4).SequenceEqual(_magic)) {
      return null;
    }

    var length = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(64, 2));
    if (length > MAX_SECRET ||
        encoded.Length != 66 + length) {
      return null;
    }

    var credential = ReadCredentialId();
    var salt = encoded.AsMemory(4, 32);
    var prf = await provider.DeriveAsync(credential, salt, ct);
    var context = Context(origin, key);
    byte[]? derived = null;
    var clear = new byte[length];
    try {
      derived = new byte[32];
      HKDF.DeriveKey(HashAlgorithmName.SHA256, prf, derived, salt.Span, context);
      using var aes = new AesGcm(derived, 16);
      aes.Decrypt(encoded.AsSpan(36, 12), encoded.AsSpan(66), encoded.AsSpan(48, 16), clear, context);
      var result = new SecretBuffer(MAX_SECRET);
      result.Append(clear);
      return result;
    }
    catch (CryptographicException) { return null; }
    finally {
      CryptographicOperations.ZeroMemory(prf);
      if (derived is not null) {
        CryptographicOperations.ZeroMemory(derived);
      }

      CryptographicOperations.ZeroMemory(clear);
    }
  }

  public async ValueTask WriteAsync(string origin, string key, ReadOnlyMemory<byte> secret, CancellationToken ct) {
    if (!IsAvailable ||
        secret.Length > MAX_SECRET) {
      throw new InvalidOperationException("Secure cache unavailable.");
    }

    Directory.CreateDirectory(directory);
    Directory.CreateDirectory(EntriesPath);
    await using var admission = await LockAsync(ct);

    var newCredential = !File.Exists(CredentialPath);
    var credential = newCredential
      ? await provider.CreateCredentialAsync(ct)
      : ReadCredentialId();
    var salt = RandomNumberGenerator.GetBytes(32);
    var nonce = RandomNumberGenerator.GetBytes(12);
    var tag = new byte[16];
    var ciphertext = new byte[secret.Length];
    byte[]? prf = null;
    byte[]? derived = null;
    try {
      prf = await provider.DeriveAsync(credential, salt, ct);
      var context = Context(origin, key);
      derived = new byte[32];
      HKDF.DeriveKey(HashAlgorithmName.SHA256, prf, derived, salt, context);
      using (var aes = new AesGcm(derived, 16)) {
        aes.Encrypt(nonce, secret.Span, ciphertext, tag, context);
      }

      var envelope = new byte[66 + ciphertext.Length];
      _magic.CopyTo(envelope, 0);
      salt.CopyTo(envelope, 4);
      nonce.CopyTo(envelope, 36);
      tag.CopyTo(envelope, 48);
      BinaryPrimitives.WriteUInt16LittleEndian(envelope.AsSpan(64), checked((ushort)ciphertext.Length));
      ciphertext.CopyTo(envelope, 66);

      if (newCredential) {
        await SaveCredentialAsync(credential, ct);
      }

      var target = EntryPath(origin, key);
      var temporary = Path.Combine(EntriesPath, Path.GetRandomFileName());
      try {
        await File.WriteAllBytesAsync(temporary, envelope, ct);
        File.Move(temporary, target, true);
      }
      finally {
        if (File.Exists(temporary)) {
          File.Delete(temporary);
        }
      }
    }
    catch {
      if (newCredential) {
        if (File.Exists(CredentialPath)) {
          File.Delete(CredentialPath);
        }

        try { await provider.DeleteCredentialAsync(credential, CancellationToken.None); }
        catch (Exception) {
          /* A failed cleanup cannot expose the passphrase. */
        }
      }

      throw;
    }
    finally {
      if (prf is not null) {
        CryptographicOperations.ZeroMemory(prf);
      }

      if (derived is not null) {
        CryptographicOperations.ZeroMemory(derived);
      }
    }
  }

  public async ValueTask RemoveAsync(string origin, string key, CancellationToken ct) {
    if (!Directory.Exists(directory)) {
      return;
    }

    await using var admission = await LockAsync(ct);
    var path = EntryPath(origin, key);
    if (File.Exists(path)) {
      File.Delete(path);
    }
  }

  public async Task ClearAllAsync(CancellationToken ct) {
    if (!Directory.Exists(directory)) {
      return;
    }

    await using var admission = await LockAsync(ct);
    if (File.Exists(CredentialPath)) {
      var id = ReadCredentialId();
      await provider.DeleteCredentialAsync(id, ct);
      File.Delete(CredentialPath);
    }

    if (Directory.Exists(EntriesPath)) {
      foreach (var path in Directory.EnumerateFiles(EntriesPath, "*.bin")) {
        File.Delete(path);
      }
    }
  }

  private async Task<FileStream> LockAsync(CancellationToken ct) {
    var start = Stopwatch.StartNew();
    while (true) {
      ct.ThrowIfCancellationRequested();
      try {
        return new FileStream(Path.Combine(directory, "cache.lock"), FileMode.OpenOrCreate,
          FileAccess.ReadWrite, FileShare.None);
      }
      catch (IOException) when (start.Elapsed < _lockBudget) {
        await Task.Delay(50, ct);
      }
    }
  }

  private async Task SaveCredentialAsync(byte[] credential, CancellationToken ct) {
    if (credential.Length is < 1 or > 1024) {
      throw new InvalidDataException("Invalid credential ID.");
    }

    var data = new byte[2 + credential.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(data, checked((ushort)credential.Length));
    credential.CopyTo(data, 2);
    var temporary = CredentialPath + ".tmp";
    try {
      await File.WriteAllBytesAsync(temporary, data, ct);
      File.Move(temporary, CredentialPath, false);
    }
    finally {
      if (File.Exists(temporary)) {
        File.Delete(temporary);
      }
    }
  }

  private byte[] ReadCredentialId() {
    var data = File.ReadAllBytes(CredentialPath);
    if (data.Length < 3 ||
        data.Length > 1026 ||
        BinaryPrimitives.ReadUInt16LittleEndian(data) != data.Length - 2) {
      throw new InvalidDataException("Invalid cache credential record.");
    }

    return data[2..];
  }

  private string EntryPath(string origin, string key) {
    var id = SHA256.HashData(Context(origin, key));
    return Path.Combine(EntriesPath, Convert.ToHexString(id) + ".bin");
  }

  private static byte[] Context(string origin, string key)
    => Encoding.UTF8.GetBytes("pfw-cache-v3\0" + origin + "\0" + key);
}
