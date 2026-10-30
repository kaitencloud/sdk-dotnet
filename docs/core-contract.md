# Verified Core contract (2026-10-30)

Authoritative sources in the current Core working tree: app/openapi.yaml;
api/internal/modules/instances/schema/{schema.go,license_validation.go};
instances/{updateinstance,reportentitlementusagemetric}/endpoint.go;
featureflags/schema/{schema.go,default_variant.go,targeting*.go};
featureflags/openfeature/ofrep/schema.go; licenses/schema/schema.go and
updatelicenseentitlement/endpoint.go; customers/schema/schema.go.

Routes relative to authenticated /api gateway:

| Route | Contract |
|---|---|
| GET customers/{slug}, instances/{slug}, licenses/{slug} | typed current entities |
| GET instances/{slug}/license-validation | authoritative term decision |
| GET instances/{slug}/entitlements/{entitlement}/usage | one usage/grant |
| GET instances/{slug}/entitlements/usage | all usage (array or null/empty) |
| POST instances/{slug}/entitlements/{entitlement}/usage | number value, explicit append/set |
| POST ofrep/v1/evaluate/flags/{slug}, ofrep/v1/evaluate/flags | context, per-flag failures |
| GET / PUT feature-flags/{slug} | typed representation / full write,204 |
| PUT instances/{slug} | full write,204 |
| GET licenses/{slug}/entitlements[?cursor&limit] | items,nextCursor,hasMore |
| GET / PUT licenses/{slug}/entitlements/{entitlement} | grant / value+overage write,204 |

License validation requires valid,status,evaluatedAt,instanceSlug,licenseId,
licenseSlug,startLicenseDate,endLicenseDate. Core added this endpoint and fixed UTC
offset persistence; an older checkout lacking it cannot satisfy this runtime contract.
Valid iff ACTIVE; other states NOT_STARTED/EXPIRED; exact wire strings only, not
composite/numeric/case/whitespace aliases; DB UTC [start,end). Single usage read and
numeric-report responses must match the requested entitlement slug. This package
provides no Core edits or database migrations.

OFREP OpenAPI makes value optional, but SDK successful evaluation requires an actual
value. False/0 remain successes. Bulk retains per-flag errors, metadata and root
additive fields. Case-sensitive entitlement discriminators number/boolean/object,
not the historical Go wrapper's uppercase NUMBER. event_count is never written.

Strict full flag write: type,variants,targetings,name,description,default_variant,
slug,metadata,enabled,event_name. Targeting/default unions support basic,
rollout_date,rollout_percentage. Instance write: name,description,customerId,
licenseId,startLicenseDate,endLicenseDate,metadata,optional deploymentZoneId/slug;
never integrations/status. Grant write: value/optional limitCapExceededOveragePercent.
ToUpdateRequest excludes read-only/additive response fields because Core rejects
unknown request keys. KaitenJson.CreateOptions provides wire rules for private snapshots.

No ETag/304: current bulk handler doesn't implement the advertised behavior. No
browser PAT, arbitrary evaluation instant, local CEL/license logic, archive-as-suspension,
reseller hierarchy or secondary-instance parity. Current enrichment uses the customer's
oldest instance for license/grant facts; Sushi tests use verified primary production
instances and opaque extra userId/server orderCount. App roles/membership are independent.
