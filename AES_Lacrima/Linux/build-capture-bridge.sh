#!/usr/bin/env bash
set -euo pipefail

out="$1"
src="$2"

if ! pkg-config --exists libpipewire-0.3 dbus-1 2>/dev/null; then
  echo "Linux capture bridge requires libpipewire-0.3-dev and libdbus-1-dev (pkg-config)." >&2
  if [[ "${CI:-}" == "true" || "${GITHUB_ACTIONS:-}" == "true" || "${REQUIRE_LINUX_CAPTURE_BRIDGE:-}" == "1" ]]; then
    exit 1
  fi
  echo "Skipping Linux capture bridge build on local machine." >&2
  exit 0
fi

cflags=$(pkg-config --cflags libpipewire-0.3 dbus-1)
libs=$(pkg-config --libs libpipewire-0.3 dbus-1)

g++ -shared -fPIC -O2 -std=c++17 -Wall -Wextra \
  ${cflags} \
  -o "${out}" "${src}" \
  -lGL -lEGL -lX11 -lXcomposite -lXdamage -lXfixes -lXext -lpthread \
  ${libs}
