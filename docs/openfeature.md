# Consumer-owned official OpenFeature / OFREP

The Kaiten SDK has no feature evaluation API, OpenFeature package dependency or
provider/transport wrapper. Install official `OpenFeature 2.14.1` and
`OpenFeature.Providers.Ofrep 0.1.7` in the consuming application. The
[official provider](https://github.com/open-feature/dotnet-sdk-contrib/tree/209b6a799e2cdd5ce8bbaa7556d270e80cd2a583/src/OpenFeature.Providers.Ofrep)
is sealed. Its public constructor takes `OfrepOptions` from
`OpenFeature.Providers.Ofrep.Configuration`. See the compiled
[example](../examples/Kaiten.Example/Program.cs) and [README](../README.md).

## Kaiten endpoint, authentication and targeting

Use the full authenticated gateway base `http://127.0.0.1:<gateway-port>/api/`
for a separately owned local Core, or the deployed HTTPS gateway with its proxy
prefix and `/api/`. Do not use a management instance URL or append `/ofrep/v1`
in the base. The provider ensures a trailing slash, then POSTs relative
`ofrep/v1/evaluate/flags/{key}`; it does not add Kaiten's `/api` segment.
These paths and the bearer requirement are pinned in `contracts/core.openapi.json`.

Set `Headers["Authorization"] = "Bearer " + runtimeToken` and `Timeout` on
`new OfrepOptions(baseUrl)`. The bearer scopes the organization and needs
`read:feature_flags`. Per invocation, use `EvaluationContext` with the authorized
customer slug as targeting key and nested `kaiten.instanceSlug`. Derive tenant,
instance and other facts from trusted server state, not arbitrary browser claims.
PATs are organization-wide; they are not a customer authorization boundary.
Core enrichment currently uses the oldest customer instance; no secondary-instance
or periodic-quantity parity claim is made here.

Await `Api.Instance.SetProviderAsync(new OfrepProvider(config))` before getting
the official client. The application owns shutdown (and global/domain lifecycle),
credential replacement and refresh UX. The sample snapshots its bearer at setup;
`IKaitenCredentialProvider` continues to rotate **Core client** credentials per
request but is not implicitly connected to the official provider.

## Native HTTP factory configuration

For host/container lifecycle and explicit transport control, use the official
Hosting/OFREP APIs directly (the provider package depends on Hosting):

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenFeature.Hosting;
using OpenFeature.Providers.Ofrep.DependencyInjection;

services.AddHttpClient("app-ofrep")
    .RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    { AllowAutoRedirect = false });
services.AddOpenFeature(builder => builder.AddOfrepProvider(options =>
{
    options.BaseUrl = baseUrl; // Full trusted HTTPS Kaiten /api/ gateway.
    options.Timeout = TimeSpan.FromSeconds(10);
    options.Headers["Authorization"] = "Bearer " + runtimeToken;
    options.HttpClientName = "app-ofrep";
}));
```

This is consumer configuration, not a Kaiten extension. Generic/ASP.NET hosts
manage initialization/shutdown through official Hosting; plain DI must explicitly
call its `IFeatureLifecycleManager`. Native options also expose HTTP client
configuration; do not invent an unsupported public provider constructor taking
`HttpClient`. Disable redirects for sensitive contexts; the simple constructor's
default HTTP handler does not supply that restriction. Provider/client logging
and activity exporters can expose keys/values/error messages; configure redaction
or suppression in the application. Do not export raw context, tokens or bodies.

## Failure behavior and removed guarantees

Always use boolean details with a false fallback for protected actions. Before
Value, check **ErrorType != None OR Reason == ERROR**. Native401/403 map
ProviderNotReady;404 maps FlagNotFound. Outage/network/timeout/redirect/429 can
return ErrorType.None with Reason.ERROR in0.1.7. Cancellation may be error details
rather than OperationCanceledException. Legitimate DEFAULT/DISABLED false/true
values remain available. Flags cannot bypass typed authoritative license-term,
entitlement, membership/role or quota checks.

> [!WARNING]
> The fallback and ErrorType/Reason guard is necessary but **not sufficient** for
> protected side effects. The supplied isolated **provider0.1.7** repros show:
> - HTTP400 `{"key":"delivery-tracking-enabled","value":true}` without `errorCode`
>   returns Value=true, ErrorType.None and empty Reason even with a false fallback.
> - HTTP200 with a key different from requested `new-ordering-flow` and value=true
>   raises no dependency error and can permit a reservation.
> No released protocol fix was identified in the supplied version check. Applications
> must validate HTTP status, success/error envelope and requested flag key at their
> **application HTTP boundary** before relying on provider details for side effects.
> The official OfrepProvider remains the sole evaluator; this SDK adds no wrapper,
> transport or replacement provider. The minimal examples assume trustworthy,
> protocol-valid responses, not protection against these malformed payloads.

The SDK-owned strict HTTP200/400 envelope guard has been removed with the wrapper.
No SDK guarantee remains for required/matching keys, missing/null values, duplicate
fields, unknown error codes, response-size/depth limits, credential deadlines,
exception-message redaction or `KaitenCloud.Sdk.Ofrep` trace correlation. The
upstream provider can substitute fallback values or keys for incomplete successes;
ErrorType/Reason checks do not turn it into a strict schema validator. Tests now
exercise supported native behavior and consumer transport configuration, not the
removed guard. Core model/usage/license strict validation and Core telemetry are
unchanged. Do not describe historical wrapper tests as current guarantees.

The already shared `0.3.0-native-uncommitted` verification package and its recorded
source-manifest SHA256
`c1ae8ec9b37cd0239244ebebcda0e0dc1f83a3ba9c42dbf7346624e9929fe5be`
describe the validated **pre-warning snapshot**, not these later docs/comments.
That package was not rebuilt or repacked for this warning; neither it nor the
current uncommitted checkout is a clean release.

Every normal call sends one request, with no cache, polling, bulk API, ETag/304,
SSE or automatic retry. Changes appear on the next call. HTTP429 triggers official
provider-wide Retry-After cooldown (one minute without advice); other flags/tenants
sharing it are temporarily unavailable too. That is not tenant decision caching.
