#!/usr/bin/env bash
# Shared apt mirror workaround for GitHub-hosted Ubuntu runners.
# shellcheck shell=bash

fix_apt_mirrors() {
  local ubuntu_sources="/etc/apt/sources.list.d/ubuntu.sources"
  local archive_uri="https://archive.ubuntu.com/ubuntu/"
  local security_uri="https://security.ubuntu.com/ubuntu/"

  case "$(uname -m)" in
    aarch64|arm64)
      archive_uri="http://ports.ubuntu.com/ubuntu-ports/"
      security_uri="http://ports.ubuntu.com/ubuntu-ports/"
      ;;
  esac

  if [[ -f "$ubuntu_sources" ]] && grep -q 'mirror+file:/etc/apt/apt-mirrors.txt' "$ubuntu_sources"; then
    local suite
    suite="$(grep -m1 '^Suites:' "$ubuntu_sources" | awk '{print $2}')"
    if [[ -z "$suite" ]]; then
      suite="noble"
    fi

    echo "Rewriting ${ubuntu_sources} to use direct Ubuntu archive mirrors..."
    sudo tee "$ubuntu_sources" > /dev/null <<EOF
Types: deb
URIs: ${archive_uri}
Suites: ${suite} ${suite}-updates ${suite}-backports
Components: main restricted universe multiverse
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg

Types: deb
URIs: ${security_uri}
Suites: ${suite}-security
Components: main restricted universe multiverse
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
EOF
  elif [[ -f "$ubuntu_sources" ]]; then
    echo "Patching Ubuntu sources to avoid azure.archive.ubuntu.com stalls..."
    sudo sed -i \
      -e 's|URIs: mirror+file:/etc/apt/apt-mirrors.txt|URIs: https://archive.ubuntu.com/ubuntu/|g' \
      -e 's|http://azure\.archive\.ubuntu\.com/ubuntu|https://archive.ubuntu.com/ubuntu|g' \
      -e 's|http://security\.ubuntu\.com/ubuntu|https://security.ubuntu.com/ubuntu|g' \
      "$ubuntu_sources"
  fi

  if [[ -f /etc/apt/sources.list ]]; then
    sudo sed -i \
      -e 's|mirror+file:/etc/apt/apt-mirrors.txt|https://archive.ubuntu.com/ubuntu|g' \
      -e 's|http://azure\.archive\.ubuntu\.com/ubuntu|https://archive.ubuntu.com/ubuntu|g' \
      /etc/apt/sources.list
  fi

  if [[ -f /etc/apt/apt-mirrors.txt ]]; then
    sudo rm -f /etc/apt/apt-mirrors.txt
  fi
}
