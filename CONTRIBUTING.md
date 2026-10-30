# Contributing

Run task check or README commands before review. Real integration needs explicit
isolated prerequisites and snapshot restoration; not normal CI. Never commit
credentials, .local, bin/obj or artifacts. Target net8.0/C#12; keep typed APIs small,
align with actual Core schemas, test behavior. Do not invent Platform/billing here.

Apache-2.0 contributions require DCO sign-off (git commit --signoff), see DCO.md.
Follow user review/commit preferences: DCO does not authorize committing/publishing.
Use semver for releases; update metadata, consumer version and lockfiles together.
