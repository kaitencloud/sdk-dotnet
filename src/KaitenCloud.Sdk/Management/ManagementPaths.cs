// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KaitenCloud.Sdk.Management.Models;
namespace KaitenCloud.Sdk.Management;

internal static class ManagementPaths
{
    internal static string Segment(string value, bool slug)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value is "." or ".." ||
            value.Any(c => char.IsControl(c) || "/\\%?#".Contains(c)))
            throw new ArgumentException("Invalid Kaiten path parameter.");
        if (slug && (value.Length is < 2 or > 100 || !Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9-]*[a-z0-9]\\z", RegexOptions.CultureInvariant)))
            throw new ArgumentException("Invalid Kaiten slug.");
        return Uri.EscapeDataString(value);
    }
    internal static void ValidateQuery(IManagementModel query)
    {
        try { query.Validate(); }
        catch (JsonException) { throw new ArgumentException("Invalid Kaiten management query.", nameof(query)); }
    }
    internal static void Query(ref string path, string name, IOptional optional, bool explode)
    {
        if (!optional.IsSpecified || optional.UntypedValue is null) return;
        var value = optional.UntypedValue;
        var values = value is IEnumerable sequence and not string ? sequence.Cast<object?>().Select(Format).ToArray() : [Format(value)];
        if (!explode) values = [string.Join(",", values)];
        foreach (var text in values)
            path += (path.Contains('?') ? "&" : "?") + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(text);
    }
    private static string Format(object? value) => value switch
    {
        null => throw new ArgumentException("Invalid query value."),
        bool boolean => boolean ? "true" : "false",
        DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Enum enumeration => JsonSerializer.SerializeToElement(enumeration, enumeration.GetType(), ManagementJson.Options).GetString()!,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!
    };
}
