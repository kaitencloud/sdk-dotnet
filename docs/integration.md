# Real integration checks

Require **isolated, demo-owned** Core seeded with Kaiten Sushi, license-validation
endpoint/UTC correction, and Core/Envoy Jaeger. Never target a shared/production
system or Core's unverified internal JWT listener.

Baseline: Sakura sakura-tokyo-prod/premium-v2; Ninja ninja-osaka-prod/starter,
active terms, tracker true/false, monthly-orders SUM count1–100 with room for one
increment (standard seed18/20). Different tenant/license baselines fail, not overwrite.
Do not mutate concurrently: full PUT has no conditional version guard.

Private files: runtime-token, presenter-token, under-scoped-token (lacks
read:instances), foreign-token (separate empty org). Never paste values into commands.
Runtime scopes: read:feature_flags/read:instances/write:instances/read:licenses/
read:customers/read:entitlements. Presenter adds write:feature_flags/write:licenses.
All Core requests, including presenter PUTs, go through this SDK. Only Jaeger
trace queries use direct HTTP, to telemetry, not Core.

```sh
KAITEN_BASE_URL=http://127.0.0.1:16060/api KAITEN_TEST_TOKEN_DIR=/private/path/dev KAITEN_JAEGER_URL=http://127.0.0.1:17686 KAITEN_TEST_MUTATIONS=1 task integration
```

Equivalent after build: `dotnet run --project tests/KaitenCloud.Sdk.Integration -c Release --no-build --no-restore`.
Missing prerequisites/Core exit nonzero; no mocked/skipped fallback. Separate from
infrastructure-free unit tests.

Coverage: typed entities/all union values, single/bulk evaluations, missing flag,
missing/invalid/wrong-scope/foreign auth, full targeted flag PUT/next-request visibility,
expired/future/active terms and offset instants, grant mutation and Ninja Standard-v2
upgrade/tracking, Sakura unchanged, append/set/exact quota409/rejected-write rollback,
real outage, SDK traceparent → Core Kaiten-TraceId → nonempty matching Jaeger trace.

Before mutation: snapshots in ignored .local/integration/<timestamp> (or
KAITEN_TEST_SNAPSHOT_DIR), Unix directory0700/files0600. Finally restores even on
failure/ambiguity using independent bounded SDK requests. Restoration failure is hard:
stop and apply saved typed requests through SDK before continuing. No token values saved.
The usage snapshot explicitly saves originalValue/originalEventCount (wire request serialization omits event_count).
Read-only updated timestamps and audits legitimately advance; restoration covers writable fields.
Exact SUM aggregate/event_count rollback uses Set then bounded zero Append observations;
Set alone resets count to1. Audits/outbox events remain; never delete history, reseed orgs
or reset DBs. This is not arbitrary AVERAGE/LATEST/unknown-counter rollback.

Redacted receipt.json contains checks/traceId/span count, not identities/bodies. View
trace in configured Jaeger. The runner verifies SDK propagation to Core/Envoy spans,
not export of an SDK span (no SDK exporter bundled; host registers ActivitySource).
.NET10 source and packaged examples are read-only, need only KAITEN_BASE_URL and
KAITEN_TOKEN_FILE. No demo/Keycloak/Kubernetes completion is claimed here.
