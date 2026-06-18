#!/usr/bin/env bash
set -euo pipefail

# GitHub-hosted Ubuntu runners route apt through azure.archive.ubuntu.com via
# /etc/apt/apt-mirrors.txt. When those mirrors are slow or unreachable, apt-get
# update can sit retrying "Ign" entries for a long time before falling back.
fix_apt_mirrors() {
  if [[ -f /etc/apt/apt-mirrors.txt ]]; then
    echo "Removing /etc/apt/apt-mirrors.txt to avoid azure.archive.ubuntu.com stalls..."
    sudo rm -f /etc/apt/apt-mirrors.txt
  fi

  for sources_file in /etc/apt/sources.list.d/ubuntu.sources /etc/apt/sources.list; do
    if [[ -f "$sources_file" ]]; then
      sudo sed -i \
        -e 's|http://azure\.archive\.ubuntu\.com/ubuntu|https://archive.ubuntu.com/ubuntu|g' \
        -e 's|http://security\.ubuntu\.com/ubuntu|https://security.ubuntu.com/ubuntu|g' \
        "$sources_file"
    fi
  done
}

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
