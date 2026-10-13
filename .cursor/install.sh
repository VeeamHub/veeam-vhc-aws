#!/usr/bin/env bash
set -euo pipefail

export DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_ROOT=/usr/local/share/dotnet

as_root() {
  if [ "$(id -u)" -eq 0 ]; then
    "$@"
  else
    sudo "$@"
  fi
}

if ! [ -x "$DOTNET_ROOT/dotnet" ] || ! "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  as_root bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT"
fi

as_root ln -sfn "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet
printf 'export DOTNET_ROOT=%s\nexport PATH="$DOTNET_ROOT:$PATH"\nexport DOTNET_CLI_TELEMETRY_OPTOUT=1\n' "$DOTNET_ROOT" \
  | as_root tee /etc/profile.d/dotnet.sh >/dev/null

export PATH="$DOTNET_ROOT:$PATH"
dotnet restore src/VeeamVhcAws/VeeamVhcAws.csproj
dotnet restore tests/VeeamVhcAws.Tests/VeeamVhcAws.Tests.csproj
dotnet restore tests/VeeamVhcAws.UiTests/VeeamVhcAws.UiTests.csproj

if [ -f .cursor/skills/verify-veeam-vhc-aws/scripts/package-lock.json ]; then
  npm ci --omit=dev --prefix .cursor/skills/verify-veeam-vhc-aws/scripts
else
  npm install --omit=dev --prefix .cursor/skills/verify-veeam-vhc-aws/scripts
fi
