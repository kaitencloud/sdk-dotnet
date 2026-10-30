// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaitenCloud.Sdk;

public abstract record KaitenResource
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record UserReference : KaitenResource
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
}
public sealed record ExternalIntegration : KaitenResource
{
    [JsonPropertyName("external_id")] public required string ExternalId { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    [JsonPropertyName("web_url")] public string? WebUrl { get; init; }
    [JsonPropertyName("synced_at")] public required DateTimeOffset SyncedAt { get; init; }
    [JsonPropertyName("last_error")] public string? LastError { get; init; }
}
public sealed record Customer : KaitenResource
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? ExternalCustomerId { get; init; }
    public string? Domain { get; init; }
    public Dictionary<string, ExternalIntegration>? Integrations { get; init; }
    public required UserReference CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required UserReference UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
public sealed record Instance : KaitenResource
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Guid CustomerId { get; init; }
    public required string CustomerSlug { get; init; }
    public required Guid LicenseId { get; init; }
    public required string LicenseSlug { get; init; }
    public Guid? DeploymentZoneId { get; init; }
    public string? DeploymentZoneSlug { get; init; }
    public required DateTimeOffset StartLicenseDate { get; init; }
    public required DateTimeOffset EndLicenseDate { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    public string? Status { get; init; }
    public string? LifecycleStage { get; init; }
    public Dictionary<string, ExternalIntegration>? Integrations { get; init; }
    public required UserReference CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required UserReference UpdatedBy { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public UpdateInstanceRequest ToUpdateRequest() => new()
    {
        Slug = Slug,
        Name = Name,
        Description = Description,
        CustomerId = CustomerId,
        LicenseId = LicenseId,
        DeploymentZoneId = DeploymentZoneId,
        Metadata = Metadata,
        StartLicenseDate = StartLicenseDate,
        EndLicenseDate = EndLicenseDate
    };
}
public sealed record UpdateInstanceRequest
{
    public string? Slug { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Guid CustomerId { get; init; }
    public required Guid LicenseId { get; init; }
    public Guid? DeploymentZoneId { get; init; }
    public required DateTimeOffset StartLicenseDate { get; init; }
    public required DateTimeOffset EndLicenseDate { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
public sealed record License : KaitenResource
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Type { get; init; }
    public required string Version { get; init; }
    public string? VersionName { get; init; }
    public required bool IsDefault { get; init; }
    public required Guid FamilyId { get; init; }
    public required string LifecycleState { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
public enum LicenseValidationStatus { Active, NotStarted, Expired }
public sealed record LicenseValidation : KaitenResource
{
    public required bool Valid { get; init; }
    public required LicenseValidationStatus Status { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
    public required string InstanceSlug { get; init; }
    public required Guid LicenseId { get; init; }
    public required string LicenseSlug { get; init; }
    public required DateTimeOffset StartLicenseDate { get; init; }
    public required DateTimeOffset EndLicenseDate { get; init; }
}
public sealed record Page<T> : KaitenResource
{
    public required IReadOnlyList<T> Items { get; init; }
    public string? NextCursor { get; init; }
    public required bool HasMore { get; init; }
}
