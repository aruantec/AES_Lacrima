#!/usr/bin/env bash
set -euo pipefail

out="$1"
src="$2"

prefix="${AES_LINUX_DEV_PREFIX:-}"
if [[ -z "$prefix" && -d "${HOME}/.local/aes-build/usr/include" ]]; then
  prefix="${HOME}/.local/aes-build"
fi

if [[ -n "$prefix" ]]; then
  export PATH="${prefix}/usr/bin:${PATH}"
  export PKG_CONFIG_PATH="${prefix}/usr/lib/x86_64-linux-gnu/pkgconfig${PKG_CONFIG_PATH:+:${PKG_CONFIG_PATH}}"
  export LD_LIBRARY_PATH="${prefix}/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}"
fi

if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists libpipewire-0.3 dbus-1 2>/dev/null; then
  cflags=$(pkg-config --cflags libpipewire-0.3 dbus-1)
  libs=$(pkg-config --libs libpipewire-0.3 dbus-1)
  link_flags=""
elif [[ -n "$prefix" && -f "${prefix}/usr/include/pipewire-0.3/pipewire/pipewire.h" && -f "${prefix}/usr/include/dbus-1.0/dbus/dbus.h" ]]; then
  cflags="-I${prefix}/usr/include/pipewire-0.3 -I${prefix}/usr/include/spa-0.2 -I${prefix}/usr/include -I${prefix}/usr/include/x86_64-linux-gnu -I${prefix}/usr/include/dbus-1.0 -I${prefix}/usr/lib/x86_64-linux-gnu/dbus-1.0/include"
  link_flags="-L${prefix}/usr/lib/x86_64-linux-gnu -L/usr/lib/x86_64-linux-gnu"
  libs="-lpipewire-0.3 -ldbus-1"
else
  echo "Linux capture bridge requires libpipewire-0.3-dev and libdbus-1-dev (pkg-config)." >&2
  echo "Install system packages or run scripts/setup-linux-dev-deps.sh." >&2
  if [[ "${CI:-}" == "true" || "${GITHUB_ACTIONS:-}" == "true" || "${REQUIRE_LINUX_CAPTURE_BRIDGE:-}" == "1" ]]; then
    exit 1
  fi
  echo "Skipping Linux capture bridge build on local machine." >&2
  exit 0
fi

if [[ -n "$prefix" && -x "${prefix}/usr/bin/g++" ]]; then
  cxx="${prefix}/usr/bin/g++"
elif [[ -n "${CXX:-}" ]]; then
  cxx="${CXX}"
elif command -v g++ >/dev/null 2>&1; then
  cxx="g++"
else
  echo "Linux capture bridge requires g++." >&2
  exit 1
fi

"${cxx}" -shared -fPIC -O2 -std=c++17 -Wall -Wextra \
  ${cflags} \
  -o "${out}" "${src}" \
  ${link_flags} \
  -lGL -lEGL -lX11 -lXcomposite -lXdamage -lXfixes -lXext -lpthread \
  ${libs}
