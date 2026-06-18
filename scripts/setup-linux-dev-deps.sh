#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "This script is for Linux only." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/fix-apt-mirrors.sh
source "${SCRIPT_DIR}/lib/fix-apt-mirrors.sh"

packages=(
  build-essential
  pkg-config
  libx11-dev
  libxcomposite-dev
  libxdamage-dev
  libxfixes-dev
  libgl1-mesa-dev
  libpipewire-0.3-dev
  libdbus-1-dev
)

echo "Installing Linux native build dependencies..."
fix_apt_mirrors
sudo apt-get update -o Acquire::Retries=5 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20
sudo apt-get install -y "${packages[@]}"
echo "Done. Rebuild with ./build.sh Compile."
