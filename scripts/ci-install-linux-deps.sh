#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/fix-apt-mirrors.sh
source "${SCRIPT_DIR}/lib/fix-apt-mirrors.sh"

APT_OPTS=(
  -o Acquire::Retries=5
  -o Acquire::http::Timeout=20
  -o Acquire::https::Timeout=20
  -o Acquire::ForceIPv4=true
)

apt_get() {
  sudo DEBIAN_FRONTEND=noninteractive apt-get "${APT_OPTS[@]}" "$@"
}

retry_apt() {
  local description="$1"
  shift
  for attempt in 1 2 3; do
    echo "==> ${description} (attempt ${attempt}/3)"
    if "$@"; then
      return 0
    fi
    echo "${description} failed; retrying in 10s..." >&2
    sleep 10
  done
  return 1
}

BASE_PACKAGES=(
  libx11-dev
  libxcomposite-dev
  libxdamage-dev
  libxfixes-dev
  libgl1-mesa-dev
  libpipewire-0.3-dev
  libdbus-1-dev
)

AOT_PACKAGES=(
  clang
  zlib1g-dev
)

install_gamescope=false
include_aot=false
skip_mirror_fix=false
skip_update=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --gamescope)
      install_gamescope=true
      ;;
    --aot)
      include_aot=true
      ;;
    --skip-mirror-fix)
      skip_mirror_fix=true
      ;;
    --skip-update)
      skip_update=true
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 2
      ;;
  esac
  shift
done

packages=("${BASE_PACKAGES[@]}")
if [[ "$include_aot" == true ]]; then
  packages+=("${AOT_PACKAGES[@]}")
fi

if [[ "$skip_mirror_fix" != true ]]; then
  fix_apt_mirrors
fi

if [[ "$skip_update" != true ]]; then
  retry_apt "apt-get update" apt_get update
fi

retry_apt "apt-get install" apt_get install -y --no-install-recommends "${packages[@]}"

if [[ "$install_gamescope" == true ]]; then
  apt_get install -y --no-install-recommends gamescope || echo "::warning::gamescope install failed (optional)"
fi
