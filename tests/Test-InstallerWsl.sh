#!/usr/bin/env bash
set -euo pipefail

repo="$(realpath -- "$(dirname -- "${BASH_SOURCE[0]}")/..")"
[[ -d "${repo}/artifacts" ]] || { printf 'Repository artifacts directory missing.\n' >&2; exit 1; }
root="$(mktemp -d "${repo}/artifacts/installer-wsl-test.XXXXXX")"
trap 'rm -rf -- "$root"' EXIT

export HOME="${root}/home"
export PFW_TEST_ROOT="$root"
mkdir -p "${HOME}/.local/bin" "${HOME}/.config/PinentryForWindows/relay" \
  "${root}/fake-bin" "${root}/gnupg"
export PATH="${root}/fake-bin:${PATH}"

cat > "${root}/fake-bin/gpgconf" <<'EOF'
#!/usr/bin/env bash
if [[ "$1" == '--list-dirs' && "$2" == 'homedir' ]]; then
  printf '%s\n' "${PFW_TEST_ROOT}/gnupg"
elif [[ "$1" == '--kill' && "$2" == 'gpg-agent' ]]; then
  exit 0
else
  exit 2
fi
EOF
chmod 755 "${root}/fake-bin/gpgconf"

cat > "${root}/relay-stub" <<'EOF'
#!/usr/bin/env bash
[[ "$1" == '--remove-identity' ]] || exit 2
printf '%s %s\n' "$1" "$2" >> "${PFW_TEST_ROOT}/calls"
rm -f -- "$2/client.pfx" "$2/client.cer" "$2/broker.cer"
EOF

case "$(uname -m)" in
  x86_64) asset='pfw-relay-x64' ;;
  aarch64) asset='pfw-relay-arm64' ;;
  *) printf 'Unsupported test architecture.\n' >&2; exit 1 ;;
esac
installed="${HOME}/.local/bin/${asset}"
config_dir="${HOME}/.config/PinentryForWindows/relay"
install -m 755 "${root}/relay-stub" "$installed"
printf 'disposable private key\n' > "${config_dir}/client.pfx"
printf 'disposable public certificate\n' > "${config_dir}/client.cer"
printf 'disposable broker pin\n' > "${config_dir}/broker.cer"
printf 'unrelated setting\n' > "${config_dir}/keep.txt"
printf 'default-cache-ttl 300\npinentry-program %s\n' "$installed" > "${root}/gnupg/gpg-agent.conf"

if bash "${repo}/install-wsl.sh" --revoked-on-windows > "${root}/invalid-output" 2>&1; then
  printf 'Revocation flag was accepted without uninstall.\n' >&2
  exit 1
fi
[[ -f "${config_dir}/client.pfx" ]] || { printf 'Invalid invocation altered private key.\n' >&2; exit 1; }

bash "${repo}/install-wsl.sh" --uninstall > "${root}/first-output"
if ! grep -qi 'revoke' "${root}/first-output"; then
  printf 'Uninstall did not explain Windows revocation before identity removal.\n' >&2
  exit 1
fi
[[ ! -e "$installed" && -f "${config_dir}/client.pfx" && -f "${config_dir}/client.cer" ]]
grep -q '^default-cache-ttl 300$' "${root}/gnupg/gpg-agent.conf"
if grep -q '^pinentry-program ' "${root}/gnupg/gpg-agent.conf"; then
  printf 'Uninstall kept its GnuPG setting.\n' >&2
  exit 1
fi

install -m 755 "${root}/relay-stub" "$installed"
bash "${repo}/install-wsl.sh" --uninstall --revoked-on-windows > "${root}/second-output"
[[ ! -e "$installed" && ! -e "${config_dir}/client.pfx" &&
   ! -e "${config_dir}/client.cer" && ! -e "${config_dir}/broker.cer" ]]
[[ -f "${config_dir}/keep.txt" ]]
grep -Fq -- "--remove-identity ${config_dir}" "${root}/calls"

printf 'disposable private key\n' > "${config_dir}/client.pfx"
printf 'disposable public certificate\n' > "${config_dir}/client.cer"
bash "${repo}/install-wsl.sh" --uninstall --revoked-on-windows > "${root}/fallback-output"
[[ ! -e "${config_dir}/client.pfx" && ! -e "${config_dir}/client.cer" ]]
[[ -f "${config_dir}/keep.txt" ]]
printf 'WSL installer revocation-confirmation and cleanup isolation: PASS\n'
