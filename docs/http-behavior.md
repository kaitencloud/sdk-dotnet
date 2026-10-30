# HTTP and failure contract

- One operation deadline covers credentials, HTTP/body and backoff: default30s,
  configurable >0 and <=5min. Caller cancellation is OperationCanceledException
  with its token; deadline/shorter HTTP timeout is KaitenTimeoutException.
- Zero retries by default. MaxRetries1–3 opts in for GET/read-only OFREP only.
  Retryable:408/429/500/502/503/504 and connection/send failures. Never replay
  401/403, business404/409/422, malformed models, credential failure, cancellation,
  timeout, uncertain body-read failure, PUT or usage POST.
- Exponential200ms delay / 2s cap by default, configurable. Honor Retry-After only
  within the bound; longer server advice returns the error without earlier retry.
  The total operation deadline always bounds waiting.
- Usage has no idempotency key. A network/timeout mutation may have committed.
  Applications must preserve a pending/uncertain outcome and reconcile, not replay
  or claim an order succeeded. No exactly-once guarantee.
- Responses/content/streams are disposed. Body limit4MiB (configurable1KiB–16MiB),
  depth64. Oversized/malformed/missing/unsupported decisions fail protocol, not grant.
- KaitenApiException preserves actual HTTP StatusCode, Code, ErrorId, RFC9457 fields,
  errors and extensions. License states accept only exact ACTIVE/NOT_STARTED/EXPIRED
  strings; aliases/composites/numbers/whitespace are protocol errors. Single usage
  reads and numeric-report responses must match the requested entitlementSlug.
  Match ReportEntitlementUsageMetric.ThresholdExceeded,
  not every409. Plain Envoy401/403 are API denials with absent problem/code.
- KaitenEvaluationException preserves Evaluation.ErrorCode, ErrorDetails, key,
  metadata and optional status. Bulk HTTP200 can contain failures: check IsSuccess
  before GetValue<T>(). Disabled flags can return true defaults; false/0 are successes.
- KaitenNetworkException, KaitenTimeoutException, KaitenCredentialException and
  KaitenProtocolException are distinct. Invalid local config/slugs/numeric reports
  throw ArgumentException/ArgumentOutOfRangeException. JSON scalar typing is strict.
- Message/ToString/log/activity fields omit sensitive requests and remote error text.
  Explicit Problem/Evaluation properties remain server data; don't dump into telemetry.
- Options are snapshotted at construction; only the credential provider is dynamic.
  The application owns trusted context and tenant authorization.
