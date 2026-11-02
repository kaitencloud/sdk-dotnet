// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Net;
using System.Text.Json;
using KaitenCloud.Sdk;
using KaitenCloud.Sdk.Management;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

// Changing TZ is process-wide: do not overlap these tests with other collections.
[CollectionDefinition("Instance assignment timezone", DisableParallelization = true)]
public sealed class InstanceAssignmentTimezoneCollection;

[Collection("Instance assignment timezone")]
public sealed class InstanceAssignmentInstantTests : IDisposable
{
    private readonly string? originalTimezone = Environment.GetEnvironmentVariable("TZ");
    public InstanceAssignmentInstantTests()
    {
        // TZ is honored by .NET on Unix. Windows still runs all protocol/round-trip cases.
        if (OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("TZ", "Pacific/Honolulu");
        TimeZoneInfo.ClearCachedData();
    }
    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("TZ", originalTimezone);
        TimeZoneInfo.ClearCachedData();
    }
    private static void AssertControlledTimezone()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Equal(TimeSpan.FromHours(-10), TimeZoneInfo.Local.GetUtcOffset(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)));
    }
    public static TheoryData<bool, string, string?> InvalidAssignments
    {
        get
        {
            var cases = new TheoryData<bool, string, string?>();
            string?[] invalid = [null, "null", "42", "true", .. new[] {
                "2026-11-01", "2026-11-01T12:00:00", "2026-11-01T12:00:00.1234567",
                "2026-11-01T12:00Z", "2026-11-01T12:00:00+14:01", "2026-11-01T12:00:00-14:01",
                "2026-11-01T12:00:00+0900", "2026-11-01T12:00:00Z private-wire-value", "private-wire-value"
            }.Select(value => JsonSerializer.Serialize(value))];
            foreach (var management in new[] { false, true })
                foreach (var field in new[] { "startLicenseDate", "endLicenseDate" })
                    foreach (var value in invalid) cases.Add(management, field, value);
            return cases;
        }
    }
    [Theory]
    [MemberData(nameof(InvalidAssignments))]
    public async Task InvalidResponseAssignmentCannotReachUpdateProjection(bool management, string field, string? value)
    {
        AssertControlledTimezone();
        var fields = AssignmentFields("2026-11-01T12:00:00Z", "2026-11-02T12:00:00Z");
        if (value is null) fields.Remove(field);
        else fields[field] = JsonSerializer.Deserialize<JsonElement>(value);
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); // No PUT may echo an inferred local instant.
            return Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(fields)));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<KaitenProtocolException>(() => ReadAndUpdate(http, management));
        Assert.Null(error.InnerException);
        Assert.Empty(error.Data);
        Assert.DoesNotContain("private-wire-value", error.ToString());
        Assert.DoesNotContain("2026-11", error.ToString());
        Assert.Equal(1, handler.Calls);
    }
    public static TheoryData<bool, string, string> ValidAssignments
    {
        get
        {
            var cases = new TheoryData<bool, string, string>();
            foreach (var management in new[] { false, true })
                foreach (var (start, end) in new[] {
                    ("2026-11-01T12:00:00Z", "2026-11-02T12:00:00Z"),
                    ("2026-11-01T21:00:00+09:00", "2026-11-02T21:00:00+09:00"),
                    ("2026-11-01T05:00:00-07:00", "2026-11-02T05:00:00-07:00"),
                    ("2026-11-01T12:00:00.1234567Z", "2026-11-02T12:00:00.1234567Z"),
                    ("2026-11-01T21:00:00.1234567+09:00", "2026-11-02T21:00:00.1234567+09:00"),
                    ("2026-11-01T05:00:00.1234567-07:00", "2026-11-02T05:00:00.1234567-07:00")
                }) cases.Add(management, start, end);
            return cases;
        }
    }
    [Theory]
    [MemberData(nameof(ValidAssignments))]
    public async Task ValidAssignmentsPreserveOffsetsAndRoundTripThroughUpdate(bool management, string start, string end)
    {
        AssertControlledTimezone();
        var payload = JsonSerializer.Serialize(AssignmentFields(start, end));
        string? updateBody = null;
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal("/api/instances/ninja-osaka-prod", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) return ClientTests.Json(payload);
            Assert.Equal(HttpMethod.Put, request.Method);
            updateBody = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        var dates = await ReadAndUpdate(http, management);
        var expectedStart = DateTimeOffset.Parse(start, CultureInfo.InvariantCulture);
        var expectedEnd = DateTimeOffset.Parse(end, CultureInfo.InvariantCulture);
        Assert.True(expectedStart.EqualsExact(dates.Start));
        Assert.True(expectedEnd.EqualsExact(dates.End));
        using var update = JsonDocument.Parse(updateBody!);
        var writtenStart = update.RootElement.GetProperty("startLicenseDate").GetDateTimeOffset();
        var writtenEnd = update.RootElement.GetProperty("endLicenseDate").GetDateTimeOffset();
        Assert.Equal(expectedStart, writtenStart);
        Assert.Equal(expectedEnd, writtenEnd);
        // Legacy projections preserve the offset; management requests retain their UTC writer.
        Assert.Equal(management ? TimeSpan.Zero : expectedStart.Offset, writtenStart.Offset);
        Assert.Equal(management ? TimeSpan.Zero : expectedEnd.Offset, writtenEnd.Offset);
        Assert.False(update.RootElement.TryGetProperty("licenseTerm", out _));
        Assert.False(update.RootElement.TryGetProperty("createdAt", out _));
        Assert.Equal(2, handler.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedRepresentationsStillAllowAbsentOrNullTermAndNullableFields(bool nullTerm)
    {
        var fields = AssignmentFields("2026-11-01T21:00:00+09:00", "2026-11-02T05:00:00-07:00");
        if (nullTerm) fields["licenseTerm"] = JsonSerializer.SerializeToElement<object?>(null);
        else fields.Remove("licenseTerm");
        foreach (var field in new[] { "deploymentZoneId", "deploymentZoneSlug", "metadata" })
            fields[field] = JsonSerializer.SerializeToElement<object?>(null);
        var shared = JsonSerializer.Serialize(fields);
        var legacy = JsonSerializer.Deserialize<Instance>(shared, KaitenJson.CreateOptions())!;
        Assert.Null(legacy.LicenseTerm);
        Assert.Null(legacy.DeploymentZoneId);
        Assert.Null(legacy.Metadata);
        using var http = new HttpClient(new StubHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? ClientTests.Json(shared, HttpStatusCode.Created)
            : ClientTests.Json(JsonSerializer.Serialize(new { items = new[] { JsonSerializer.Deserialize<JsonElement>(shared) }, hasMore = false })))));
        using var management = new KaitenManagementClient(http, Options());
        var listed = (await management.Instances.ListAsync()).Items![0];
        var request = JsonSerializer.Deserialize<KaitenCloud.Sdk.Management.Models.CreateInstanceRequest>(
            ManagementWireTests.Fixture("createInstance", false).GetProperty("request").GetRawText(), ManagementJson.Options)!;
        var created = await management.Instances.CreateAsync(request);
        foreach (var instance in new[] { listed, created })
        {
            Assert.Null(instance.LicenseTerm);
            Assert.Null(instance.DeploymentZoneId);
            Assert.Null(instance.Metadata);
            Assert.True(legacy.StartLicenseDate.EqualsExact(instance.StartLicenseDate));
            Assert.True(legacy.EndLicenseDate.EqualsExact(instance.EndLicenseDate));
        }
    }
    [Theory]
    [InlineData("startLicenseDate", "2026-11-01")]
    [InlineData("endLicenseDate", "2026-11-01")]
    [InlineData("startLicenseDate", "2026-11-01T12:00:00")]
    [InlineData("endLicenseDate", "2026-11-01T12:00:00")]
    public async Task IntegrationInstanceRepresentationRejectsIncompleteAssignments(string field, string value)
    {
        AssertControlledTimezone();
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            ManagementWireTests.Fixture("get-instance-integration-by-external-id", false).GetProperty("response").GetRawText())!;
        fields[field] = JsonSerializer.SerializeToElement(value);
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(ClientTests.Json(JsonSerializer.Serialize(fields)))));
        using var management = new KaitenManagementClient(http, Options());
        var error = await Assert.ThrowsAsync<KaitenProtocolException>(() => management.Integrations.GetInstanceByExternalIdAsync("adapter", "external-id"));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(value, error.ToString());
    }
    private static Dictionary<string, JsonElement> AssignmentFields(string start, string end)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(RestResourceMigrationTests.Detail(
            "{\"status\":\"EXPIRED\",\"evaluatedAt\":\"2026-11-01T12:00:00Z\"}"))!;
        fields["startLicenseDate"] = JsonSerializer.SerializeToElement(start);
        fields["endLicenseDate"] = JsonSerializer.SerializeToElement(end);
        return fields;
    }
    private static KaitenClientOptions Options() => new() { BaseUri = new Uri("https://gateway.test") };
    private static async Task<(DateTimeOffset Start, DateTimeOffset End)> ReadAndUpdate(HttpClient http, bool management)
    {
        if (management)
        {
            using var client = new KaitenManagementClient(http, Options());
            var instance = await client.Instances.GetAsync("ninja-osaka-prod");
            Assert.Equal(LicenseTermStatus.Expired, instance.LicenseTerm!.Status); // No local expiry enforcement.
            await client.Instances.UpdateAsync("ninja-osaka-prod", new()
            {
                Name = instance.Name, Description = instance.Description,
                CustomerId = instance.CustomerId, LicenseId = instance.LicenseId,
                StartLicenseDate = instance.StartLicenseDate, EndLicenseDate = instance.EndLicenseDate
            });
            return (instance.StartLicenseDate, instance.EndLicenseDate);
        }
        using var legacy = new KaitenClient(http, Options());
        var resource = await legacy.GetInstanceAsync("ninja-osaka-prod");
        Assert.Equal(LicenseTermStatus.Expired, resource.LicenseTerm!.Status);
        await legacy.UpdateInstanceAsync("ninja-osaka-prod", resource.ToUpdateRequest());
        return (resource.StartLicenseDate, resource.EndLicenseDate);
    }
}
