#!/usr/bin/env bash
set -euo pipefail

repo='baliestri/pinentry-for-windows'
config_dir="${HOME}/.config/PinentryForWindows/relay"
bin_dir="${HOME}/.local/bin"
broker_certificate=''
activate=false
replace=false
uninstall=false
revoked_on_windows=false

while (($#)); do
  case "$1" in
    --broker-certificate)
      broker_certificate="${2:?missing certificate path}"
      shift 2 ;;
    --activate) activate=true; shift ;;
    --replace) replace=true; shift ;;
    --uninstall) uninstall=true; shift ;;
    --revoked-on-windows) revoked_on_windows=true; shift ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
  esac
done

if "$revoked_on_windows" && ! "$uninstall"; then
  printf '%s\n' '--revoked-on-windows requires --uninstall.' >&2
  exit 2
fi

case "$(uname -m)" in
  x86_64) arch='x64' ;;
  aarch64) arch='arm64' ;;
  *) printf 'Unsupported WSL architecture.\n' >&2; exit 1 ;;
esac

asset="pfw-relay-${arch}"
installed="${bin_dir}/${asset}"

if "$uninstall"; then
  if command -v gpgconf >/dev/null 2>&1; then
    homedir="$(gpgconf --list-dirs homedir)"
    config="${homedir}/gpg-agent.conf"
    if [[ -f "$config" ]] && grep -Eq '^[[:space:]]*pinentry-program[[:space:]]' "$config"; then
      if awk -v path="$installed" '$1 == "pinentry-program" && $2 == path { found = 1 } END { exit !found }' "$config"; then
        backup="${config}.pfw-backup.$(date -u +%Y%m%d%H%M%S)"
        cp -- "$config" "$backup"
        temporary="$(mktemp "${homedir}/.pfw-gpg-agent.XXXXXX")"
        awk -v path="$installed" '$1 != "pinentry-program" || $2 != path {print}' "$config" > "$temporary"
        chmod 600 "$temporary"
        mv -- "$temporary" "$config"
        gpgconf --kill gpg-agent || true
      fi
    fi
  fi

  if "$revoked_on_windows"; then
    if [[ -x "$installed" ]]; then
      "$installed" --remove-identity "$config_dir"
    else
      rm -f -- "${config_dir}/client.pfx" "${config_dir}/client.cer" "${config_dir}/broker.cer"
      rmdir -- "$config_dir" 2>/dev/null || true
    fi
    printf 'Local relay identity removed after confirmed Windows revocation.\n'
  elif [[ -f "${config_dir}/client.pfx" || -f "${config_dir}/client.cer" ]]; then
    printf 'Revoke this public client certificate on Windows before removing the Linux identity:\n'
    printf '  %s\n' "${config_dir}/client.cer"
    printf 'On Windows: pfw.exe --revoke-client <broker-directory> <Windows path to client.cer>\n'
    printf 'Then rerun: bash tools/setup-wsl.sh --uninstall --revoked-on-windows\n'
  fi

  if [[ -f "$installed" ]]; then rm -- "$installed"; fi
  printf 'Relay binary removed.\n'
  exit 0
fi

for executable in curl sha256sum gpgconf; do
  command -v "$executable" >/dev/null 2>&1 || {
    printf 'Required command not found: %s\n' "$executable" >&2
    exit 1
  }
done

temporary="$(mktemp -d)"
trap 'rm -rf -- "$temporary"' EXIT
base="https://github.com/${repo}/releases/latest/download"
curl --fail --location --retry 3 --silent --show-error "${base}/${asset}" --output "${temporary}/${asset}"
curl --fail --location --retry 3 --silent --show-error "${base}/SHA256SUMS.txt" --output "${temporary}/SHA256SUMS.txt"
expected="$(awk -v name="$asset" '$2 == name { print $1 }' "${temporary}/SHA256SUMS.txt")"
actual="$(sha256sum "${temporary}/${asset}" | cut -d ' ' -f 1)"
if [[ -z "$expected" || "$expected" != "$actual" ]]; then
  printf 'Release checksum verification failed.\n' >&2
  exit 1
fi

install -d -m 700 "$config_dir"
install -d -m 700 "$bin_dir"
install -m 755 "${temporary}/${asset}" "$installed"
if [[ ! -f "${config_dir}/client.pfx" ]]; then
  "$installed" --init "$config_dir"
fi

if [[ -n "$broker_certificate" ]]; then
  "$installed" --trust-broker "$config_dir" "$broker_certificate"
fi

printf 'Relay installed at: %s\n' "$installed"
printf 'Give this public certificate to tools/setup-bridge.ps1 on Windows: %s\n' "${config_dir}/client.cer"
if [[ ! -f "${config_dir}/broker.cer" ]]; then
  printf 'Broker public certificate is not pinned yet; run with --broker-certificate before activation.\n'
fi

if ! "$activate"; then
  printf 'After Windows pairing, rerun with --activate to configure gpg-agent.\n'
  exit 0
fi

[[ -f "${config_dir}/broker.cer" ]] || {
  printf 'Broker identity must be pinned before activating the relay.\n' >&2
  exit 1
}

homedir="$(gpgconf --list-dirs homedir)"
install -d -m 700 "$homedir"
config="${homedir}/gpg-agent.conf"
if [[ -f "$config" ]] && grep -Eq '^[[:space:]]*pinentry-program[[:space:]]' "$config"; then
  if awk -v path="$installed" '$1 == "pinentry-program" && $2 != path { different = 1 } END { exit !different }' "$config" && ! "$replace"; then
    printf 'Existing pinentry-program found; use --replace after reviewing gpg-agent.conf.\n' >&2
    exit 1
  fi

  cp -- "$config" "${config}.pfw-backup.$(date -u +%Y%m%d%H%M%S)"
fi

temporary_config="$(mktemp "${homedir}/.pfw-gpg-agent.XXXXXX")"
if [[ -f "$config" ]]; then
  awk '$1 != "pinentry-program" {print}' "$config" > "$temporary_config"
fi
printf 'pinentry-program %s\n' "$installed" >> "$temporary_config"
chmod 600 "$temporary_config"
mv -- "$temporary_config" "$config"
gpgconf --kill gpg-agent
printf 'WSL gpg-agent now uses the Windows pinentry relay.\n'
