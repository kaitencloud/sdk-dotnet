# Pinned Core resource contract

The self-contained schema is pinned to Core
`d55c6312a42e982e260cfe7796b8645b9ee4676c` (see `contracts/source.json`).
The existing `GET /api/instances/{instanceSlug}` retrieves the flat persisted
resource plus detail-only, read-only `licenseTerm: { status, evaluatedAt }`.
There is no validation action endpoint or wire `Valid` decision boolean.

`status` is exactly ACTIVE, NOT_STARTED or EXPIRED; `evaluatedAt` is the UTC
database instant. Existing license ID/slug/start/end dates remain flat assignment
data, and top-level operational `status` remains separate. Every term state
is a successful HTTP200 resource retrieval, not an access denial. Shared list,
create, event and GraphQL representations omit the optional computed member.

Both legacy `GetInstanceAsync` and management `Instances.GetAsync` require a complete
known term on fresh detail. Missing/null/malformed data is a protocol/dependency
failure, never ACTIVE/false entitlement. Other representations accept omission.
The SDK does not compare to local now, enforce global expiry, rewrite timestamps,
change billing policy or interpret archive/operational status as suspension.
Protected-action policy remains in the application alongside entitlements, roles
and official OpenFeature decisions.

Detail success is no-store. Existing credential/scope failures remain401/403;
missing/deleted/foreign remain404 GetInstance.NotFound; internal failures return
private correlated500 problems without partial resource. Same getInstance and
read:instances operation/scope; no pseudo-resource/relationship wrapper.

The draft ValidateInstanceLicenseAsync/management ValidateLicenseAsync and
LicenseValidation DTO/status enum were removed intentionally in local unpublished
0.3.0. The other12 original interface signatures remain. Do not treat historical
0.2.0 source/package receipts as the migrated contract or replace their bytes.

Writes keep the existing strict field set. Legacy ToUpdateRequest copies supported
assignment/date/metadata fields only; read-only licenseTerm and additive response
extensions never enter write/snapshot payloads. Generated management create/update
DTOs also exclude it (including nested inputs). PUT/PATCH remain204.

Entitlement usage still uses exact lowercase number/boolean/object tagged values.
Usage POST requires explicit append/set behavior; unkeyed usage and all existing
writes are never automatically replayed. No historical-date migration or secondary
instance/periodic flag enrichment parity is inferred here.

Runtime flags use only official OpenFeature+official OFREP with per-call trusted
context and both ErrorType/Reason guards; no direct SDK evaluator/bulk/cache or
custom provider is introduced by this resource migration.
