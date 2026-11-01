// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KaitenCloud.Sdk;
using KaitenCloud.Sdk.Management;
using Xunit;
namespace KaitenCloud.Sdk.Tests;

public sealed class ManagementWireTests
{
    internal static readonly JsonElement[] Api = Load("batch1-api.json");
    internal static readonly JsonElement[] Fixtures = Load("management-wire.json");
    private static JsonElement[] Load(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }
    public static IEnumerable<object[]> WireCases() => Fixtures.Select(f => new object[] { f.GetProperty("operationId").GetString()!, f.GetProperty("full").GetBoolean() });
    public static IEnumerable<object[]> Operations() => Api.Select(op => new object[] { op.GetProperty("operationId").GetString()! });
    public static IEnumerable<object[]> ErrorCases() => Api.SelectMany(op => new[] { 400, 401, 403, 404, 409, 422, 500 }.Select(status => new object[] { op.GetProperty("operationId").GetString()!, status }));
    internal static JsonElement Operation(string id) => Api.Single(op => op.GetProperty("operationId").GetString() == id);
    internal static JsonElement Fixture(string id, bool full) => Fixtures.Single(f => f.GetProperty("operationId").GetString() == id && f.GetProperty("full").GetBoolean() == full);

    [Theory]
    [MemberData(nameof(WireCases))]
    public async Task EveryFacadeFullAndMinimalWireContract(string id, bool full)
    {
        var operation = Operation(id); var fixture = Fixture(id, full);
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.Equal(operation.GetProperty("method").GetString(), request.Method.Method);
            var path = operation.GetProperty("path").GetString()!;
            foreach (var parameter in fixture.GetProperty("pathArguments").EnumerateObject())
                path = path.Replace("{" + parameter.Name + "}", Uri.EscapeDataString(parameter.Value.GetString()!));
            Assert.Equal("/proxy/api" + path, request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("private-token", request.Headers.Authorization.Parameter);
            CheckQuery(request.RequestUri, fixture.GetProperty("query"));
            if (fixture.GetProperty("request").ValueKind == JsonValueKind.Null) Assert.Null(request.Content);
            else
            {
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                Equivalent(fixture.GetProperty("request"), body.RootElement);
            }
            return Response(operation, fixture);
        });
        using var http = new HttpClient(handler);
        using var client = NewClient(http);
        var result = await Invoke(client, operation, fixture);
        if (result is not null)
        {
            var serialized = JsonSerializer.SerializeToElement(result, result.GetType(), ManagementJson.Options);
            if (serialized.ValueKind == JsonValueKind.Object)
                Assert.True(serialized.GetProperty("futureProperty").GetProperty("retained").GetBoolean());
        }
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [MemberData(nameof(WireCases))]
    public void RawResponseMatchesTypedContract(string id, bool full)
    {
        var op = Operation(id); var fixture = Fixture(id, full);
        if (op.GetProperty("responseType").ValueKind == JsonValueKind.Null) return;
        using var http = new HttpClient(); using var client = NewClient(http);
        var resource = client.GetType().GetProperty(op.GetProperty("resource").GetString()!)!.GetValue(client)!;
        var resultType = resource.GetType().GetMethod(op.GetProperty("methodName").GetString()!)!.ReturnType.GetGenericArguments()[0];
        Assert.NotNull(JsonSerializer.Deserialize(fixture.GetProperty("response").GetRawText(), resultType, ManagementJson.Options));
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task EveryFacadeRejectsWrongSuccessStatus(string id)
    {
        var op = Operation(id); var fixture = Fixture(id, false);
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(Response(op, fixture,
            op.GetProperty("status").GetInt32() == 200 ? 201 : 200))));
        using var client = NewClient(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => Invoke(client, op, fixture));
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task EveryFacadeRejectsInvalidSuccessEnvelope(string id)
    {
        var op = Operation(id); var fixture = Fixture(id, false);
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)op.GetProperty("status").GetInt32())
        { Content = new StringContent("{", Encoding.UTF8, "application/json") })));
        using var client = NewClient(http);
        await Assert.ThrowsAsync<KaitenProtocolException>(() => Invoke(client, op, fixture));
    }

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task EveryFacadePreservesStatusCodeAndSafeProblem(string id, int status)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ClientTests.Json("""{"code":"Specific.Code","errorId":"correlation","detail":"private-body"}""", (HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        using var client = NewClient(http);
        var error = await Assert.ThrowsAsync<KaitenApiException>(() => Invoke(client, Operation(id), Fixture(id, false)));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal("Specific.Code", error.Code);
        Assert.Equal("correlation", error.ErrorId);
        Assert.DoesNotContain("private-body", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    internal static KaitenManagementClient NewClient(HttpClient http, int retries = 0)
        => new(http, new KaitenClientOptions { BaseUri = new Uri("https://gateway.test/proxy"), BearerToken = "private-token", MaxRetries = retries, RetryDelay = TimeSpan.Zero, MaxRetryDelay = TimeSpan.Zero });
    internal static HttpResponseMessage Response(JsonElement operation, JsonElement fixture, int? status = null)
        => new((HttpStatusCode)(status ?? operation.GetProperty("status").GetInt32()))
        { Content = new StringContent(fixture.GetProperty("response").ValueKind == JsonValueKind.Null ? "" : fixture.GetProperty("response").GetRawText(), Encoding.UTF8, "application/json") };
    internal static async Task<object?> Invoke(KaitenManagementClient client, JsonElement operation, JsonElement fixture, CancellationToken token = default)
    {
        var resource = typeof(KaitenManagementClient).GetProperty(operation.GetProperty("resource").GetString()!)!.GetValue(client)!;
        var method = resource.GetType().GetMethod(operation.GetProperty("methodName").GetString()!)!;
        var arguments = method.GetParameters().Select(parameter =>
        {
            if (parameter.ParameterType == typeof(CancellationToken)) return (object)token;
            if (parameter.Name is "request" or "query")
            {
                var element = fixture.GetProperty(parameter.Name);
                return element.ValueKind == JsonValueKind.Null ? null : JsonSerializer.Deserialize(element.GetRawText(), parameter.ParameterType, ManagementJson.Options);
            }
            return fixture.GetProperty("pathArguments").GetProperty(parameter.Name!).GetString();
        }).ToArray();
        var task = (Task)method.Invoke(resource, arguments)!;
        await task.ConfigureAwait(false);
        return method.ReturnType.IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
    }
    private static void CheckQuery(Uri uri, JsonElement expected)
    {
        var actual = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
        if (expected.ValueKind == JsonValueKind.Null) { Assert.Empty(actual); return; }
        foreach (var property in expected.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            var text = property.Value.ValueKind == JsonValueKind.Array ? string.Join(",", property.Value.EnumerateArray().Select(QueryValue)) : QueryValue(property.Value);
            Assert.Equal(text, actual[property.Name]);
        }
        Assert.Equal(expected.EnumerateObject().Count(p => p.Value.ValueKind != JsonValueKind.Null), actual.Count);
    }
    private static string QueryValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String && value.GetString()!.Contains('T') && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    private static void Equivalent(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            Assert.Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count());
            foreach (var property in expected.EnumerateObject()) Equivalent(property.Value, actual.GetProperty(property.Name));
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
            for (var i = 0; i < expected.GetArrayLength(); i++) Equivalent(expected[i], actual[i]);
        }
        else if (expected.ValueKind == JsonValueKind.String && expected.GetString()!.Contains('T') && expected.TryGetDateTimeOffset(out var instant))
            Assert.Equal(instant, actual.GetDateTimeOffset());
        else if (expected.ValueKind == JsonValueKind.String) Assert.Equal(expected.GetString(), actual.GetString());
        else Assert.Equal(expected.GetRawText(), actual.GetRawText());
    }
}
