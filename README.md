# Kaiten SDK for .NET

Typed **server-side** Kaiten Core APIs and integration with the **official
OpenFeature SDK + sealed OFREP provider**. Package/namespace: **KaitenCloud.Sdk**,
local unpublished version **0.2.0**, targeting **net8.0 / C#12**.

**Breaking change:** direct flag evaluation APIs/models/exceptions are removed.
All flag decisions use OpenFeature's `IFeatureClient` instead. License validation,
entitlement usage/reporting, management and presenter APIs remain typed Kaiten calls.
The single package pins `OpenFeature.Hosting 2.14.1` and
`OpenFeature.Providers.Ofrep 0.1.7`; the provider already depends on Hosting, so a
separate integration package would not reduce this integration's dependencies.

## Build and local consumption

Prerequisites: .NET 10 SDK (global.json), .NET 8 runtime for tests, NuGet.org on
initial restore. No global upgrades, package publication or services are required.

```sh
task check
# Or:
dotnet restore KaitenCloud.sln --locked-mode
dotnet build KaitenCloud.sln -c Release --no-restore
dotnet test tests/KaitenCloud.Sdk.Tests -c Release --no-build --no-restore
dotnet pack src/KaitenCloud.Sdk -c Release --no-build --no-restore -o artifacts/packages
sh scripts/check-package.sh
```

Use `artifacts/packages/KaitenCloud.Sdk.0.2.0.nupkg` from a local NuGet feed
alongside NuGet.org, never a workstation-specific source reference:

```xml
<PackageReference Include="KaitenCloud.Sdk" Version="0.2.0" />
```

Restore changing unpublished bytes into a fresh project-local package cache; never
clear global caches. Published consumers should lock immutable versions. SDK,
unit tests, example and integration dependencies have committed locks; the local
consumer regenerates its changing unpublished-package hash in ignored obj/.

## Server DI and credentials

```csharp
using KaitenCloud.Sdk;
using OpenFeature;

builder.Services.AddSingleton<IKaitenCredentialProvider>(
    new FileKaitenCredentialProvider(builder.Configuration["Kaiten:TokenFile"]!));
var gateway = new Uri(builder.Configuration["Kaiten:BaseUrl"]!);
builder.Services.AddKaiten(o => { o.BaseUri = gateway; o.RequestTimeout = TimeSpan.FromSeconds(10); });
builder.Services.AddKaitenOpenFeature(o =>
{
    o.BaseUri = gateway;
    o.RequestTimeout = TimeSpan.FromSeconds(10);
    o.MaxResponseBytes = 4 * 1024 * 1024;
});
// Inject IKaitenClient for Core APIs and official IFeatureClient for flags.
```

Add once per host. Official Hosting initializes/shuts down the provider with the
ASP.NET/generic host and owns an isolated OpenFeature API per service container.
The registration selects the **default domain**; do not mutate global/client context
for tenants or use `Api.Instance` instead of the injected client.
Plain `ServiceCollection` applications explicitly await
`IFeatureLifecycleManager.EnsureInitializedAsync(token)` and `ShutdownAsync()`.

Both transports share `IKaitenCredentialProvider`. The file provider rereads on
every request (Core retries also reread); atomically replace a private 0600 file to
rotate. An async vault provider is also supported. The credential source overrides
static `BearerToken`, including null for anonymous access; a failing source does
not fall back to static/anonymous credentials. PATs stay **server-only**, never in a
browser, tracked config, logged context or request body.

Use the authenticated gateway, not Core's internal listener. `BaseUri` appends
`/api/` if absent, preserves proxy prefixes and accepts an existing `/api/`:
`https://host/edge/kaiten` becomes `https://host/edge/kaiten/api/`.
The official provider appends `ofrep/v1/evaluate/flags/{key}` itself. Base URI
credentials, query and fragment are rejected. Redirects are disabled.

Non-DI Core calls can reuse `KaitenClient.Create(options, credentials)` and dispose
at shutdown. `new KaitenClient(http, options, credentials)` never alters/disposes
caller HTTP. For a non-DI official provider with static credentials see
[upstream documentation](https://github.com/open-feature/dotnet-sdk-contrib/tree/main/src/OpenFeature.Providers.Ofrep).
Use the DI integration for rotating credentials, envelope validation and safe telemetry.

## Per-request decisions: availability is not false

```csharp
using OpenFeature.Constant;
using OpenFeature.Model;

// Identity/tenant/instance/facts must come from authorized backend state.
var context = EvaluationContext.Builder().SetTargetingKey(authorizedCustomerSlug)
    .Set("kaiten", new Value(new Structure(new Dictionary<string, Value>
    { ["instanceSlug"] = new(authorizedPrimaryInstanceSlug) })))
    .Set("userId", opaqueAuthenticatedUserId).Set("orderCount", serverOrderCount).Build();
var detail = await flags.GetBooleanDetailsAsync("delivery-tracking-enabled", false,
    context, cancellationToken: cancellationToken);
if (detail.ErrorType != ErrorType.None || detail.Reason == Reason.Error)
    throw new InvalidOperationException("Flag service unavailable."); // e.g. HTTP503, not flag-off.
var license = await kaiten.ValidateInstanceLicenseAsync(authorizedPrimaryInstanceSlug, cancellationToken);
var entitlement = await kaiten.GetInstanceEntitlementUsageAsync(authorizedPrimaryInstanceSlug,
    "delivery-tracking", cancellationToken);
var allowed = license.Valid && ((BooleanEntitlementValue)entitlement.Value).Value && detail.Value;
// A flag is not authorization. Also enforce app membership/roles and usage quotas.
```

Always use details and a false fallback for protected boolean actions. **Check BOTH
ErrorType and Reason:** upstream 0.1.7 maps internal `general_error` to
`ErrorType.None` while retaining `Reason.ERROR` for outages/timeouts/rate limits.
The internal HTTP envelope guard rejects missing/null values, wrong/missing keys,
unknown error codes, duplicate fields and oversized/malformed successful bodies
**before the official provider decodes them**. It neither evaluates rules nor
interprets typed values. Official provider typing handles type mismatch. Legitimate
server `DEFAULT` and `DISABLED` decisions (including true/false/zero) remain available.

The provider sends a single request for each call: **no flag cache, bulk API,
polling, ETag/304, refresh thread, SSE or retry loop**. Changes become visible on the
next request. HTTP429 causes an official **provider-wide Retry-After cooldown**
(default one minute without advice), during which calls return unavailable without
network traffic, including other tenants/flags. UI refresh is application-owned.

License validation is authoritative DB UTC `[start,end)`, with exact
ACTIVE/NOT_STARTED/EXPIRED states. HTTP200 `Valid=false` is a denial, not an outage;
missing/contradictory terms are protocol failures. DRAFT/ARCHIVED catalog states and
MAINTENANCE/CHURNED instance states do not suspend assigned licenses. Requires the
corrected [Core contract](docs/core-contract.md).

## Core API capabilities

| API | PAT scope |
|---|---|
| GetCustomerAsync | read:customers |
| GetInstanceAsync, ValidateInstanceLicenseAsync | read:instances |
| GetLicenseAsync | read:licenses |
| GetInstanceEntitlementUsageAsync, GetAllInstanceEntitlementUsageAsync | read:instances |
| ReportNumericUsageAsync | write:instances |
| GetFeatureFlagAsync, UpdateFeatureFlagAsync | read:feature_flags / write:feature_flags |
| UpdateInstanceAsync | write:instances |
| GetLicenseEntitlementAsync, GetLicenseEntitlementsAsync | read:licenses |
| UpdateLicenseEntitlementAsync | write:licenses |

OFREP through OpenFeature requires read:feature_flags. PATs are vendor-org-wide,
not customer-scoped: derive allowed tenants on the backend, never expose a generic
Core proxy. Keep a separate presenter credential/container from the runtime host.

`ToUpdateRequest() with { ... }` preserves supported writable fields and excludes
read-only/additive fields. Full PUT has no conditional concurrency guard. Entitlement
wire types are lowercase number/boolean/object; event_count is response-only.
Append and Set are explicit; Set resets count to one. A mutation timeout may have
committed: reconcile uncertainty, never automatically replay usage POST or any PUT.

## Safe diagnostics and verification

[HTTP behavior](docs/http-behavior.md) separates typed Core exceptions from official
OpenFeature details. The integration disables factory HTTP loggers and the unsafe
upstream provider/client logger categories (they include keys/exception messages).
Only operation/status is logged. Subscribe to `KaitenCloud.Sdk` and
`KaitenCloud.Sdk.Ofrep` ActivitySources for redacted spans and W3C
traceparent/tracestate plus validated Core `Kaiten-TraceId` correlation.
Do not indiscriminately export System.Net.Http/OpenFeature activities or add logging
hooks: upstream instrumentation can contain URLs, flag keys/values/error messages.
Never log request context, tokens, bodies, `Problem` or `ErrorMessage` wholesale.

The [.NET10 example](examples/Kaiten.Example/Program.cs) is read-only and consumes
OpenFeature for flags, typed Kaiten calls for licenses/entitlements. Set
KAITEN_BASE_URL and KAITEN_TOKEN_FILE. Successful denial exits 2, unavailability 1.
[Real integration](docs/integration.md) creates a separate owned Core environment,
checks flags/guards/mutations/tenant isolation/outage/Jaeger, restores snapshots,
and removes only owned resources. Missing prerequisites fail, never skip/mock.

No Platform/billing/customer-session APIs, broad CRUD, offline cache, usage idempotency
or secondary-instance parity is claimed. Core enrichment currently uses the
customer's oldest instance; use verified primary instances. Periodic quantity
enrichment has a separate known limitation.

Apache-2.0: [LICENSE](LICENSE), [NOTICE](NOTICE), [DCO](DCO.md), [contributing](CONTRIBUTING.md),
[security](SECURITY.md).
