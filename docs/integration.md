# Real official-provider integration checks

Use **only a new exclusively owned temporary Core** seeded with Kaiten Sushi,
license-validation/UTC correction, and Core/Envoy Jaeger. Never mutate the live
Demo or a shared/production database. Existing environments must not run this
mutation runner concurrently. All flags use official OpenFeature IFeatureClient
with sealed OfrepProvider; license/grant/usage/presenter operations use typed
Kaiten APIs. Only telemetry queries use direct HTTP (to Jaeger, not Core).

## Isolated fixture and cleanup

`scripts/integration-core.py` accepts the verified Core source directory (config
mounts only) and four already-built local images. It pins their image IDs and never
builds, pulls, retags, updates or restarts existing runtimes:

```sh
python3 scripts/integration-core.py --core-source /path/to/corrected/kaiten \
  --api-image existing-corrected-api --seeder-image existing-core-seeder \
  --admin-image existing-core-migrate --bootstrap-image existing-core-bootstrap
```

The script generates a random kaiten-sdk-ofrep-* Compose project, private 0700
credential directory/0600 files, new named PostgreSQL volume with an ownership
label, isolated network/Rabbit/CDC volumes and two available loopback ports. No
credentials or env files from other stacks are read/copied. API, bootstrap and
seeder share host UID/GID and the same new mount. Native admin tools explicitly
mount /credentials with that UID when minting tokens. A foreign empty organization
is created through native `organization ensure`, not direct DB writes.

Missing images/config/readiness/scopes fail. The new database alone is migrated
and seeded through supported Core tools. Runtime/presenter/under-scoped/foreign
private tokens belong only to it. Finally, Compose removes only the random project
and its volumes; the separately created PostgreSQL volume is removed only after
its ownership label matches. This invocation's credentials/DB env secrets are
deleted after cleanup; private snapshots/receipt/bootstrap.log remain under
ignored `.local/<project>`. Cleanup errors fail, never silently claim success.

## Runner prerequisites and coverage

The executable requires KAITEN_BASE_URL, KAITEN_TEST_TOKEN_DIR, KAITEN_JAEGER_URL,
KAITEN_TEST_MUTATIONS=1, and KAITEN_TEST_ENVIRONMENT=kaiten-sdk-ofrep-<owned-id>.
Missing prerequisites exit nonzero. The fixture sets these itself. After build:
`dotnet run --project tests/KaitenCloud.Sdk.Integration -c Release --no-build --no-restore`.
Do not fabricate the ownership acknowledgement to target another environment.

Verified seed: Ninja ninja-osaka-prod/starter, Sakura sakura-tokyo-prod/premium-v2,
active licenses, tracker false/true, monthly-orders SUM observations 1–100 and room
for one increment. The runner refuses changed/unrelated baselines.

Nine real groups cover typed resources/number/boolean/object reads; official
OpenFeature four boolean/three object flags and concurrent tenant contexts; real
missing/invalid/wrong-scope/foreign auth; presenter targeting and next-request
propagation; enabled flag versus denied entitlement and expired/future license
guards, UTC offsets/active restoration; grant change and Ninja license upgrade
with Sakura unchanged; append/set/exact quota409/rejected-write rollback; real
Core/provider closed-port outage; redacted provider traceparent/Core trace ID and
a matching nonempty Jaeger trace. No manual OFREP evaluator/HTTP bulk probe exists.

Writable snapshots are saved before changes, restored in finally with independent
bounded SDK calls, then compared. SUM Set resets count to one, so bounded zero
Appends restore the original observation count too. Snapshots have no credentials.
Audit/outbox events legitimately remain during tests; deleting the exclusively
owned temporary environment removes only test history, never another stack's data.

The redacted receipt has group descriptions, trace ID and span count. Host code
exports SDK spans if desired; no exporter is bundled. Read-only source/package
examples use KAITEN_BASE_URL and KAITEN_TOKEN_FILE and enforce details/term/grant
guards; no Demo/Keycloak/Kubernetes completion is claimed by SDK tests.
