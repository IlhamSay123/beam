# Development

## Local Workflow

Use Windows and .NET 8 for desktop work. The WPF application hosts the configured
server; the standalone server Program.cs is not the application entry point.
For web regression tests, install Node.js 22+ and Microsoft Edge, then run
`npm ci` in tests/Beam.Web.Tests.

Before proposing a change:

```powershell
dotnet build Beam.sln --configuration Release
dotnet test tests/Beam.Server.Tests/Beam.Server.Tests.csproj --configuration Release
dotnet run --project tests/Beam.App.Smoke/Beam.App.Smoke.csproj --configuration Release
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
