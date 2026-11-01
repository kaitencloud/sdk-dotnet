#!/usr/bin/env python3
# Copyright 2026 KAITEN INC. SPDX-License-Identifier: Apache-2.0
"""Verify pinned, standalone contracts and the explicit operation coverage map."""
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent.parent
METHODS = {"get", "post", "put", "patch", "delete", "head", "options", "trace"}


def load_contracts():
    source = json.loads((ROOT / "contracts/source.json").read_text())
    documents = {}
    for name, pin in source["documents"].items():
        data = (ROOT / pin["path"]).read_bytes()
        if hashlib.sha256(data).hexdigest() != pin["sha256"]:
            raise ValueError(f"{name} contract checksum changed; explicitly re-pin the approved source")
        document = json.loads(data)
        if document["openapi"] != "3.1.0":
            raise ValueError("Only the pinned OpenAPI 3.1 contract is supported")
        operations = [(path, method) for path, item in document["paths"].items()
                      for method in item if method in METHODS]
        permitted = [op for op in operations if not forbidden_path(op[0])]
        if len(operations) != pin["restOperations"] or len(permitted) != pin["permittedOperations"]:
            raise ValueError(f"{name} operation count differs from the source pin")
        if len(document.get("webhooks", {})) != pin["webhooks"]:
            raise ValueError(f"{name} webhook declarations changed")
        documents[name] = document
    return documents


def forbidden_path(path):
    return path == "/ofrep/v1" or path.startswith("/ofrep/v1/")


def coverage_rows(documents):
    return sorted([
        {"document": name, "path": path, "method": method.upper(),
         "operationId": operation["operationId"]}
        for name, document in documents.items() for path, item in document["paths"].items()
        for method, operation in item.items() if method in METHODS
    ], key=lambda row: (row["document"], row["path"], row["method"]))


def main():
    expected = coverage_rows(load_contracts())
    coverage = json.loads((ROOT / "contracts/coverage.json").read_text())
    actual = [{key: row[key] for key in ("document", "path", "method", "operationId")}
              for row in coverage["operations"]]
    if actual != expected:
        raise ValueError("Every REST operation must have exactly one coverage entry")
    wired = {row["operationId"] for row in json.loads((ROOT / "contracts/batch1-api.json").read_text())}
    for row in coverage["operations"]:
        if (row["status"] == "management-facade") != (row["document"] == "core" and row["operationId"] in wired):
            raise ValueError("Facade coverage must match actual generated resource bindings")
        if forbidden_path(row["path"]):
            if row["status"] != "excluded-official-provider":
                raise ValueError("OFREP must never be a Kaiten client operation")
        elif row["status"] not in {"existing-client", "management-facade", "pending-parity"}:
            raise ValueError("Unknown coverage status (generation is not facade coverage)")
        if not row.get("reason"):
            raise ValueError("Coverage entries require an explicit implementation/exclusion reason")
    print("PASS: pinned contracts; 226 permitted Core + 11 Platform; 117 webhook declarations; explicit coverage")


if __name__ == "__main__":
    try:
        main()
    except (KeyError, ValueError, OSError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
