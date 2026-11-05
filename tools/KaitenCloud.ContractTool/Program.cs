// Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
using KaitenCloud.ContractTool;

try
{
    var root = Directory.GetCurrentDirectory();
    if (!File.Exists(Path.Combine(root, "contracts/source.json"))) throw new InvalidDataException("Run from the SDK repository root.");
    if (args is not (["contracts"] or ["package"] or ["package", _] or ["package", _, "--published"] or ["generate"] or ["fixtures"] or ["generate", "--check"] or ["fixtures", "--check"]))
        throw new ArgumentException("Usage: dotnet run --project tools/KaitenCloud.ContractTool -- contracts|generate|fixtures [--check] | package [artifact-label [--published]]");
    var check = args.Contains("--check");
    switch (args[0])
    {
        case "contracts": Contracts.Check(root); Console.WriteLine("PASS: pinned snapshots, REST/webhook counts and explicit coverage"); break;
        case "generate": Generation.Run(root, check); Console.WriteLine("PASS: 64 typed operations; deterministic source and API manifest"); break;
        case "fixtures":
            var output = new Fixtures(Contracts.Load(root)["core"]).Render((System.Text.Json.Nodes.JsonArray)Json.Read(Path.Combine(root, "contracts/batch1-api.json")));
            var path = Path.Combine(root, "tests/KaitenCloud.Sdk.Tests/Fixtures/management-wire.json");
            if (check)
            {
                if (File.ReadAllText(path) != Json.Render(output)) throw new InvalidDataException("Wire fixture drift");
            }
            else File.WriteAllText(path, Json.Render(output));
            Console.WriteLine("PASS: 128 full/minimal schema-shaped wire fixtures"); break;
        case "package": await PackageCheck.RunAsync(root, args.Length >= 2 ? args[1] : "native-sdk-uncommitted", args.Contains("--published")); break;
    }
    return 0;
}
catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine("FAIL: " + error.Message);
    return 1;
}
