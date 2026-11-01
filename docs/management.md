# Typed Core management

The additive `IKaitenManagementClient` lives in `KaitenCloud.Sdk.Management`.
`IKaitenClient` and its remaining twelve original methods are unchanged; the draft validation API is intentionally removed. All feature-flag
decisions still use official OpenFeature and its official OFREP provider.

```csharp
using KaitenCloud.Sdk;
using KaitenCloud.Sdk.Management;
using KaitenCloud.Sdk.Management.Models;

services.AddSingleton<IKaitenCredentialProvider>(new FileKaitenCredentialProvider(tokenFile));
services.AddKaitenManagement(o => o.BaseUri = gateway);
// AddKaitenOpenFeature remains a separate opt-in registration for flags.

var customers = await management.Customers.ListAllAsync(
    new ListCustomerQuery { Limit = 100 }, totalLimit: 250, cancellationToken: ct);
var instance = await management.Instances.GetAsync(instanceSlug, ct);
var term = instance.LicenseTerm ?? throw new KaitenProtocolException();
await management.Instances.UpdateStatusAsync(instanceSlug, PatchInstanceRequestStatus.Healthy, ct);
await management.Instances.UpdateLifecycleStageAsync(instanceSlug, "CHURNED", ct);
```

These are wire management operations, not local authorization/expiry logic.
Instance detail returns the authoritative DB-authored term information; clients
do not compare it to a local clock, rewrite dates or suspend archived licenses.
Scope/membership authorization and protected actions remain application/server
responsibilities. Runtime and presenter credentials should remain separate.

## Resource groups

64 distinct operations are wired and covered by full/minimal HTTP fixtures:

| Resource | Capabilities |
|---|---|
| Customers | CRUD, page/all/enumerable lists, integration-link CRUD |
| Instances | CRUD, patch, status/lifecycle conveniences, lists, audit filtering/pagination, usage reads, integration-link CRUD, read-only assigned license term on existing detail GET |
| Licenses | CRUD, publish/archive/unarchive, grant association/list/get/update/delete |
| LicenseFamilies | list/get with version/include filters, update family settings |
| Entitlements | catalog CRUD, aggregation/reset/window/display/unit metadata |
| EntitlementGroups | CRUD, membership add/remove, grouped usage |
| FeatureFlags | management CRUD, targeting context/lint/rehearsal, OpenFeature manifest metadata; no flag resolution |
| Integrations | lookup/upsert customer or instance by adapter and external ID |

Response/request records live in `KaitenCloud.Sdk.Management.Models`. They are
separate from legacy DTOs where schema requirements differ, with exact wire names,
read-only request-field exclusion and preserved additive response fields. Tagged
entitlement/targeting/default variants reuse existing strict Kaiten converters.
Only genuine schema-defined JSON slots (metadata, arbitrary values/context) use
JSON elements/dictionaries; unknown composed schemas fail generation.

Request enums serialize exact wire strings. Required-null fields remain present.
`Optional<T>` distinguishes omission from explicit null, false or zero:

```csharp
var patch = new UpdateLicenseFamilyRequest
{
    IsPublic = false,
    FallbackLicenseFamilySlug = new Optional<string?>(null)
};
// Other fields are omitted. No client-side trial/payment policy is computed.
```

The instance PATCH does not support clearing lifecycle stage; a blank value is
invalid. Nullable family fields retain explicit null; server semantics decide
what that null means. DateTimeOffset request/query values are written as UTC
instants without losing the original absolute offset meaning. Decimal-string
fields remain strings; the SDK does not calculate billing totals.

## HTTP, ownership and paging

Use `KaitenManagementClient.Create(options, credentials)` for an owned long-lived
pool and dispose at shutdown, or the optional DI extension. The constructor
accepting an HttpClient never changes or disposes it. Supplied clients must have
redirects disabled and safe handlers/loggers, just as with the legacy client;
Create/DI disable redirects and unsafe factory HTTP logging themselves.

Transport is shared internally with the existing client: fresh request/attempt
credentials, one deadline including body/credentials/backoff, bounded responses,
caller cancellation, safe fixed exceptions/logs/activities, W3C correlation.
No default retries. Optional retries apply to GET only, never management writes.
A write timeout/network failure can have committed: reconcile, do not replay.

Operations enforce their specific success status and JSON/empty-body contract.
Malformed/missing required values fail protocol; actual HTTP error status/code/
errorId/RFC9457 details remain available. Do not dump explicit server details or
response metadata into logs. Paths and query values are escaped; traversal/raw
URLs and OFREP calls are not public API escape hatches.

`ListAsync` returns one typed page. `ListEnumerableAsync` iterates lazily and
`ListAllAsync` collects or fails (never returns a silent partial list). Query
`Limit` is page size; `totalLimit` is a separate optional result ceiling. Filters
are cloned/preserved, the caller query is not mutated. Missing/repeated/cyclic
cursors fail. A whole enumeration has an operation deadline and owns no cache.
Schema-declared root-null collection results normalize to empty; nonnullable typed
list/dictionary entries cannot be null. Nullable JSON metadata slots remain valid.
Required grouped usage calls need a query with a nonempty instance value:

```csharp
var usage = await management.EntitlementGroups.GetUsageAsync(groupSlug,
    new GetUsageEntitlementGroupQuery { Instance = instanceSlug }, ct);
```

Required query parameters are validated before credentials or HTTP. Required page fields
and incomplete envelopes remain strict.

## Current boundaries

The explicit operation map now covers 65 unique endpoints:64 management operations
plus the existing unkeyed numeric-report operation. 172 permitted endpoints remain
pending. Billing/subscriptions/invoices/handoff, keyed metering/history/exports,
public/session/Platform clients, composed GraphQL snapshots and webhook verification
are not completed by this batch. No API/platform/provider compatibility claim is
inferred from a DTO alone. No runtime deployment/package publication is performed.

Generation and semantic exceptions are recorded in [code-generation.md](code-generation.md).
