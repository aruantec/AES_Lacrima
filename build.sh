#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

dotnet run --project "$script_dir/build/_build.csproj" -- "$@"
