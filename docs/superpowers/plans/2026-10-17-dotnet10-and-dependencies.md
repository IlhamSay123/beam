# .NET 10 and Dependency Hardening Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for this bounded migration in the existing implementation worktree.

**Goal:** Move every Beam executable and test host to stable .NET 10 and remove the two flagged legacy dependency chains without changing transfer or pairing behavior.

**Architecture:** Keep WPF, ASP.NET Core, xUnit v2 and the current storage/protocol contracts. Upgrade compatible upstream dependencies rather than suppressing audit findings. Pin the stable SDK major in global.json and align CI, integration-host lookup and source documentation.

**Tech Stack:** .NET 10, WPF, ASP.NET Core, xUnit v2, Playwright Chromium/WebKit.

**Spec:** User request on 2026-10-17: upgrade to .NET 10 and fix the two dependency issues identified by the prior audit.

## Global Constraints

- Preserve Documents/Beam token, files, history and safe-retry receipts.
- Keep Windows target floor windows10.0.19041.0 and the existing desktop/Safari UI.
- Preserve Safari transfer semantics and optional mDNS; no HTTP/token redesign.
- Do not disable vulnerability auditing or suppress vulnerable-package warnings.
- Keep historical plans and alpha.1 release notes accurate for their original checkpoint.
- Use an SDK downloaded from Microsoft and verified against release metadata; keep SDK tooling outside the repository.

## Review Focus

- All projects and real-server browser fixtures must use .NET 10, not stale net8.0 binaries.
- mDNS must remain binary-compatible with its promoted IPNetwork2 dependency.
- Legacy dependencies must be removed from the resolved/published graph, rather than hidden from audit.
- Desktop shutdown and persistent receipts must retain existing tested behavior.
- Self-contained Windows output must include .NET 10 and all browser/license assets, without personal data.

## Task 1: Runtime and dependency migration

**Files:** global.json; src/Beam.App/Beam.App.csproj; src/Beam.Server/Beam.Server.csproj; tests/Beam.Server.Tests/Beam.Server.Tests.csproj; tests/Beam.App.Smoke/Beam.App.Smoke.csproj; tests/Beam.Web.Host/Beam.Web.Host.csproj.

**Interfaces:** Produces net10.0 server/test hosts and net10.0-windows10.0.19041.0 desktop binaries. Existing HTTP/storage interfaces stay unchanged.

- [x] Trace the current audit failures with dotnet nuget why. Baseline: mDNS -> IPNetwork2 2.1.2 -> NETStandard.Library 1.6.1 and xUnit 2.5.3 -> NETStandard.Library 1.6.1 pull the flagged 4.3.0 packages.
- [x] Install and verify stable Microsoft .NET 10 SDK outside the repository.
- [x] Retarget all five projects, add stable-major SDK selection, update xUnit to 2.9.3 and promote IPNetwork2 to compatible 2.6.618. Update the test SDK/runner/coverage tooling to supported stable versions.
- [x] Restore/build and audit every project, including smoke and browser hosts outside Beam.sln. Expected: zero vulnerable packages and zero build errors; investigate all warnings.
- [x] Run all backend checks and native smoke checks. Expected: preserved existing behavior, including clean-restart receipts and lifecycle.

## Task 2: Development, CI and package integration

**Files:** .github/workflows/ci.yml; .github/workflows/package.yml; tests/Beam.Web.Tests/integration.test.cjs; README.md; CONTRIBUTING.md; docs/setup/verification.md; packaging release source notes as needed.

**Interfaces:** Consumes Task 1 net10.0 hosts; produces working documented commands and CI using stable .NET 10.

- [x] Change the browser integration DLL path to net10.0 and all setup-dotnet inputs to 10.0.x. Ensure SDK selection and CI agree.
- [x] Update current build requirements and verification notes; preserve dated alpha.1 facts and clearly distinguish current source from its old downloadable package.
- [x] Build the real browser host, then run npm test serially for Chromium and WebKit. Expected: 92 checks per engine pass against the .NET 10 host.
- [x] Publish a self-contained Windows x64 verification package to a new ignored artifact directory, with an explicit unreleased version. Check runtime version, assets, checksum and dependency manifests.
- [x] Request one read-only review of the final migration; resolve material findings with meaningful regression checks.
- [x] Record exact verification/audit/package results, then commit and update the existing GitHub source workflow after all checks pass.

## Execution Record

- Baseline: clean implementation worktree at 82d1550; preceding milestone passed 192 backend checks, 92 checks per browser engine, native smoke and GitHub CI.
- SDK decision: use F:/beam-tooling/dotnet10 for local verification because this session cannot perform an elevated machine-wide SDK install. Microsoft SHA-512 metadata validates the SDK archive. The repository itself requires a normal .NET 10 SDK installation on other developer machines.
- Configuration migration is verified by actual restore/build/audit and the existing regression suites; do not add tests that merely match project-file strings.

- Completed verification: zero-warning Release solution/host builds; backend 192/192; Chromium 92/92; WebKit 92/92; native smoke PASS; genuine mDNS startup/disposal PASS; all five NuGet audits clean; no legacy flagged package entries in resolved or published graphs; self-contained .NET 10.0.13 ZIP/checksum verified.
- Independent read-only review: no Critical, Important or Minor finding. Historical alpha.1 notes and physical-device acceptance remain accurately scoped.
