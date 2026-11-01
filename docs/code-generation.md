# Standalone contracts and deterministic STJ generation

`contracts/source.json` pins Core commit `d55c6312a42e982e260cfe7796b8645b9ee4676c`,
original git blobs/SHA256 and normalized JSON checksums. The source has228 Core
REST operations (226 after OFREP exclusions),11 Platform operations and117 separate
webhook declarations. Normal SDK builds/generation do not need Core or brain siblings.
No network fetch, tool installation, schema overlay or timestamp migration occurs.

## Accepted Batch1 templates

`contracts/batch1.json` explicitly selects64 operations. The standard-library Python
generator `scripts/generate-management.py` emits typed STJ records, request enums,
operation-specific method/path/status/auth metadata and resource methods. It runs
twice and byte-compares output. `--check` compares every emitted file and API
manifest with the checked-in tree. Normal CI runs it, fixture drift checks, coverage
guards and tests; no production runtime package/dependency is added.

```sh
python3 scripts/generate-management.py --check
python3 scripts/generate-management-fixtures.py --check
task check
# Explicit regeneration, followed by review:
python3 scripts/generate-management.py
python3 scripts/generate-management-fixtures.py
```

Never hand-edit `src/KaitenCloud.Sdk/Management/Generated/`. Update the approved
source/selection/templates, regenerate, and review the exact diff instead.
Unhandled root/reference object compositions (`oneOf`/`anyOf`/`allOf`) fail before
writing any generated files; explicit known value mappings are separate. Required
query flags are preserved in DTOs, method signatures, metadata and minimal fixtures.
Nonnullable typed collection members are checked independently of nullable containers.
Unsupported unions/closed schemas fail generation rather than becoming an arbitrary
whole-body dictionary. Simple object/ref/array/scalar models and known nullable/tagged
unions are supported. Arbitrary JSON is reserved for actual dynamic metadata/value
slots. Response enum strings remain forward-compatible; request enums are exact.
The draft validation method/DTO are intentionally removed (interface12); other
legacy identities and official flag integration remain intact. LicenseTerm maps to
a strict shared DTO instead of a permissive response enum string. Only detail GET
requires it; operation-aware fixtures omit it from create/list/nested resources.

Request read-only fields are removed, required nullable fields serialize even when
null, and optional fields use explicit presence. New records use STJ required-member
checks plus generated null/bounds guards; malformed successes are protocol failures.
This is not a general JSON Schema validator: server cross-field/business constraints
remain server decisions. No local CEL, flag, quota, renewal or term-expiry logic exists.

### Explicit schema compatibility detail

`FeatureFlag.metadata` is marked required object in the pinned OpenAPI but the
verified strict full-write/legacy SDK contract supports a required *nullable map*.
The `allowNull` exception/reason is recorded in `batch1.json`, tested, and applied to
new flag DTOs only. No original contract is rewritten. Existing targeted/default
variants reuse strict converters; new null-shape guards validate their required
management fields without changing legacy serialization or interpreting rules.
Optional response properties permit null where the wire can omit/return it;
required nonnullable data and exact tagged values remain strict. No NativeAOT claim
is made for reflection-based optional/converter support.

Whole-page/all-page validation and exact operation policy are distinct from model
generation. `coverage.json` only marks methods actually wired, with128 full/minimal
wire fixtures plus malformed/error/status/paging/cancellation/ownership checks.
Source updates must retain every `/ofrep/v1/**` exclusion and account for new paths.
Webhooks are receipt schemas, not outbound REST operation counts.

## Rejected private Kiota experiment

Phase0 used already installed Kiota1.30.0 at source
`6ccf6da832742181cba0ab02588c5795b2185e13`, runtime1.21.2 in a private test project.
It generated181 internal files reproducibly and compiled. Three acceptance probes
failed: required-null flag fields were omitted, uppercase discriminators accepted,
and required values allowed missing. Default success-status policy, raw WithUrl
builders and usage response-header loss also prevented public-surface adoption.

The experiment remains opt-in `task codegen:kiota:check`/`codegen:kiota:test`, outside
normal generation/build. Its two Kiota dependencies are test-only; no generated
Kiota code enters the SDK package. Full Phase0 evidence/report remains external.
Passing its characterization tests is not passing semantic acceptance. The accepted
implementation reuses existing STJ/HTTP behavior instead of a second adapter stack.
