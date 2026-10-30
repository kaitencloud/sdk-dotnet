# Security policy

Report privately via GitHub Private Vulnerability Reporting for this repository,
when available, or security@kaiten.sh; not public issues. Include affected package/
version, impact, reproduction, mitigations; never real credentials, only placeholders.
KaitenCloud.Sdk0.1.1 is local/unpublished; no hypothetical release/backport promise.

PATs are vendor-org-wide server secrets, not customer/diner credentials. Separate
runtime/presenter secrets, enforce app membership/roles, never expose a Core proxy
or browser token. Missing/failed decisions cannot allow protected actions. Uncertain
quota outcomes cannot be blindly replayed.

Authenticated gateway only, private file/vault providers, HTTPS outside isolated
loopback development, redacted host telemetry. A token in a browser/repository/log
is compromised; rotate through supported Core management. The SDK cannot make
leaked credentials safe.
