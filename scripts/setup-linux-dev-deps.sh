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

if [[ -f /etc/apt/apt-mirrors.txt ]]; then
  echo "Removing /etc/apt/apt-mirrors.txt to avoid slow azure.archive.ubuntu.com fallback..."
  sudo rm -f /etc/apt/apt-mirrors.txt
fi

sudo apt-get update -o Acquire::Retries=5 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20
sudo apt-get install -y "${packages[@]}"
echo "Done. Rebuild with ./build.sh Compile."
