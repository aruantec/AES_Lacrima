#!/usr/bin/env bash
set -euo pipefail

out="$1"
src="$2"

if ! pkg-config --exists libpipewire-0.3 dbus-1 2>/dev/null; then
  echo "Skipping Linux capture bridge: install libpipewire-0.3-dev and libdbus-1-dev"
  exit 0
fi

cflags=$(pkg-config --cflags libpipewire-0.3 dbus-1)
libs=$(pkg-config --libs libpipewire-0.3 dbus-1)

g++ -shared -fPIC -O2 -std=c++17 -Wall -Wextra \
  ${cflags} \
  -o "${out}" "${src}" \
  -lGL -lEGL -lX11 -lXcomposite -lXdamage -lXfixes -lXext -lpthread \
  ${libs}
