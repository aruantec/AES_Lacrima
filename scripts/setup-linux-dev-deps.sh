#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "This script is for Linux only." >&2
  exit 1
fi

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
sudo apt-get update
sudo apt-get install -y "${packages[@]}"
echo "Done. Rebuild with ./build.sh Compile."
