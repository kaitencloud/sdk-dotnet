// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaitenCloud.Sdk;

public abstract class KaitenException(string message) : Exception(message);
public sealed class KaitenNetworkException() : KaitenException("Kaiten gateway could not be reached; the outcome of a mutation may be unknown.");
public sealed class KaitenTimeoutException() : KaitenException("Kaiten request timed out; the outcome of a mutation may be unknown.");
public sealed class KaitenCredentialException() : KaitenException("Kaiten credentials could not be obtained or were invalid.");
public sealed class KaitenProtocolException() : KaitenException("Kaiten returned an invalid or unsupported response; no security decision is available.");

public sealed record KaitenProblem
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public int? Status { get; init; }
    public string? Detail { get; init; }
    public string? Instance { get; init; }
    public string? Code { get; init; }
    public string? ErrorId { get; init; }
    public JsonElement? Errors { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

// Message/ToString do not include remote detail, routes, credentials or raw bodies.
// The explicit Problem properties may contain server data: don't log them indiscriminately.
public sealed class KaitenApiException(HttpStatusCode statusCode, KaitenProblem? problem = null)
    : KaitenException($"Kaiten API request failed (HTTP {(int)statusCode}).")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public KaitenProblem? Problem { get; } = problem;
    public string? Code => Problem?.Code;
    public string? ErrorId => Problem?.ErrorId;
}

public sealed class KaitenEvaluationException(FlagEvaluation evaluation, HttpStatusCode? statusCode = null)
    : KaitenException("Kaiten flag evaluation failed; no successful value is available.")
{
    public FlagEvaluation Evaluation { get; } = evaluation;
    public string? ErrorCode => Evaluation.ErrorCode;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
