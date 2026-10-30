# Kaiten SDK for .NET

A typed **server-side** client for Kaiten Core and OFREP. Package and namespace:
**KaitenCloud.Sdk**, local version **0.1.1** (not published).

Targets **net8.0 / C#12**, an explicit baseline chosen here, not an inferred
organization-wide policy. A .NET 10 executable and separate NuGet consumer verify
compatibility with Sushi. No netstandard, Platform, billing, browser-token,
offline evaluation or OpenFeature-provider compatibility is claimed.

## Local build and package consumption

Prerequisites: .NET 10 SDK (global.json permits the latest 10.0 feature band),
.NET 8 runtime for minimum-runtime unit tests, NuGet.org access on initial restore.
Task is optional. No global upgrades or services are needed for unit/package checks.

```sh
task check
# Equivalent commands from this repository:
dotnet restore KaitenCloud.sln --locked-mode
dotnet build KaitenCloud.sln -c Release --no-restore
dotnet test tests/KaitenCloud.Sdk.Tests -c Release --no-build --no-restore
dotnet pack src/KaitenCloud.Sdk -c Release --no-build --no-restore -o artifacts/packages
sh scripts/check-package.sh
```

Output: `artifacts/packages/KaitenCloud.Sdk.0.1.1.nupkg`. Keep product repos
independent: consume this package from a local NuGet feed, not a workstation-specific
source path. In the demo's NuGet.Config, add a documented relative artifact feed
(or copy the package into its own ignored local feed) alongside NuGet.org:

```xml
<PackageReference Include="KaitenCloud.Sdk" Version="0.1.1" />
```

After rebuilding this unpublished version, restore to a fresh **project-local**
package directory to avoid NuGet's immutable-version cache. Never clear global
caches. Bump the version for release. Keep SDK/test/example dependency lockfiles with
the source. The local package fixture regenerates its lock under ignored obj/ and
uses a fresh private cache: unpublished package bytes change with working-tree or
Git provenance changes. A canonical lock on that changing package hash would make
building after a commit impossible; published consumers should lock immutable versions.

## Configuration, authentication and ownership

```csharp
using KaitenCloud.Sdk;
services.AddSingleton<IKaitenCredentialProvider>(
    new FileKaitenCredentialProvider(configuration["Kaiten:TokenFile"]!));
services.AddKaiten(options =>
{
    options.BaseUri = new Uri(configuration["Kaiten:BaseUrl"]!);
    options.RequestTimeout = TimeSpan.FromSeconds(10);
    // MaxRetries defaults to 0; at most 3 opt-in retries, GET/read-only OFREP only.
});
// Inject IKaitenClient. DI owns wrappers; IHttpClientFactory manages the pool.
```

The file provider rereads on **each request/attempt**. Rotate by atomically replacing
a private file (0600 on Unix). Implement `IKaitenCredentialProvider` for a vault or
other async source. Its result takes precedence over `KaitenClientOptions.BearerToken`,
including null (no credential). Keep secrets out of tracked config, telemetry and browsers.

Non-DI: reuse `KaitenClient.Create(options, provider)` for the application lifetime,
dispose at shutdown; it owns HTTP. `new KaitenClient(httpClient, options, provider)`
**never disposes or changes the caller's HttpClient**. Configure that client's pool,
timeout, redirects and diagnostics yourself; disable automatic redirects. Default
Authorization headers are rejected: configure auth through the SDK. A shorter
caller HTTP timeout wins and becomes a SDK timeout.

Use the **authenticated gateway**, e.g. `http://127.0.0.1:16060/api` for the isolated
local stack, never Core's unverified internal JWT listener. Normalization appends
`/api/` when absent, preserves proxy prefixes and does not duplicate an existing
`/api`. Example: `https://host/edge/kaiten` → `https://host/edge/kaiten/api/`.
Credentials/query/fragment in base URIs are rejected.

## Real runtime decisions

```csharp
var license = await client.ValidateInstanceLicenseAsync(instanceSlug, cancellationToken);
if (!license.Valid) throw new InvalidOperationException("License term is not active."); // Map to an explicit app denial.
var context = new Dictionary<string, object?>
{
    ["targetingKey"] = authorizedCustomerSlug,
    ["kaiten"] = new { instanceSlug = authorizedPrimaryInstanceSlug },
    ["userId"] = opaqueAuthenticatedUserId,
    ["orderCount"] = serverComputedOrderCount
};
var tracker = await client.EvaluateFlagAsync<bool>("delivery-tracking-enabled", context, cancellationToken);
var bulk = await client.EvaluateAllFlagsAsync(context, cancellationToken);
foreach (var flag in bulk.Flags)
{
    if (!flag.IsSuccess) throw new KaitenEvaluationException(flag); // No successful value to authorize with.
}
var usage = await client.GetInstanceEntitlementUsageAsync(instanceSlug, "monthly-orders", cancellationToken);
await client.ReportNumericUsageAsync(instanceSlug, "monthly-orders", 1,
    UsageReportBehavior.Append, cancellationToken: cancellationToken);
```

All APIs are async/cancellable. No local CEL, date/license, cap or percentage
evaluation; no cache, ETag optimization, subscription or live-push mechanism.
Current Core changes appear on the **next request**; UI refresh/polling is separate.
Revalidate protected actions on the backend, not just by hiding controls.

License validation has eight required fields. HTTP200 with `Valid=false` is a real
denial, not an outage. Core uses DB UTC `[startLicenseDate,endLicenseDate)` and
Active/NotStarted/Expired. Missing/null/unknown/contradictory decisions fail as
protocol errors. Catalog DRAFT/ARCHIVED and operational MAINTENANCE/CHURNED do not
suspend assigned instances. Requires corrected Core: [contract](docs/core-contract.md).

## Public capabilities

| API | Result / PAT scope |
|---|---|
| GetCustomerAsync | Customer / read:customers |
| GetInstanceAsync, ValidateInstanceLicenseAsync | Instance, LicenseValidation / read:instances |
| GetLicenseAsync | License / read:licenses |
| GetInstanceEntitlementUsageAsync, GetAllInstanceEntitlementUsageAsync | usage, list / read:instances |
| ReportNumericUsageAsync | usage / write:instances |
| EvaluateFlagAsync, EvaluateFlagAsync<T>, EvaluateAllFlagsAsync | raw/typed result, BulkFlagEvaluation / read:feature_flags |
| GetFeatureFlagAsync, UpdateFeatureFlagAsync | flag, full PUT / read:feature_flags, write:feature_flags |
| UpdateInstanceAsync | full PUT / write:instances |
| GetLicenseEntitlementAsync, GetLicenseEntitlementsAsync | grant, cursor page / read:licenses |
| UpdateLicenseEntitlementAsync | grant PUT / write:licenses |

Use a **separate presenter credential**. `resource.ToUpdateRequest() with { ... }`
preserves supported writable fields/metadata and excludes read-only/additive response
fields. Flag `default_variant`/`event_name` retain snake_case. Slugs are writable:
preserve unless deliberate rename. Instance PUT cannot set integrations/status/lifecycle;
omitted deployment zone preserves it (clearing unsupported). Grant update preserves
value and numeric overage percent. Full PUT has no conditional concurrency guard.

Entitlement wire types are lowercase **number / boolean / object**; `event_count`
is response-only. `Append` folds by Core aggregation (SUM increments; LATEST reports
the latest value); `Set` overwrites and resets observation count to1. Both are explicit.
-1 limits mean unlimited; missing limits are not permission to assume unlimited.

PATs are **vendor-org-wide**, not restaurant/customer-scoped. `write:instances`
permits more than usage reporting. The app must validate Keycloak membership/roles,
derive allowed customer/instance server-side, and never expose a generic Core proxy.

## Failures, diagnostics and verification

[HTTP behavior](docs/http-behavior.md) distinguishes API problems (StatusCode/Code/ErrorId),
evaluation, protocol, credentials, network, timeout and caller cancellation. None
becomes allowed/default. Usage POST and **all PUTs** never automatically replay.

ILogger emits operation/status only. ActivitySource `KaitenCloud.Sdk` emits redacted
operation spans, propagates W3C traceparent/tracestate, and attaches Core
`Kaiten-TraceId` as `kaiten.trace_id`. Add this source to your OpenTelemetry exporter.
The SDK disables factory HTTP loggers/default handler propagation; it never logs token,
context, body, route arguments or remote error text. Host HttpClient instrumentation
may still capture full URLs: configure redaction or avoid indiscriminate HTTP tracing.
Problem/Evaluation properties retain server data; do not blindly log them.

The **.NET10 example has no direct HTTP Core bypass**:

```sh
KAITEN_BASE_URL=http://127.0.0.1:16060/api KAITEN_TOKEN_FILE=/private/path/runtime-token task example
# Same executable consuming the packaged SDK:
KAITEN_BASE_URL=http://127.0.0.1:16060/api KAITEN_TOKEN_FILE=/private/path/runtime-token dotnet run --project tests/PackageConsumer -c Release --no-build --no-restore
```

[Real integration](docs/integration.md) covers flags, grants/usage, term states,
presenter isolation, auth denials, outage, rollback and Core/Envoy Jaeger correlation.
Missing prerequisites **fail**; mocks/skips never count as real integration.

Gaps: no Platform/billing/customer-session APIs, general CRUD/listing, catalog
publish/archive/unarchive, SSE/OpenFeature provider, offline cache or usage idempotency.
Core's secondary-instance flag license facts still use the customer's oldest instance;
periodic quantity enrichment also has a separate known limitation. Use verified
primary instances, not invented parity. Test audit/outbox events remain after
snapshot restoration; legitimate observations are never deleted.

Apache-2.0: [LICENSE](LICENSE), [NOTICE](NOTICE), [DCO](DCO.md), [contributing](CONTRIBUTING.md),
[security](SECURITY.md).
