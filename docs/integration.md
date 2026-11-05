# Explicit real-provider integration checks

Use **only a new exclusively owned temporary Core** seeded with Kaiten Sushi,
D55 instance-detail licenseTerm/UTC contract, and Core/Envoy Jaeger. Never mutate
a live Demo, shared stack or production database. The SDK no longer provisions
containers, volumes, databases or credentials. Environment creation/cleanup is an
explicit operator responsibility through supported native Core tools, outside
this SDK's build/check toolchain. Historical environment receipts are not fresh
verification of these revised sources.

The guarded C# executable uses official OpenFeature/OFREP with consumer-owned native
Hosting/HTTP factory configuration. Typed Kaiten APIs handle license/grant/usage
and presenter operations. Only telemetry queries use direct HTTP (Jaeger, not Core).

## Prerequisites

Prepare a separately authorized, exclusively owned fixture with private runtime,
presenter, under-scoped and foreign-organization token files. Do not copy credentials
from other stacks. Use private0700 directories/0600 files and maintain an explicit
resource ownership/cleanup record. Never target an existing/shared stack by merely
setting the acknowledgement variables. This runner performs business writes and
creates audit/outbox history; it requires exact action authorization before running.

Set KAITEN_BASE_URL including `/api/`, KAITEN_TEST_TOKEN_DIR, KAITEN_JAEGER_URL,
KAITEN_TEST_MUTATIONS=1, KAITEN_TEST_ENVIRONMENT=kaiten-sdk-ofrep-<owned-id>, and
optionally KAITEN_TEST_SNAPSHOT_DIR. Missing prerequisites exit nonzero.

```sh
dotnet run --project tests/KaitenCloud.Sdk.Integration -c Release --no-build --no-restore
```

Verified baseline required: Ninja ninja-osaka-prod/starter, Sakura
sakura-tokyo-prod/premium-v2, active licenses, tracker false/true, monthly-orders
SUM observations1–100 and room for one increment. The runner refuses changed or
unrelated tenants. Provider credentials snapshot at fixture construction; no SDK
credential bridge or provider trace wrapper exists.

## Coverage and restoration

The executable retains typed resources/number/boolean/object reads; four official
boolean/three object flags and concurrent trusted tenant contexts; missing/invalid,
wrong-scope/foreign authentication; targeting/next-request propagation; enabled
flags versus denied entitlements and expired/future licenses; UTC offsets and
active restoration; grant changes/instance upgrade with Sakura unchanged;
append/set/exact quota409/rejected-write rollback; closed-port outage; and **typed
Core SDK** redacted traceparent/Core trace ID correlation with real Jaeger.
No custom evaluator or HTTP bulk probe is introduced. Removed provider envelope,
credential-rotation and redaction guarantees are not tested or claimed here.
Protected-action examples assume trustworthy, valid OFREP responses: provider0.1.7
detail guards alone do not reject malformed HTTP400/wrong-key HTTP200 payloads.
Validate protocol at the application HTTP boundary before side effects; see the
[reported repros](openfeature.md).

Writable snapshots are saved before changes and restored in finally with independent
bounded SDK calls, then compared. SUM Set resets count to one; bounded zero Appends
restore the original count too. Cleanup/restoration errors fail; private snapshots
must be used for supported restoration before continuing. Snapshot files contain
no credentials. Audit/outbox events legitimately remain; remove only the exclusively
owned environment afterward using its ownership record, never another stack's data.

The receipt records groups, typed Core trace ID and span count. Host code controls
provider instrumentation. The read-only source/package example only needs
KAITEN_BASE_URL and KAITEN_TOKEN_FILE. SDK builds/tests do not claim completion of
Demo, auth infrastructure or runtime deployments.
