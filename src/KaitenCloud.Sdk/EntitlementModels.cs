// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaitenCloud.Sdk;

public abstract record EntitlementValue : KaitenResource;
public sealed record NumberEntitlementValue : EntitlementValue
{
    public required double Value { get; init; }
    [JsonPropertyName("event_count")] public int? EventCount { get; init; }
}
public sealed record BooleanEntitlementValue : EntitlementValue
{
    public required bool Value { get; init; }
}
public sealed record ObjectEntitlementValue : EntitlementValue
{
    public required Dictionary<string, JsonElement> Value { get; init; }
}
public enum UsageReportBehavior { Append, Set }
public sealed record EntitlementUsage : KaitenResource
{
    public required Guid EntitlementId { get; init; }
    public required string EntitlementSlug { get; init; }
    public required Guid LicenseId { get; init; }
    public required string LicenseSlug { get; init; }
    public required EntitlementValue Value { get; init; }
    public EntitlementValue? Limit { get; init; }
    public DateTimeOffset? CurrentPeriodStart { get; init; }
    public DateTimeOffset? CurrentPeriodEnd { get; init; }
}
public sealed record LicenseEntitlement : KaitenResource
{
    public required string EntitlementSlug { get; init; }
    public required string EntitlementName { get; init; }
    public string? EntitlementType { get; init; }
    public required Guid LicenseId { get; init; }
    public required string LicenseSlug { get; init; }
    public required EntitlementValue Value { get; init; }
    public int? LimitCapExceededOveragePercent { get; init; }
    public required UserReference CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required UserReference UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<JsonElement>? EntitlementGroups { get; init; }
    public UpdateLicenseEntitlementRequest ToUpdateRequest() => new()
    { Value = Value, LimitCapExceededOveragePercent = LimitCapExceededOveragePercent };
}
public sealed record UpdateLicenseEntitlementRequest
{
    public required EntitlementValue Value { get; init; }
    public int? LimitCapExceededOveragePercent { get; init; }
}
