// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace KaitenCloud.ContractTool;

internal static class PackageCheck
{
    internal const string Version = "0.3.0-native-uncommitted";
    internal static async Task RunAsync(string root, string artifactLabel = "native-sdk-uncommitted", bool published = false)
    {
        if (string.IsNullOrEmpty(artifactLabel) || artifactLabel is "." or ".." || Path.GetFileName(artifactLabel) != artifactLabel)
            throw new ArgumentException("Artifact label must be a single directory name.", nameof(artifactLabel));
        var artifacts = Path.Combine(root, "artifacts", artifactLabel);
        var feed = Path.Combine(artifacts, "packages");
        var version = published ? "0.3.0" : Version;
        var package = Path.Combine(feed, $"KaitenCloud.Sdk.{version}.nupkg");
        using (var archive = ZipFile.OpenRead(package))
        {
            var spec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var stream = spec.Open();
            if (XDocument.Load(stream).Descendants().Any(element => element.Name.LocalName == "dependency" && ((string?)element.Attribute("id"))?.StartsWith("OpenFeature", StringComparison.Ordinal) == true))
                throw new InvalidDataException("SDK package must not depend on OpenFeature");
        }
        var temporary = Path.Combine(artifacts, "consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var config = XDocument.Load(Path.Combine(root, "tests/PackageConsumer/NuGet.Config"));
            config.Descendants("add").Single(element => (string?)element.Attribute("key") == "sdk-local").SetAttributeValue("value", feed);
            var ownedDependencies = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrEmpty(ownedDependencies) && Directory.Exists(ownedDependencies))
            {
                var source = config.Descendants("add").Single(element => (string?)element.Attribute("key") == "nuget.org");
                source.SetAttributeValue("value", Path.GetFullPath(ownedDependencies)); source.Attribute("protocolVersion")?.Remove();
            }
            var configPath = Path.Combine(temporary, "NuGet.Config"); config.Save(configPath);
            var positive = XDocument.Load(Path.Combine(root, "tests/PackageConsumer/PackageConsumer.csproj"));
            foreach (var compile in positive.Descendants("Compile")) compile.SetAttributeValue("Include", Path.GetFullPath(Path.Combine(root, "tests/PackageConsumer", (string)compile.Attribute("Include")!)));
            positive.Descendants("PackageReference").Single(element => (string?)element.Attribute("Include") == "KaitenCloud.Sdk").SetAttributeValue("Version", version);
            var positivePath = Path.Combine(temporary, "Positive.csproj"); positive.Save(positivePath);
            var cache = Path.Combine(temporary, "packages");
            await RunDotnet(root, ["restore", positivePath, "--packages", cache, "--configfile", configPath, "--verbosity", "quiet"]);
            await RunDotnet(root, ["build", positivePath, "-c", "Release", "--no-restore", "--verbosity", "quiet"]);
            var negativeDir = Path.Combine(temporary, "negative"); Directory.CreateDirectory(negativeDir);
            var negative = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net8.0"), new XElement("NuGetLockFilePath", "obj/packages.lock.json")),
                new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", "KaitenCloud.Sdk"), new XAttribute("Version", version)))));
            var negativePath = Path.Combine(negativeDir, "Negative.csproj"); negative.Save(negativePath);
            await File.WriteAllTextAsync(Path.Combine(negativeDir, "Obsolete.cs"), """
                using KaitenCloud.Sdk;
                using KaitenCloud.Sdk.Management;
                using System.Threading.Tasks;
                public static class Obsolete
                {
                    public static async Task Bind(IKaitenClient client, KaitenClient concrete, KaitenManagementClient management)
                    {
                        await client.EvaluateFlagAsync("flag", null);
                        await client.EvaluateAllFlagsAsync(null);
                        await concrete.EvaluateFlagAsync("flag", null);
                        await concrete.EvaluateAllFlagsAsync(null);
                        await client.ValidateInstanceLicenseAsync("instance");
                        await concrete.ValidateInstanceLicenseAsync("instance");
                        await management.Instances.ValidateLicenseAsync("instance");
                        FlagEvaluation single = null;
                        BulkFlagEvaluation bulk = null;
                        KaitenEvaluationException error = null;
                        LicenseValidation validation = null;
                        LicenseValidationStatus status = default;
                        KaitenOfrepOptions options = null;
                        var removed = typeof(KaitenOpenFeatureServiceCollectionExtensions);
                    }
                }
                """);
            await RunDotnet(root, ["restore", negativePath, "--packages", cache, "--configfile", configPath, "--verbosity", "quiet"]);
            var result = await Execute(root, ["build", negativePath, "-c", "Release", "--no-restore", "--verbosity", "quiet"]);
            if (result.Code == 0) throw new InvalidDataException("Obsolete public APIs compiled from package");
            var logPath = Path.Combine(artifacts, "negative-public-api.log"); await File.WriteAllTextAsync(logPath, result.Output);
            foreach (var symbol in new[] { "EvaluateFlagAsync", "EvaluateAllFlagsAsync", "ValidateInstanceLicenseAsync", "ValidateLicenseAsync", "FlagEvaluation", "BulkFlagEvaluation", "KaitenEvaluationException", "LicenseValidation", "LicenseValidationStatus", "KaitenOfrepOptions", "KaitenOpenFeatureServiceCollectionExtensions" })
                if (!result.Output.Split('\n').Any(line => Regex.IsMatch(line, "error CS(1061|0246)") && line.Contains(symbol, StringComparison.Ordinal))) throw new InvalidDataException("Expected negative compilation diagnostic absent for " + symbol);
            Console.WriteLine("PASS: native package metadata guard, direct-provider consumer builds, removed APIs cannot compile");
        }
        finally { Directory.Delete(temporary, recursive: true); } // Only this invocation's private cache/project.
    }
    private static async Task RunDotnet(string root, string[] args)
    {
        var result = await Execute(root, args);
        if (result.Code != 0) throw new InvalidDataException($"dotnet {args[0]} failed ({result.Code}): {result.Output}");
    }
    private static async Task<(int Code, string Output)> Execute(string root, string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch dotnet");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("Package verification command timed out"); }
        return (process.ExitCode, await output + await error);
    }
}
