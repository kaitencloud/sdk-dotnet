// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaitenCloud.Sdk;

public sealed record FlagEvaluation : KaitenResource
{
    public required string Key { get; init; }
    public JsonElement Value { get; init; }
    public string? Variant { get; init; }
    public string? Reason { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorDetails { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    [JsonIgnore] public bool IsSuccess => ErrorCode is null && Value.ValueKind != JsonValueKind.Undefined;

    public T GetValue<T>()
    {
        if (!IsSuccess) throw new KaitenEvaluationException(this);
        try { return Value.Deserialize<T>(KaitenJson.Options) ?? throw new KaitenProtocolException(); }
        catch (JsonException) { throw new KaitenProtocolException(); }
        catch (NotSupportedException) { throw new KaitenProtocolException(); }
    }
}
public sealed record BulkFlagEvaluation : KaitenResource
{
    public required IReadOnlyList<FlagEvaluation> Flags { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record FlagEvaluation<T>(string Key, T Value, string? Variant, string? Reason,
    IReadOnlyDictionary<string, JsonElement>? Metadata, IReadOnlyDictionary<string, JsonElement>? Extensions);

public sealed record FlagVariant
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement Value { get; init; }
}
public abstract record FlagTargeting
{
    public required string Name { get; init; }
    public required string Rule { get; init; }
}
public sealed record BasicFlagTargeting : FlagTargeting { public required string Variant { get; init; } }
public sealed record PercentageFlagTargeting : FlagTargeting { public required Dictionary<string, long> Distribution { get; init; } }
public sealed record DateFlagTargeting : FlagTargeting
{
    public required RolloutStep Start { get; init; }
    public required RolloutStep End { get; init; }
}
public sealed record RolloutStep
{
    public required string Variant { get; init; }
    public required long Percentage { get; init; }
    public required DateTimeOffset Date { get; init; }
}
public abstract record DefaultFlagVariant;
public sealed record BasicDefaultFlagVariant : DefaultFlagVariant { public required string Value { get; init; } }
public sealed record PercentageDefaultFlagVariant : DefaultFlagVariant { public required Dictionary<string, long> Distribution { get; init; } }
public sealed record DateDefaultFlagVariant : DefaultFlagVariant
{
    public required RolloutStep Start { get; init; }
    public required RolloutStep End { get; init; }
}
public sealed record FeatureFlag : KaitenResource
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required IReadOnlyList<FlagVariant>? Variants { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required IReadOnlyList<FlagTargeting>? Targetings { get; init; }
    public required string Name { get; init; }
    public required string? Description { get; init; }
    [JsonPropertyName("default_variant")] public required DefaultFlagVariant DefaultVariant { get; init; }
    public required Dictionary<string, JsonElement>? Metadata { get; init; }
    public required bool Enabled { get; init; }
    [JsonPropertyName("event_name")] public required string EventName { get; init; }
    public UpdateFeatureFlagRequest ToUpdateRequest() => new()
    {
        Slug = Slug,
        Type = Type,
        Variants = Variants,
        Targetings = Targetings,
        Name = Name,
        Description = Description,
        DefaultVariant = DefaultVariant,
        Metadata = Metadata,
        Enabled = Enabled,
        EventName = EventName
    };
}
public sealed record UpdateFeatureFlagRequest
{
    public string? Slug { get; init; }
    public required string Type { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required IReadOnlyList<FlagVariant>? Variants { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required IReadOnlyList<FlagTargeting>? Targetings { get; init; }
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required string? Description { get; init; }
    [JsonPropertyName("default_variant")] public required DefaultFlagVariant DefaultVariant { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required Dictionary<string, JsonElement>? Metadata { get; init; }
    public required bool Enabled { get; init; }
    [JsonPropertyName("event_name")] public required string EventName { get; init; }
}
