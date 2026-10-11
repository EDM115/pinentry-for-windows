# Pinentry for Windows

A native Windows pinentry for GnuPG. Passphrase entry uses the **Windows Security** credential window rather than a
generic Qt dialog. A Linux relay lets a `gpg-agent` in WSL2 show that same window on the interactive Windows desktop.
Each agent and its keys stay in their original environment; only pinentry requests and responses cross the boundary.

Windows Security displays the full GnuPG key description without an editable username field. Its title is **Pinentry for
Windows** for a Windows request and **Pinentry for Windows [distroName]** for a paired WSL request, using the
distribution name assigned during pairing rather than client-provided `SETTITLE`. Windows Hello uses the registered
credential name **Pinentry for Windows**; the authenticated origin still scopes the cache entry.

![Windows Security passphrase prompt](.github/images/CredentialManagerPrompt.png)

## Requirements

- Windows 11 24H2 or newer and an interactive user session.
- WSL2 for Linux-to-Windows prompts. Launching Windows `.exe` files from WSL is **not** required.
- GnuPG in the environment whose keys are used.
- Windows Hello with a platform WebAuthn credential that supports PRF for persistent passphrase caching. If unavailable,
  normal passphrase entry still works.
- .NET 10 SDK to build from source. Release executables are Native AOT.

## How requests travel

| Agent               | `pinentry-program`                   | Secret key location | Window           |
|---------------------|--------------------------------------|---------------------|------------------|
| Windows `gpg-agent` | `pfw.x64.exe` or `pfw.arm64.exe`     | Windows keyring     | Windows Security |
| WSL2 `gpg-agent`    | `pfw-relay-x64` or `pfw-relay-arm64` | WSL keyring         | Windows Security |

The Windows executable serves the local agent over binary stdin/stdout. For WSL2, the Linux relay forwards that same
byte protocol through `AF_VSOCK` to a user-session Windows broker listening on `AF_HYPERV`. The stream is protected by
TLS 1.3 with a separately paired client certificate for each WSL installation. An unpaired connection is rejected
**before** an Assuan greeting. The broker binds to the current WSL VM ID, which it discovers without elevation through
`wslinfo --vm-id` in a paired running distribution. The VM ID may change after a WSL restart; it is not the security
identity of a distribution.

AssuanLibrary 2.0.0 handles Assuan framing and sessions. The Linux relay forwards bytes without treating passphrases as
text. It writes only Assuan to stdout and uses stderr for redacted failure categories.

The source is grouped by responsibility: `PinentryForWindows.Core` contains `Commands/`, `Contracts/`, and `Protocol/`;
the Windows project contains `Broker/`, `Cache/`, `Cli/`, `Transport/`, and `Ui/`; and `PinentryForWindows.Relay`
contains `Relay/`, `Security/`, and `Transport/`. Windows tests mirror the corresponding areas. Entry points, project
files, the application manifest, and the CsWin32 method list remain at their project roots.

## Windows installation

The Windows release assets are `pfw.x64.exe` and `pfw.arm64.exe`. `tools/install.ps1` downloads the matching executable
and `SHA256SUMS.txt`, checks its hash, updates the Windows `gpg-agent.conf` with confirmation when another pinentry is
configured, and restarts `gpg-agent` unless requested otherwise:

```powershell
./tools/install.ps1
./tools/install.ps1 -Update
./tools/install.ps1 -Uninstall
```

Use `-InstallDirectory <path>` for a nondefault destination and `-NoRestartAgent` to leave the agent running. The
installer uses the GnuPG `bindir` by default; that location may require an elevated installation shell. The executable
configured in `gpg-agent.conf` is still started as a normal, interactive user process.

`-Uninstall` restores or removes only its own `pinentry-program` setting. Before deleting the executable, it clears the
product's persistent cache and removes broker pairing material and HKCU autostart when associated with that executable.
It preserves an autostart pointing to another executable, and stops rather than deleting the binary if credential
cleanup fails. Specify `-DataDirectory` or `-BrokerDirectory` when those were configured outside their defaults. Hyper-V
service registration under HKLM is not modified automatically; remove it yourself with elevation when no installation
uses it. A broker process already running in the current session must also be stopped separately.

For a manual Windows installation, set the following in the Windows GnuPG home directory's `gpg-agent.conf`, then run
`gpgconf --kill gpg-agent`:

```text
pinentry-program C:/path/to/pfw.x64.exe
```

To configure the WSL broker, first create its identity and user-logon startup entry. Run the following as the Windows
user, supplying the executable installed above:

```powershell
./tools/setup-bridge.ps1 -ExecutablePath 'C:\path\to\pfw.x64.exe'
```

The script prints the **one-time administrative `reg add` command** for the Hyper-V service ID. Run that command
yourself in an elevated Command Prompt. It does not elevate or change HKLM on your behalf. It also prints the location
of the broker's public `broker.cer`. The broker's private TLS material is protected using the current Windows user's
DPAPI context.

## WSL2 installation and pairing

The release assets `pfw-relay-x64` and `pfw-relay-arm64` are Linux Native AOT binaries. From the distribution that owns
the `gpg-agent`, install the relay and pin the public broker certificate (adjust the mounted Windows path):

```bash
bash tools/install-wsl.sh --broker-certificate /mnt/c/path/to/broker.cer
```

The script verifies the release checksum, installs the relay under `~/.local/bin`, and creates a client certificate
under `~/.config/PinentryForWindows/relay` with a private key accessible only to that Linux user. It prints the path to
`client.cer`. On Windows, approve that **public** certificate for the distribution:

```powershell
./tools/setup-bridge.ps1 -ExecutablePath 'C:\path\to\pfw.x64.exe' `
  -Distribution archlinux `
  -RelayCertificatePath '\\wsl.localhost\archlinux\home\YOUR_USER\.config\PinentryForWindows\relay\client.cer'
```

The broker starts automatically at the next Windows sign-in; to start it in the current session, use the
`pfw.exe --broker <broker-directory>` command printed by the setup script. Once the broker and service registration are
ready, activate the WSL agent configuration:

```bash
bash tools/install-wsl.sh --broker-certificate /mnt/c/path/to/broker.cer --activate
```

If `gpg-agent.conf` already names another pinentry, inspect it and pass `--replace` only when you intend to change it.
The script backs up that file before changing it. Each additional WSL distribution gets its own relay credential and
approval. The broker can serve multiple paired distributions within the WSL2 VM; cache entries remain isolated by the
paired certificate identity.

For a manual WSL configuration, set `pinentry-program /home/YOUR_USER/.local/bin/pfw-relay-x64` in that distribution's
`gpg-agent.conf` after pairing and restart its agent with `gpgconf --kill gpg-agent`.

The current-user relay configuration must live on the Linux filesystem with `0700` directory and `0600` private-key
permissions. A Windows drive mounted through DrvFs can report broader Linux permissions even after `chmod`, so the relay
refuses to load a key that is not private. WSL1 and noninteractive/headless Windows sessions are outside this release's
scope.

For WSL uninstall, first run `bash tools/install-wsl.sh --uninstall`. This removes the relay binary and its GnuPG
setting but preserves `client.pfx` and the public `client.cer`, printing the certificate path. Revoke that public
certificate on Windows with `pfw.exe --revoke-client <broker-directory> <Windows path to client.cer>` (for example
through `\\wsl.localhost\...`). Then run `bash tools/install-wsl.sh --uninstall --revoked-on-windows` to remove the
Linux identity, even if the relay binary is already gone. Only supply the confirmation flag after Windows revocation, or
after the Windows broker identity has itself been removed.

## Command-line interface

The Windows binary accepts these commands (paths are local to Windows):

| Invocation                                                              | Purpose                                                                                              |
|-------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| `pfw.exe`                                                               | Serve one local Assuan pinentry session on stdin/stdout.                                             |
| `pfw.exe --help` or `-h`                                                | Show command usage.                                                                                  |
| `pfw.exe --check`                                                       | Report local prerequisites and GnuPG configuration without enrolling a Hello credential.             |
| `pfw.exe --clear-cache`                                                 | Remove the persistent cache credential and all entries from the current data directory.              |
| `pfw.exe --clear-cache <40-hex-keygrip>`                                | Remove only the Windows-origin entry for a software key; `n/`, `s/`, and `u/` prefixes are accepted. |
| `pfw.exe --clear-cache <data-directory>`                                | Clear a cache in an explicit data directory.                                                         |
| `pfw.exe --init-broker <broker-directory>`                              | Create or load the Windows broker identity and print its public certificate path.                    |
| `pfw.exe --approve-client <broker-directory> <client.cer> wsl:<distro>` | Pair a public WSL client certificate with a distribution label.                                      |
| `pfw.exe --revoke-client <broker-directory> <client.cer>`               | Revoke the matching client certificate.                                                              |
| `pfw.exe --reset-broker <broker-directory>`                             | Remove local broker identity and paired clients.                                                     |
| `pfw.exe --vm-id <distribution>`                                        | Report the running WSL VM ID, or `not running`.                                                      |
| `pfw.exe --broker <broker-directory>`                                   | Run the Windows broker in the interactive user session.                                              |

`--ui-helper` is an internal, per-prompt process mode; it is not a user command. The Linux binary accepts:

| Invocation                                                | Purpose                                                            |
|-----------------------------------------------------------|--------------------------------------------------------------------|
| `pfw-relay`                                               | Serve the WSL pinentry using `~/.config/PinentryForWindows/relay`. |
| `pfw-relay --run <relay-directory>`                       | Serve using an explicit relay identity directory.                  |
| `pfw-relay --init <relay-directory>`                      | Create the client key and public `client.cer`.                     |
| `pfw-relay --trust-broker <relay-directory> <broker.cer>` | Pin the Windows broker's public certificate.                       |
| `pfw-relay --remove-identity <relay-directory>`           | Remove the local client identity and pinned broker certificate.    |

For opt-in diagnostics, set `PFW_VERBOSE=1` **before starting** the Windows pinentry/broker or the WSL relay. Each side
reads its own environment; setting the variable in Windows does not turn it on inside WSL. If `gpg-agent` was already
running, restart that environment's agent so its next pinentry child inherits the variable. Extra events go only to
stderr and contain fixed lifecycle/cache categories, never Assuan payloads, passphrases, key descriptions, grips, or
certificate contents. With the variable unset (or any value other than `1`), these extra events are disabled; normal
error categories still appear on stderr.

```powershell
$env:PFW_VERBOSE = '1' # Windows shell starting pfw.exe or its broker
```

```bash
export PFW_VERBOSE=1 # WSL shell starting pfw-relay or its gpg-agent
```

## Passphrase cache and hardware tokens

Persistent cache is opt-in **per passphrase**. `gpg-agent` must send `OPTION allow-external-password-cache`, a stable
software-key identity must be available, and the user must check the save box in Windows Security. Each later cache hit
requests a fresh Windows Hello verification. A protected platform WebAuthn credential releases a PRF-derived encryption
key; entries are authenticated and encrypted with AES-GCM, partitioned by Windows/paired-WSL origin and key identity.
`S PASSWORD_FROM_CACHE` is sent only after a successful Hello-protected retrieval.

If Hello is cancelled, unavailable, or fails to return a usable PRF, the user enters the passphrase normally. There is
no silent fallback to an independently decryptable DPAPI cache. `SETERROR` bypasses cache for the next attempt;
`CLEARPASSPHRASE` removes the matching origin's entry. Old `pfwv2:` PasswordVault entries are not imported.

**Smart cards and hardware tokens never use this cache.** `SETKEYINFO --clear` and card/token prompts disable read and
write, even when external caching is otherwise allowed. Their PIN handling remains with the token, `gpg-agent`, and
`scdaemon`.

Use `--check` for noninteractive diagnostics. It reports Windows Hello platform-authenticator prerequisites, but does
not enroll a credential or claim that PRF has been proven without an actual assertion. To revoke the local cache
credential and remove all entries, run `--clear-cache` without arguments; the default data directory is
`%LOCALAPPDATA%\PinentryForWindows`. The directory form is useful for a separate test installation:

```powershell
& 'C:\path\to\pfw.x64.exe' --check
& 'C:\path\to\pfw.x64.exe' --clear-cache
& 'C:\path\to\pfw.x64.exe' --clear-cache "$env:LOCALAPPDATA\PinentryForWindows"
```

`--clear-cache <40-hex-keygrip>` (also accepting `n/`, `s/`, or `u/` prefixes) removes only the matching **local
Windows** key entry. It does not clear a paired WSL origin; use `CLEARPASSPHRASE` within that origin or clear all
entries explicitly.

Buffers owned by this application are bounded and wiped after use. Windows credential-provider memory, OS transports,
runtime copies, swap, and crash dumps cannot all be erased by the application. A process running as the same user can
still request an additional Hello operation; users should reject unexpected verification prompts.

## Pinentry protocol behavior

| Assuan command                                 | Behavior                                                                                                                                                     |
|------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GETPIN`                                       | Show Windows Security and return the passphrase, or a cancellation/timeout error.                                                                            |
| `CONFIRM` / `CONFIRM --one-button` / `MESSAGE` | Show a native confirmation or information dialog.                                                                                                            |
| `GETINFO version\|pid\|flavor\|ttyinfo`        | Return executable/protocol metadata.                                                                                                                         |
| `SETTITLE`, `SETDESC`, `SETPROMPT`, `SETERROR` | Set per-session dialog text; the Windows Security title for `GETPIN` is fixed as described above, while `SETTITLE` still applies to `CONFIRM` and `MESSAGE`. |
| `SETREPEAT`, `SETREPEATERROR`, `SETTIMEOUT`    | Configure repeat entry and timeout; the repeat-error text is accepted as session metadata.                                                                   |
| `SETOK`, `SETCANCEL`, `SETNOTOK`               | Record button labels; Windows dialogs use the labels their native controls support.                                                                          |
| `SETKEYINFO`, `CLEARPASSPHRASE <keygrip>`      | Set/clear software-key cache identity or remove this origin's cached passphrase.                                                                             |
| `OPTION`, `RESET`, `BYE`, `NOP`, `HELP`        | Handled by AssuanLibrary; `RESET` clears transient state without changing the authenticated origin.                                                          |

Recognized `OPTION` values are `allow-external-password-cache`, `default-ok`, `default-cancel`, `default-notok`, and
`default-prompt`. `grab`, `no-grab`, and unknown options are accepted without changing the Windows credential window.
The relay forwards Assuan bytes without interpreting them; diagnostics never enter protocol stdout.

`SETKEYINFO --clear` disables cache eligibility for the current card/token request. A key with a stable 40-hex-digit
grip, optionally prefixed by `n/`, `s/`, or `u/`, may be eligible for software-key caching. `SETERROR` suppresses one
cache read so a rejected passphrase is not immediately reused. Successful repeat-passphrase input sends
`S PIN_REPEATED`; a successful Hello-protected cache read sends `S PASSWORD_FROM_CACHE`. Responses use Assuan's bounded
binary-safe data escaping, and cancellation, timeout, and repeat mismatch return protocol errors rather than a partial
password.

## Build and test

```powershell
dotnet restore PinentryForWindows.slnx
dotnet test PinentryForWindows.slnx -c Debug
dotnet publish src/PinentryForWindows/PinentryForWindows.csproj -c Release -r win-x64
pwsh -NoProfile -File tests/Test-PinentryForWindows.ps1
pwsh -NoProfile -File tests/Test-InstallerConfig.ps1
```

The WSL installer has an isolated uninstall check that uses a disposable HOME and fake GnuPG executable:

```bash
bash tests/Test-InstallerWsl.sh
```

The protocol script defaults to non-interactive checks with an isolated cache under `artifacts/`. Add
`-Interactive -RequireWindowsHelloCache` for Windows Security and Hello checks. Add
`-IncludeGpgKeyTests -IncludeCacheTests -IncludeDecryptTest` to exercise a disposable, passphrase-protected GPG key in a
temporary GnuPG home; use only the script's dummy passphrase (`teste123` by default). The installer test uses a fake
`gpgconf` and never changes the primary agent configuration.

To measure the local `GETPIN` dispatch-to-Windows Security API interval, run
`pwsh -NoProfile -File tests/Measure-PinentryLatency.ps1 -Samples 21` with a published Windows x64 AOT binary available
under `artifacts/release-check-win-x64/`. Cancel every dialog; no password is required. The script reports the first
sample separately, median/p95 over the remaining prompts, and helper allocation/memory samples, then removes its
numeric-only measurement files. Normal pinentry runs write no timing data; `PFW_LATENCY_LOG` and
`PFW_HELPER_METRICS_LOG` enable the respective diagnostics explicitly with absolute output paths. The latency marker is
captured immediately before the native credential prompt call and written after the dialog returns, so the user's
response time is excluded. Helper allocation is managed allocation during one worker invocation; peak working set and
private memory are process-level measurements.

Build and test the Linux relay in WSL2:

```bash
dotnet test tests/PinentryForWindows.Core.Tests/PinentryForWindows.Core.Tests.csproj -c Debug
dotnet test tests/PinentryForWindows.Relay.Tests/PinentryForWindows.Relay.Tests.csproj -c Debug
dotnet publish src/PinentryForWindows.Relay/PinentryForWindows.Relay.csproj -c Release -r linux-x64
```

The published project outputs are `pfw.exe` and `pfw-relay`; the release workflow stages them as the
architecture-suffixed assets listed above. Native Windows Security, Windows Hello, GnuPG and Hyper-V/vsock require real
interactive Windows 11/WSL2 validation in addition to unit tests.
The release workflow also runs tests and a published-binary smoke check on each native x64/ARM64 runner before creating
a release; ARM64 remains unverified until those GitHub jobs run.

## Acknowledgments

- [Ookii.VmSockets](https://github.com/SvenGroot/Ookii.VmSockets) by Sven Groot informed the minimal Hyper-V/VSock
  endpoint implementation. The NuGet package is **not** a runtime dependency.
  See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for its MIT notice.
- [AssuanLibrary](https://github.com/baliestri/assuan-library) 2.0.0 supplies the Assuan protocol primitives and
  server-session API.

Pinentry for Windows is licensed under the [MIT License](LICENSE.md).
