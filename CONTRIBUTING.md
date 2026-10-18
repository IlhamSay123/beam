# Development

## Local Workflow

Use Windows and a stable .NET 10 SDK for desktop work. global.json selects the
latest installed stable .NET 10 feature band; a runtime alone cannot build Beam.
The WPF application hosts the configured
server; the standalone server Program.cs is not the application entry point.
For web regression tests, install Node.js 22+ and Microsoft Edge, then run
`npm ci` in tests/Beam.Web.Tests.

NuGet audits direct and transitive dependencies during restore. Vulnerability
findings fail restore/build through Directory.Build.props; do not suppress them.
If using a portable SDK folder, put it on PATH and set DOTNET_ROOT for the current
shell so browser integration tests spawn the same dotnet host.

Before proposing a change:

```powershell
dotnet build Beam.sln --configuration Release
dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --configuration Release
dotnet run --project tests/Beam.App.Smoke/Beam.App.Smoke.csproj --configuration Release
dotnet build tests/Beam.Web.Host/Beam.Web.Host.csproj --configuration Release
Push-Location tests/Beam.Web.Tests
npm ci
npm test
Pop-Location
```

Keep manual phone testing separate from automated fixture results. Include exact
steps, expected/actual results, platform versions, and any remaining test gaps.

## Important Contracts

- Close and tray Quit must stop the receiver and remove the tray icon.
- Desktop shutdown must wait for tracked queue work without blocking the dispatcher.
- Keep expensive file copies off the WPF UI thread.
- Do not acknowledge a phone copy/download before its user action succeeds.
- Queued, acknowledged, and permanently saved are different states.
- Preserve stored transfers when changing the UI or history format.
- Use fixture tokens and generated data in tests, screenshots, and issues.

Historical design notes under docs/superpowers may describe earlier behavior.
Current code, README, and docs/setup describe the implemented workflow.

## Screenshot updates

Use the isolated native and browser demo capture commands in
[docs/assets/README.md](docs/assets/README.md). Review the rendered images before
publishing. Keep phone captures labelled as browser-engine captures, and use
generated data instead of real transfers or pairing credentials.

For bug reports, include Windows and phone/browser versions, reproduction
steps, expected/actual behaviour and redacted diagnostics where useful.