# Kaiten .NET SDK

Typed Kaiten Core management, licensing and entitlement APIs for server-side .NET.
Package/namespace: **KaitenCloud.Sdk**, target **net8.0**. The SDK does not evaluate
feature flags and has no OpenFeature dependency, provider, transport wrapper, bulk
evaluator, local rule engine or decision cache. Consumers import the official
OpenFeature SDK and official OFREP provider independently.

## Install and verify

```sh
dotnet add package KaitenCloud.Sdk
# Only applications that evaluate flags need these official packages:
dotnet add package OpenFeature --version 2.14.1
dotnet add package OpenFeature.Providers.Ofrep --version 0.1.7

dotnet restore KaitenCloud.sln --locked-mode
dotnet build KaitenCloud.sln -c Release --no-restore
dotnet run --project tools/KaitenCloud.ContractTool -c Release --no-build --no-restore -- contracts
dotnet run --project tools/KaitenCloud.ContractTool -c Release --no-build --no-restore -- generate --check
dotnet run --project tools/KaitenCloud.ContractTool -c Release --no-build --no-restore -- fixtures --check
dotnet test tests/KaitenCloud.Sdk.Tests -c Release --no-build --no-restore
# Revised verification bytes, not historical 0.3.0 or a committed release:
dotnet pack src/KaitenCloud.Sdk -c Release --no-build --no-restore -p:PackageVersion=0.3.0-native-uncommitted -o artifacts/native-sdk-uncommitted/packages
dotnet run --project tools/KaitenCloud.ContractTool -c Release --no-build --no-restore -- package
```

With Task installed, `task check` runs the same checks. Only .NET is required for
the active build/generation/test/package toolchain. Contracts are pinned and
standalone; no Core, brain, schema download or generator framework is needed.
See [generation and contract coverage](docs/code-generation.md).

## Typed Core client

```csharp
using KaitenCloud.Sdk;

builder.Services.AddSingleton<IKaitenCredentialProvider>(
    new FileKaitenCredentialProvider(tokenFile)); // Private server-side file, reread per Core request.
builder.Services.AddKaiten(options => options.BaseUri = new Uri(baseUrl));
```

Use the authenticated gateway base, including `/api` and any reverse-proxy prefix:
for a local owned Core this is `http://127.0.0.1:<gateway-port>/api/`. Use HTTPS
outside a trusted local fixture. Core options normalize the gateway prefix;
the official provider **does not add `/api` for you**.

Core defaults: 30s operation deadline, 4MiB body limit, depth64, zero retries.
Optional bounded retries apply to selected GET failures only. Never replay PUT,
usage POST, credential failure, cancellation, protocol error or business denial.
Core `IKaitenCredentialProvider` and `BearerToken` do not configure OpenFeature.

## Runtime flags: direct official provider

```csharp
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OpenFeature.Providers.Ofrep;
using OpenFeature.Providers.Ofrep.Configuration;

// Full authenticated Kaiten gateway, NOT /ofrep/v1 and NOT an instance endpoint.
var config = new OfrepOptions(baseUrl)
{
    Timeout = TimeSpan.FromSeconds(10),
    Headers = new Dictionary<string, string>
    { ["Authorization"] = "Bearer " + runtimeToken }
};
using var provider = new OfrepProvider(config);
await Api.Instance.SetProviderAsync(provider);
var flags = Api.Instance.GetClient();

// Derive these from authorized server state, never browser-supplied tenant claims.
var context = EvaluationContext.Builder().SetTargetingKey(authorizedCustomerSlug)
    .Set("kaiten", new Value(new Structure(new Dictionary<string, Value>
    { ["instanceSlug"] = new(authorizedPrimaryInstanceSlug) })))
    .Set("userId", opaqueAuthenticatedUserId).Set("orderCount", serverOrderCount).Build();
// Assumes trustworthy, protocol-valid OFREP responses: these detail checks alone
// are not fail-closed in provider 0.1.7. Validate at the app boundary before side effects.
var detail = await flags.GetBooleanDetailsAsync("delivery-tracking-enabled", false,
    context, cancellationToken: cancellationToken);
if (detail.ErrorType != ErrorType.None || detail.Reason == Reason.Error)
    throw new InvalidOperationException("Flag service unavailable.");
var instance = await kaiten.GetInstanceAsync(authorizedPrimaryInstanceSlug, cancellationToken);
var term = instance.LicenseTerm ?? throw new KaitenProtocolException();
var entitlement = await kaiten.GetInstanceEntitlementUsageAsync(authorizedPrimaryInstanceSlug,
    "delivery-tracking", cancellationToken);
var allowed = term.Status == LicenseTermStatus.Active &&
    ((BooleanEntitlementValue)entitlement.Value).Value && detail.Value;
// Also enforce application membership/roles and usage quotas.
// At application shutdown: await Api.Instance.ShutdownAsync();
```

The provider POSTs `ofrep/v1/evaluate/flags/{key}` relative to that gateway. The
bearer credential selects the organization and requires `read:feature_flags`;
`targetingKey` selects the customer, and `kaiten.instanceSlug` carries the trusted
instance context. Core currently enriches from the customer's oldest instance:
use verified primary instances; secondary-instance and periodic-quantity parity
are not claimed.

For protected boolean actions use details, a **false fallback**, and **both**
ErrorType/Reason guards. A flag is not authorization. Provider0.1.7 can return
ErrorType.None with Reason.ERROR for outages/timeouts/rate limits. Legitimate
DEFAULT/DISABLED values are not errors. Every normal call makes one request;
no cache, bulk API, polling, SSE or retry loop. HTTP429 imposes provider-wide
Retry-After cooldown (one minute without advice), including other tenants/flags.

> [!WARNING]
> False fallback plus ErrorType/Reason checks alone do **not** guarantee fail-closed
> behavior in provider **0.1.7**. Reported HTTP400 `{"key":"delivery-tracking-enabled","value":true}`
> without `errorCode` returns true, ErrorType.None and empty Reason; HTTP200 with a
> wrong key and true value for `new-ordering-flow` also raises no dependency error.
> Protected side effects require trustworthy, valid OFREP responses: validate status,
> envelope and requested key at the **application HTTP boundary**. Keep the official
> provider as sole evaluator; no SDK wrapper is supplied. See [details and snapshot caveat](docs/openfeature.md).

Consumers own provider lifecycle, endpoint validation, static credential rotation,
transport policy and telemetry. The removed wrapper's strict HTTP200/400 envelope
validation, bounded response buffering, per-request credentials and redacted
`KaitenCloud.Sdk.Ofrep` spans **are no longer supplied**. Official provider defaults
can substitute fallbacks for missing values and do not provide those validation
guarantees. See [official integration and changed guarantees](docs/openfeature.md),
including native HTTP factory configuration for redirect/logging control.

## Licensing, entitlements and management

`GetInstanceAsync` and management `Instances.GetAsync` require the authoritative
read-only detail `licenseTerm` with exact ACTIVE/NOT_STARTED/EXPIRED and UTC
`evaluatedAt`. Missing/null/malformed term is a protocol/dependency failure,
never ACTIVE or a false entitlement. All three term states are successful reads;
the SDK does not enforce local expiry. List/create/nested representations may omit
this computed member. Assignment instants retain their absolute UTC instant.
DRAFT/ARCHIVED catalogs and MAINTENANCE/CHURNED instances do not imply suspension.
See the [pinned Core contract](docs/core-contract.md).

The twelve remaining original client signatures and64 typed management operations
remain intact. Register `AddKaitenManagement` independently for customers, instances,
license versions/families/grants, entitlements/groups, flag authoring/targeting
rehearsal/manifest and integrations. These are management APIs, not runtime flag
resolution. See [management APIs and paging](docs/management.md).

| API | Required scope |
|---|---|
| GetCustomerAsync | read:customers |
| GetInstanceAsync | read:instances |
| GetLicenseAsync | read:licenses |
| GetInstanceEntitlementUsageAsync, GetAllInstanceEntitlementUsageAsync | read:instances |
| ReportNumericUsageAsync, UpdateInstanceAsync | write:instances |
| GetFeatureFlagAsync, UpdateFeatureFlagAsync | read:feature_flags / write:feature_flags |
| GetLicenseEntitlementAsync, GetLicenseEntitlementsAsync | read:licenses |
| UpdateLicenseEntitlementAsync | write:licenses |

PATs are vendor-org-wide, not customer-scoped: derive authorized tenants on the
backend, never expose a generic Core proxy. Keep presenter credentials separate
from the runtime host. `ToUpdateRequest()` preserves supported writable fields and
excludes read-only/extensions. Full PUT has no conditional concurrency guard.
Entitlement tags are exact lowercase number/boolean/object; event_count is
response-only. Append and Set are explicit; Set resets count to one. A timed-out
mutation may have committed: reconcile uncertainty instead of replaying or
claiming an order succeeded. No usage idempotency, Platform/billing/customer-session
APIs or offline caching is claimed.

## Diagnostics and examples

[Core HTTP behavior](docs/http-behavior.md) applies only to the typed SDK.
`KaitenCloud.Sdk` activities carry redacted operation/status and validated Core
trace correlation; no equivalent SDK-owned OFREP source exists. Consumers must
control official provider/HTTP logging and exporters themselves. Do not log
context, tokens, bodies, Problem or ErrorMessage wholesale; upstream activities
can include URLs, flag keys/values and error messages.

The [.NET10 read-only example](examples/Kaiten.Example/Program.cs) uses official
OpenFeature+OFREP directly alongside typed license/entitlement calls. Set
KAITEN_BASE_URL including `/api/` and KAITEN_TOKEN_FILE. Successful denial exits2,
unavailability1. [Real integration](docs/integration.md) is an explicit guarded
C# executable requiring a separately authorized owned Core fixture; no environment,
database or credentials are provisioned by this SDK. Missing prerequisites fail.

Apache-2.0: [LICENSE](LICENSE), [NOTICE](NOTICE), [DCO](DCO.md),
[contributing](CONTRIBUTING.md), [security](SECURITY.md).
