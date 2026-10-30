# Official OpenFeature / OFREP integration

## Verified upstream packages

- OpenFeature.Hosting/OpenFeature 2.14.1, [tagged source](https://github.com/open-feature/dotnet-sdk/tree/v2.14.1).
- OpenFeature.Providers.Ofrep 0.1.7, NuGet repository commit
  [209b6a799e2cdd5ce8bbaa7556d270e80cd2a583](https://github.com/open-feature/dotnet-sdk-contrib/tree/209b6a799e2cdd5ce8bbaa7556d270e80cd2a583/src/OpenFeature.Providers.Ofrep).

`OfrepProvider` is sealed and remains the actual provider. Its native DI options
`HttpClientName` and `ConfigureHttpClient` allow factory/client injection. The
constructor accepting a client is internal: do not invent constructor overloads.
Kaiten configures the public `AddOfrepProvider` path, not a custom provider or
transport evaluator. Official Hosting owns an isolated API/container lifecycle.

`AddKaitenOpenFeature(Action<KaitenOfrepOptions>)` selects the default domain.
Options are BaseUri (required), BearerToken (optional server credential),
RequestTimeout (default 10s, >0 and <=5min), MaxResponseBytes (default 4MiB,
1KiB–16MiB). Options snapshot at registration. Register IKaitenCredentialProvider
for request-local rotating credentials; it takes precedence over BearerToken.
The named client is `KaitenOpenFeatureServiceCollectionExtensions.HttpClientName`.
No request context, provider results or tenant decisions are cached by Kaiten.

The provider POSTs each invocation to relative `ofrep/v1/evaluate/flags/{key}`.
BaseUri is normalized to the authenticated gateway `/api/`, including proxy
prefixes. Request cancellation flows through the official provider/client into
HTTP and async credential/body reads. HttpClient.Timeout bounds the operation,
including credential sources that ignore cancellation (WaitAsync bounds awaiting).
Cancellation/deadline/network/credential failures are returned as OpenFeature
details, not thrown Kaiten flag exceptions. Do not assume cancellation will throw
OperationCanceledException to the application: check details and caller token.

## Native failure behavior and envelope validation

Native HTTP401/403 map ProviderNotReady; 404 maps FlagNotFound. HTTP500/503/redirect,
network/timeouts and 429 return Reason.ERROR but upstream `general_error` is mapped
to ErrorType.None. Applications MUST check both ErrorType != None **or**
Reason == ERROR before looking at Value. A fallback value never grants authorization.

Native `ProcessOkResponseAsync` substitutes default when value is absent/null,
substitutes request key for missing key, and `MapErrorType` ignores unknown codes.
The internal named HTTP handler validates HTTP200 envelopes before official decode:
object root, unique fields, exact requested key, non-null success value, or a known
uppercase OpenFeature error code (FLAG_NOT_FOUND/TYPE_MISMATCH/PARSE_ERROR/
PROVIDER_NOT_READY/INVALID_CONTEXT/TARGETING_KEY_MISSING/PROVIDER_FATAL/GENERAL).
Unmapped codes including GENERAL_ERROR are unavailable via a sanitized protocol
exception, which the official provider turns into Reason.ERROR. No response body
is logged. The handler buffers at most MaxResponseBytes and replays untouched bytes
to the official provider; it does not inspect a boolean decision or run rule logic.
The official provider alone checks requested value type. Legitimate server DEFAULT
(including false/zero) and DISABLED true remain successful; never blanket reject DEFAULT.

## Refresh and cooldown

No cache, bulk API, polling, live updates, ETag/304 or automatic retries are supplied
by this .NET provider. Every normal call sends one HTTP request, including repeats
and different request contexts. Presenter/grant changes appear on the next request.
Provider HTTP429 stores Retry-After as a provider-wide cooldown; subsequent calls
return unavailable without sending until expiry. Missing advice defaults to one
minute. This is not a tenant result cache: no tenant receives another tenant
value, but all tenants sharing the provider can be temporarily unavailable.
Applications own refresh UX and must not claim instant push/poll semantics.

## Safe telemetry

Kaiten factory HTTP logging and default propagation are disabled. A request-local
handler propagates W3C traceparent/tracestate and emits only method/status plus
validated Core trace ID on ActivitySource KaitenCloud.Sdk.Ofrep. No URL, token,
context, raw body, remote error text or baggage is logged by this integration.
Upstream OfrepClient and OpenFeatureClient logger categories are suppressed: their
exception/key logging is not safe for sensitive context. Host code may override
filters/instrument HTTP or add hooks; that is outside the SDK redaction guarantee.
Export only safe Kaiten sources or explicitly redact System.Net.Http/OpenFeature
activities (the latter include flag keys/values/error messages).

See the compiled [example](../examples/Kaiten.Example/Program.cs) and README for
per-request nested context and explicit licensing/entitlement/application guards.
