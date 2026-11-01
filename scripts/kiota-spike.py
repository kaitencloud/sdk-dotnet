#!/usr/bin/env python3
# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
"""Reproduce the bounded INTERNAL Kiota experiment; never edits product sources."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
sys.dont_write_bytecode = True
from check_contracts import ROOT, forbidden_path, load_contracts


def fingerprints(directory):
    return {str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob("*.cs"))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Check the reviewed generated C# fingerprints")
    parser.add_argument("--kiota", help="Path to an already installed Kiota 1.30.0 executable")
    args = parser.parse_args()
    config = json.loads((ROOT / "contracts/kiota-spike.json").read_text())
    binary = args.kiota or os.environ.get("KIOTA") or shutil.which("kiota")
    if binary is None:
        candidate = Path.home() / ".dotnet/tools/kiota"
        if candidate.is_file():
            binary = str(candidate)
    if binary is None:
        raise ValueError("Kiota 1.30.0 is required; supply --kiota. No tool is installed automatically.")
    environment = dict(os.environ, **config["environment"])
    version = subprocess.check_output([binary, "--version"], env=environment, text=True).strip()
    if version != config["version"] + "+" + config["sourceCommit"]:
        raise ValueError(f"Wrong generator identity: {version}")
    documents = load_contracts()
    destination = ROOT / "artifacts/phase0/generated"
    destination.mkdir(parents=True, exist_ok=True)
    expected_path = ROOT / "contracts/kiota-spike.expected.json"
    all_hashes = {}
    with tempfile.TemporaryDirectory(prefix="generation-", dir=ROOT / "artifacts/phase0") as temporary:
        work = Path(temporary)
        for name, document in documents.items():
            # Policy filtering precedes the generator. Webhook declarations/schemas remain intact.
            filtered = dict(document)
            filtered["paths"] = {path: item for path, item in document["paths"].items()
                                 if not forbidden_path(path)}
            spec = work / (name + ".json")
            spec.write_text(json.dumps(filtered, sort_keys=True, indent=2) + "\n")
            hashes = []
            for iteration in (1, 2):
                output = work / f"{name}-{iteration}"
                command = [binary, "generate", "--openapi", str(spec), "--language", config["language"],
                           "--type-access-modifier", config["access"], "--class-name", name.title() + "SpikeClient",
                           "--namespace-name", "KaitenCloud.Spike." + name.title(), "--output", str(output),
                           "--serializer", config["serializer"], "--deserializer", config["deserializer"],
                           "--log-level", "Warning"]
                for path in config["slices"][name]:
                    command.extend(["--include-path", path])
                result = subprocess.run(command, env=environment, text=True, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, timeout=120)
                (ROOT / f"artifacts/phase0/kiota-{name}-{iteration}.log").write_text(result.stdout)
                if result.returncode:
                    raise ValueError(f"{name} generation failed; see artifacts/phase0/kiota-{name}-{iteration}.log")
                found = fingerprints(output)
                if not found:
                    raise ValueError(f"{name} generation emitted no C# source")
                for path in output.rglob("*.cs"):
                    content = path.read_text()
                    if "/ofrep/v1" in content or "EvaluateFlag" in content or "EvaluateFlagsBulk" in content:
                        raise ValueError(f"Forbidden evaluation generated: {path.name}")
                hashes.append(found)
                if iteration == 1:
                    target = destination / name
                    if target.exists():
                        shutil.rmtree(target)  # Only this script's ignored experiment output.
                    shutil.copytree(output, target, ignore=shutil.ignore_patterns("*.json"))
            if hashes[0] != hashes[1]:
                changed = [path for path in set(hashes[0]) | set(hashes[1])
                           if hashes[0].get(path) != hashes[1].get(path)]
                diagnostic = ROOT / "artifacts/phase0/generation-differences.json"
                diagnostic.write_text(json.dumps({"document": name, "files": sorted(changed)}, indent=2))
                for iteration in (1, 2):
                    target = ROOT / f"artifacts/phase0/nonrepro-{name}-{iteration}"
                    if target.exists():
                        shutil.rmtree(target)
                    shutil.copytree(work / f"{name}-{iteration}", target)
                raise ValueError(f"{name} C# generation is not byte-for-byte reproducible; see {diagnostic}")
            all_hashes[name] = hashes[0]
    encoded = json.dumps(all_hashes, sort_keys=True, indent=2) + "\n"
    (ROOT / "artifacts/phase0/generated-fingerprints.json").write_text(encoded)
    if args.check:
        if expected_path.read_text() != encoded:
            raise ValueError("Generated C# differs from reviewed experiment; inspect before updating fingerprints")
    print(f"PASS: Kiota {version}; two identical generations; {sum(map(len, all_hashes.values()))} internal C# files; no OFREP operations")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
