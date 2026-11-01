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
config="$root/tests/PackageConsumer/NuGet.Config"
# When a caller supplies an owned cache, resolve dependencies there, without
# downloading/upgrading or sharing cached unpublished SDK bytes.
if [ -n "${NUGET_PACKAGES:-}" ] && [ -d "$NUGET_PACKAGES" ]; then
  python3 - "$config" "$cache/NuGet.Config" "$root/artifacts/packages" "$NUGET_PACKAGES" <<'PY'
import sys, xml.etree.ElementTree as E
source, target, feed, packages = sys.argv[1:]
root = E.parse(source)
for item in root.findall("./packageSources/add"):
    item.set("value", feed if item.get("key") == "sdk-local" else packages)
    item.attrib.pop("protocolVersion", None)
root.write(target, encoding="utf-8", xml_declaration=True)
PY
  config="$cache/NuGet.Config"
fi
dotnet restore "$root/tests/PackageConsumer/PackageConsumer.csproj" --force-evaluate \
  --packages "$cache" --configfile "$config" --verbosity quiet
dotnet build "$root/tests/PackageConsumer/PackageConsumer.csproj" -c Release --no-restore --verbosity quiet
# Prove obsolete methods/models cannot compile from the PACKAGED public API.
cat > "$negative/Negative.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetLockFilePath>obj/packages.lock.json</NuGetLockFilePath></PropertyGroup>
  <ItemGroup><PackageReference Include="KaitenCloud.Sdk" Version="0.3.0" /></ItemGroup>
</Project>
EOF
cat > "$negative/Obsolete.cs" <<'EOF'
using KaitenCloud.Sdk;
using KaitenCloud.Sdk.Management;
using System.Collections.Generic;
using System.Threading.Tasks;
public static class Obsolete
{
    public static async Task Bind(IKaitenClient client, KaitenClient concrete, KaitenManagementClient management)
    {
        var context = new Dictionary<string, object?>();
        await client.EvaluateFlagAsync("some-flag", context);
        await client.EvaluateFlagAsync<bool>("some-flag", context);
        await client.EvaluateAllFlagsAsync(context);
        await concrete.EvaluateFlagAsync("some-flag", context);
        await concrete.EvaluateFlagAsync<bool>("some-flag", context);
        await concrete.EvaluateAllFlagsAsync(context);
        await management.Instances.ValidateLicenseAsync("instance-slug");
        await client.ValidateInstanceLicenseAsync("instance-slug");
        await concrete.ValidateInstanceLicenseAsync("instance-slug");
        LicenseValidation? removedValidation = null;
        LicenseValidationStatus removedStatus = default;
        FlagEvaluation? single = null;
        BulkFlagEvaluation? bulk = null;
        KaitenEvaluationException? failure = null;
        _ = single; _ = bulk; _ = failure; _ = removedValidation; _ = removedStatus;
    }
}
EOF
dotnet restore "$negative/Negative.csproj" --packages "$cache" \
  --configfile "$config" --verbosity quiet
if dotnet build "$negative/Negative.csproj" -c Release --no-restore --verbosity quiet > "$root/artifacts/negative-public-api.log" 2>&1; then
  echo "FAIL: obsolete evaluation API compiled from package" >&2
  exit 1
fi
for symbol in EvaluateFlagAsync EvaluateAllFlagsAsync FlagEvaluation BulkFlagEvaluation KaitenEvaluationException ValidateLicenseAsync ValidateInstanceLicenseAsync LicenseValidation LicenseValidationStatus; do
  if ! grep -E 'error CS(1061|0246)' "$root/artifacts/negative-public-api.log" | grep -q "$symbol"; then
    echo "FAIL: expected negative compilation diagnostic absent for $symbol" >&2
    exit 1
  fi
done
echo "PASS: packaged OpenFeature/management examples build; obsolete validation/evaluation APIs do not compile."
# Only this script's fresh temporary directories are removed; executable/evidence remain.
