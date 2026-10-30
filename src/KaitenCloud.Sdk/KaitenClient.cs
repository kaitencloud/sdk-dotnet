// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KaitenCloud.Sdk;

public sealed class KaitenClient : IKaitenClient, IDisposable
{
    public const string ActivitySourceName = "KaitenCloud.Sdk";
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, "0.1.1");
    private readonly HttpClient _http;
    private readonly KaitenClientOptions _options;
    private readonly IKaitenCredentialProvider? _credentials;
    private readonly ILogger<KaitenClient> _logger;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    // This overload never owns or alters the supplied HttpClient.
    public KaitenClient(HttpClient httpClient, KaitenClientOptions options,
        IKaitenCredentialProvider? credentialProvider = null, ILogger<KaitenClient>? logger = null)
        : this(httpClient, options, credentialProvider, logger, false) { }

    internal KaitenClient(HttpClient httpClient, KaitenClientOptions options,
        IKaitenCredentialProvider? credentialProvider, ILogger<KaitenClient>? logger, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        if (httpClient.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Configure authentication through Kaiten options or a credential provider, not HttpClient default headers.");
        _http = httpClient; _options = options.Snapshot(); _credentials = credentialProvider;
        _logger = logger ?? NullLogger<KaitenClient>.Instance; _ownsHttpClient = ownsHttpClient;
    }

    // Long-lived client/pool for non-DI applications. Dispose when the application stops.
    public static KaitenClient Create(KaitenClientOptions options, IKaitenCredentialProvider? credentialProvider = null,
        ILogger<KaitenClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = options.Snapshot();
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ActivityHeadersPropagator = null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        return new KaitenClient(http, options, credentialProvider, logger, true);
    }

    public Task<Customer> GetCustomerAsync(string customerSlug, CancellationToken cancellationToken = default)
        => SendAsync<Customer>(HttpMethod.Get, $"customers/{Slug(customerSlug)}", "get-customer", null, true, cancellationToken);
    public Task<Instance> GetInstanceAsync(string instanceSlug, CancellationToken cancellationToken = default)
        => SendAsync<Instance>(HttpMethod.Get, $"instances/{Slug(instanceSlug)}", "get-instance", null, true, cancellationToken);
    public Task<License> GetLicenseAsync(string licenseSlug, CancellationToken cancellationToken = default)
        => SendAsync<License>(HttpMethod.Get, $"licenses/{Slug(licenseSlug)}", "get-license", null, true, cancellationToken);
    public async Task<LicenseValidation> ValidateInstanceLicenseAsync(string instanceSlug, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<LicenseValidation>(HttpMethod.Get, $"instances/{Slug(instanceSlug)}/license-validation",
            "validate-license", null, true, cancellationToken).ConfigureAwait(false);
        if (result.InstanceSlug != instanceSlug) throw new KaitenProtocolException();
        return result; // DB-authored decision only: no local date/license evaluation.
    }
    public async Task<EntitlementUsage> GetInstanceEntitlementUsageAsync(string instanceSlug, string entitlementSlug, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<EntitlementUsage>(HttpMethod.Get, UsagePath(instanceSlug, entitlementSlug), "get-usage", null, true, cancellationToken).ConfigureAwait(false);
        if (result.EntitlementSlug != entitlementSlug) throw new KaitenProtocolException();
        return result;
    }
    public async Task<IReadOnlyList<EntitlementUsage>> GetAllInstanceEntitlementUsageAsync(string instanceSlug, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<JsonElement>(HttpMethod.Get, $"instances/{Slug(instanceSlug)}/entitlements/usage",
            "get-all-usage", null, true, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Null) return [];
        var usages = KaitenJson.Deserialize<EntitlementUsage[]>(JsonSerializer.SerializeToUtf8Bytes(result));
        foreach (var usage in usages) ValidateModel(usage);
        return usages;
    }
    public async Task<EntitlementUsage> ReportNumericUsageAsync(string instanceSlug, string entitlementSlug, double value,
        UsageReportBehavior behavior, IReadOnlyDictionary<string, JsonElement>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (!Enum.IsDefined(behavior)) throw new ArgumentOutOfRangeException(nameof(behavior));
        var result = await SendAsync<EntitlementUsage>(HttpMethod.Post, UsagePath(instanceSlug, entitlementSlug), "report-usage",
            new { value = (EntitlementValue)new NumberEntitlementValue { Value = value }, behavior, metadata }, false, cancellationToken).ConfigureAwait(false);
        if (result.EntitlementSlug != entitlementSlug) throw new KaitenProtocolException();
        return result;
    }
    public async Task<FlagEvaluation> EvaluateFlagAsync(string flagSlug, IReadOnlyDictionary<string, object?> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await SendAsync<FlagEvaluation>(HttpMethod.Post, $"ofrep/v1/evaluate/flags/{Slug(flagSlug)}",
            "evaluate-flag", new { context }, true, cancellationToken).ConfigureAwait(false);
        if (result.Key != flagSlug) throw new KaitenProtocolException();
        if (!result.IsSuccess) throw new KaitenEvaluationException(result, HttpStatusCode.OK);
        return result;
    }
    public async Task<FlagEvaluation<T>> EvaluateFlagAsync<T>(string flagSlug, IReadOnlyDictionary<string, object?> context, CancellationToken cancellationToken = default)
    {
        var result = await EvaluateFlagAsync(flagSlug, context, cancellationToken).ConfigureAwait(false);
        return new(result.Key, result.GetValue<T>(), result.Variant, result.Reason, result.Metadata, result.Extensions);
    }
    public async Task<BulkFlagEvaluation> EvaluateAllFlagsAsync(IReadOnlyDictionary<string, object?> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bulk = await SendAsync<BulkFlagEvaluation>(HttpMethod.Post, "ofrep/v1/evaluate/flags", "evaluate-all-flags",
            new { context }, true, cancellationToken).ConfigureAwait(false);
        return bulk; // HTTP 200 may contain individual failures; never convert these to false/allowed.
    }
    public Task<FeatureFlag> GetFeatureFlagAsync(string flagSlug, CancellationToken cancellationToken = default)
        => SendAsync<FeatureFlag>(HttpMethod.Get, $"feature-flags/{Slug(flagSlug)}", "get-feature-flag", null, true, cancellationToken);
    public async Task UpdateFeatureFlagAsync(string flagSlug, UpdateFeatureFlagRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SendAsync<JsonElement>(HttpMethod.Put, $"feature-flags/{Slug(flagSlug)}", "update-feature-flag", request, false, cancellationToken).ConfigureAwait(false);
    }
    public async Task UpdateInstanceAsync(string instanceSlug, UpdateInstanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SendAsync<JsonElement>(HttpMethod.Put, $"instances/{Slug(instanceSlug)}", "update-instance", request, false, cancellationToken).ConfigureAwait(false);
    }
    public Task<LicenseEntitlement> GetLicenseEntitlementAsync(string licenseSlug, string entitlementSlug, CancellationToken cancellationToken = default)
        => SendAsync<LicenseEntitlement>(HttpMethod.Get, GrantPath(licenseSlug, entitlementSlug), "get-license-entitlement", null, true, cancellationToken);
    public Task<Page<LicenseEntitlement>> GetLicenseEntitlementsAsync(string licenseSlug, string? cursor = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var path = $"licenses/{Slug(licenseSlug)}/entitlements?limit={limit}";
        if (cursor is not null) path += "&cursor=" + Uri.EscapeDataString(cursor);
        return SendAsync<Page<LicenseEntitlement>>(HttpMethod.Get, path, "get-license-entitlements", null, true, cancellationToken);
    }
    public async Task UpdateLicenseEntitlementAsync(string licenseSlug, string entitlementSlug, UpdateLicenseEntitlementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SendAsync<JsonElement>(HttpMethod.Put, GrantPath(licenseSlug, entitlementSlug), "update-license-entitlement", request, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, string operation, object? body, bool safeToRetry, CancellationToken callerToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        callerToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(_options.RequestTimeout);
        var token = deadline.Token;
        using var activity = ActivitySource.StartActivity("kaiten." + operation, ActivityKind.Client);
        activity?.SetTag("kaiten.operation", operation);
        activity?.SetTag("http.request.method", method.Method);
        int? status = null;
        try
        {
            byte[]? payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, KaitenJson.Options);
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, new Uri(_options.BaseUri, path));
                string? bearer;
                try
                {
                    bearer = _credentials is null ? _options.BearerToken : await _credentials.GetBearerTokenAsync(token).AsTask().WaitAsync(token).ConfigureAwait(false);
                    if (bearer is not null && (string.IsNullOrWhiteSpace(bearer) || bearer.Any(c => c <= ' ' || c >= '\u007f'))) throw new KaitenCredentialException();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { throw new KaitenCredentialException(); }
                if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("application/problem+json");
                var current = activity ?? Activity.Current;
                if (current is { IdFormat: ActivityIdFormat.W3C, Id: not null })
                {
                    request.Headers.TryAddWithoutValidation("traceparent", current.Id);
                    if (current.TraceStateString is not null) request.Headers.TryAddWithoutValidation("tracestate", current.TraceStateString);
                }
                if (payload is not null)
                {
                    request.Content = new ByteArrayContent(payload);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
                HttpResponseMessage response;
                try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
                catch (HttpRequestException) when (safeToRetry && attempt < _options.MaxRetries)
                {
                    await DelayRetryAsync(attempt, null, token).ConfigureAwait(false); continue;
                }
                using (response)
                {
                    status = (int)response.StatusCode;
                    activity?.SetTag("http.response.status_code", status);
                    if (response.Headers.TryGetValues("Kaiten-TraceId", out var ids))
                    {
                        var traceId = ids.FirstOrDefault();
                        if (traceId is { Length: 32 } && traceId.All(Uri.IsHexDigit)) activity?.SetTag("kaiten.trace_id", traceId);
                    }
                    if (safeToRetry && attempt < _options.MaxRetries && IsTransient(response.StatusCode) && CanHonorRetryAfter(response.Headers.RetryAfter))
                    {
                        await DelayRetryAsync(attempt, response.Headers.RetryAfter, token).ConfigureAwait(false); continue;
                    }
                    if (response.StatusCode == HttpStatusCode.NoContent)
                    {
                        if (method != HttpMethod.Put || typeof(T) != typeof(JsonElement)) throw new KaitenProtocolException();
                        return (T)(object)default(JsonElement);
                    }
                    var bytes = await ReadBodyAsync(response.Content, token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (operation.StartsWith("evaluate-", StringComparison.Ordinal) && response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden)
                            ThrowEvaluationFailure(bytes, response.StatusCode);
                        throw new KaitenApiException(response.StatusCode, ReadProblem(bytes));
                    }
                    if (response.StatusCode != HttpStatusCode.OK || method == HttpMethod.Put) throw new KaitenProtocolException();
                    if (operation.StartsWith("evaluate-", StringComparison.Ordinal)) ThrowEvaluationFailure(bytes, response.StatusCode);
                    var result = KaitenJson.Deserialize<T>(bytes);
                    ValidateModel(result);
                    return result;
                }
            }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "canceled");
            throw new OperationCanceledException("Kaiten request canceled.", callerToken);
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "timeout");
            throw new KaitenTimeoutException();
        }
        catch (HttpRequestException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "network"); throw new KaitenNetworkException();
        }
        catch (IOException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "network"); throw new KaitenNetworkException();
        }
        catch (KaitenException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "kaiten-error"); throw;
        }
        finally
        {
            // Deliberately no URL/path, context, credentials, bodies or remote error text.
            _logger.LogDebug("Kaiten operation {Operation} completed with HTTP status {Status}", operation, status);
        }
    }

    private async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > _options.MaxResponseBytes) throw new KaitenProtocolException();
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > _options.MaxResponseBytes) throw new KaitenProtocolException();
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
    private bool CanHonorRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        var advised = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        return advised is null || advised <= _options.MaxRetryDelay;
    }
    private Task DelayRetryAsync(int attempt, RetryConditionHeaderValue? retryAfter, CancellationToken token)
    {
        var delay = TimeSpan.FromMilliseconds(_options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt));
        var advised = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        if (advised > delay) delay = advised.Value;
        if (delay > _options.MaxRetryDelay) delay = _options.MaxRetryDelay;
        return Task.Delay(delay, token);
    }
    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    private static KaitenProblem? ReadProblem(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<KaitenProblem>(bytes, KaitenJson.Options); }
        catch (JsonException) { return null; }
    }
    private static void ThrowEvaluationFailure(byte[] bytes, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errorCode", out var code)) return;
            if (code.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(code.GetString())) throw new KaitenProtocolException();
            // Bulk failures have no key. Preserve it when present on a single failure.
            var evaluation = new FlagEvaluation
            {
                Key = root.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString()! : "",
                ErrorCode = code.GetString(),
                ErrorDetails = root.TryGetProperty("errorDetails", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null,
                Metadata = root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                    ? meta.Deserialize<Dictionary<string, JsonElement>>(KaitenJson.Options) : null
            };
            throw new KaitenEvaluationException(evaluation, status);
        }
        catch (JsonException) { /* Non-JSON auth/proxy errors are handled by HTTP status. */ }
    }
    private static void ValidateModel<T>(T result)
    {
        if (result is null) throw new KaitenProtocolException();
        switch (result)
        {
            case LicenseValidation validation when string.IsNullOrWhiteSpace(validation.InstanceSlug) || string.IsNullOrWhiteSpace(validation.LicenseSlug) || validation.LicenseId == Guid.Empty || validation.Valid != (validation.Status == LicenseValidationStatus.Active):
                throw new KaitenProtocolException();
            case FlagEvaluation evaluation:
                ValidateEvaluation(evaluation);
                break;
            case BulkFlagEvaluation bulk:
                if (bulk.Flags is null) throw new KaitenProtocolException();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in bulk.Flags)
                {
                    ValidateEvaluation(item);
                    if (!keys.Add(item.Key)) throw new KaitenProtocolException();
                }
                break;
            case EntitlementUsage usage when usage.Value is null || usage.EntitlementId == Guid.Empty || usage.LicenseId == Guid.Empty || string.IsNullOrWhiteSpace(usage.EntitlementSlug) || string.IsNullOrWhiteSpace(usage.LicenseSlug):
            case LicenseEntitlement grant when grant.Value is null || grant.LicenseId == Guid.Empty || string.IsNullOrWhiteSpace(grant.EntitlementSlug) || string.IsNullOrWhiteSpace(grant.LicenseSlug):
            case Instance instance when instance.CustomerId == Guid.Empty || instance.LicenseId == Guid.Empty || string.IsNullOrWhiteSpace(instance.Slug) || string.IsNullOrWhiteSpace(instance.CustomerSlug) || string.IsNullOrWhiteSpace(instance.LicenseSlug):
            case FeatureFlag flag when flag.DefaultVariant is null || string.IsNullOrWhiteSpace(flag.Slug):
                throw new KaitenProtocolException();
            case Page<LicenseEntitlement> page:
                if (page.Items is null || page.HasMore && string.IsNullOrEmpty(page.NextCursor)) throw new KaitenProtocolException();
                foreach (var item in page.Items) ValidateModel(item);
                break;
        }
    }
    private static void ValidateEvaluation(FlagEvaluation result)
    {
        if (result is null) throw new KaitenProtocolException();
        if (string.IsNullOrWhiteSpace(result.Key) || result.ErrorCode is not null && string.IsNullOrWhiteSpace(result.ErrorCode) ||
            result.ErrorCode is null && result.Value.ValueKind == JsonValueKind.Undefined) throw new KaitenProtocolException();
    }
    private static string Slug(string value)
    {
        if (value is null || value.Length is < 2 or > 100 || !Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9-]*[a-z0-9]\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("A Kaiten slug must be 2–100 lowercase alphanumeric/hyphen characters.");
        return Uri.EscapeDataString(value);
    }
    private static string UsagePath(string instance, string entitlement) => $"instances/{Slug(instance)}/entitlements/{Slug(entitlement)}/usage";
    private static string GrantPath(string license, string entitlement) => $"licenses/{Slug(license)}/entitlements/{Slug(entitlement)}";
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _http.Dispose();
    }
}
