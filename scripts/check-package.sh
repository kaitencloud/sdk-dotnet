#!/bin/sh
# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
mkdir -p "$root/artifacts"
# A local unpublished package can change without its version changing. Use a new
# private cache, never clear a user's global cache, and resolve only the local SDK.
cache=$(mktemp -d "$root/artifacts/consumer-packages.XXXXXX")
trap 'rm -rf "$cache"' 0
dotnet restore "$root/tests/PackageConsumer/PackageConsumer.csproj" --force-evaluate \
  --packages "$cache" --configfile "$root/tests/PackageConsumer/NuGet.Config" --verbosity quiet
dotnet build "$root/tests/PackageConsumer/PackageConsumer.csproj" -c Release --no-restore --verbosity quiet
# Only this script's fresh temporary directory is removed; executable output remains.
