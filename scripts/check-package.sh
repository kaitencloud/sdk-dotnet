#!/bin/sh
# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
mkdir -p "$root/artifacts"
# A local unpublished package can change without its version changing. Use a new
# private cache, never clear a user's global cache, and resolve only the local SDK.
cache=$(mktemp -d "$root/artifacts/consumer-packages.XXXXXX")
negative=$(mktemp -d "$root/artifacts/negative-api.XXXXXX")
trap 'rm -rf "$cache" "$negative"' 0
dotnet restore "$root/tests/PackageConsumer/PackageConsumer.csproj" --force-evaluate \
  --packages "$cache" --configfile "$root/tests/PackageConsumer/NuGet.Config" --verbosity quiet
dotnet build "$root/tests/PackageConsumer/PackageConsumer.csproj" -c Release --no-restore --verbosity quiet
# Prove obsolete methods/models cannot compile from the PACKAGED public API.
cat > "$negative/Negative.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetLockFilePath>obj/packages.lock.json</NuGetLockFilePath></PropertyGroup>
  <ItemGroup><PackageReference Include="KaitenCloud.Sdk" Version="0.2.0" /></ItemGroup>
</Project>
EOF
cat > "$negative/Obsolete.cs" <<'EOF'
using KaitenCloud.Sdk;
using System.Collections.Generic;
using System.Threading.Tasks;
public static class Obsolete
{
    public static async Task Bind(IKaitenClient client, KaitenClient concrete)
    {
        var context = new Dictionary<string, object?>();
        await client.EvaluateFlagAsync("some-flag", context);
        await client.EvaluateFlagAsync<bool>("some-flag", context);
        await client.EvaluateAllFlagsAsync(context);
        await concrete.EvaluateFlagAsync("some-flag", context);
        await concrete.EvaluateFlagAsync<bool>("some-flag", context);
        await concrete.EvaluateAllFlagsAsync(context);
        FlagEvaluation? single = null;
        BulkFlagEvaluation? bulk = null;
        KaitenEvaluationException? failure = null;
        _ = single; _ = bulk; _ = failure;
    }
}
EOF
dotnet restore "$negative/Negative.csproj" --packages "$cache" \
  --configfile "$root/tests/PackageConsumer/NuGet.Config" --verbosity quiet
if dotnet build "$negative/Negative.csproj" -c Release --no-restore --verbosity quiet > "$root/artifacts/negative-public-api.log" 2>&1; then
  echo "FAIL: obsolete evaluation API compiled from package" >&2
  exit 1
fi
for symbol in EvaluateFlagAsync EvaluateAllFlagsAsync FlagEvaluation BulkFlagEvaluation KaitenEvaluationException; do
  if ! grep -E 'error CS(1061|0246)' "$root/artifacts/negative-public-api.log" | grep -q "$symbol"; then
    echo "FAIL: expected negative compilation diagnostic absent for $symbol" >&2
    exit 1
  fi
done
echo "PASS: packaged OpenFeature example builds; direct evaluation API/models do not compile."
# Only this script's fresh temporary directories are removed; executable/evidence remain.
