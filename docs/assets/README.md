# UI captures

These images show the alpha.3 interface captured on 2026-10-18.
All filenames, notes and image content are generated fixtures. No personal
transfer data, production pairing secret or real phone session is used.

| Image | Source |
| --- | --- |
| beam-transfer.png | Native WPF Transfer workspace |
| beam-vault.png | Native WPF Vault with demo image and notes |
| beam-vault-small.png | Native WPF Vault at its smaller window size |
| beam-pill.png | Native compact pill |
| beam-startup-menu.png | Native pill context menu; no startup option is toggled |
| beam-safari-send.png | WebKit browser Send view with real fixture uploads |
| beam-safari-receive.png | WebKit browser Receive view with prepared Save action |
| beam-safari-vault.png | WebKit browser Vault with authenticated image preview |

Native captures render the actual application's WPF visual tree. Browser captures
run the actual page against an isolated Beam test server bound to loopback, at
390 × 900 CSS pixels with a 2× pixel scale. They demonstrate the phone layout;
they are not physical-iPhone screenshots or independent device acceptance tests.

## Reproduce

From the repository root on Windows with .NET 10 and Node.js installed:

```powershell
dotnet run --project tests/Beam.App.Smoke/Beam.App.Smoke.csproj --configuration Release -- --screenshots artifacts/screenshots/native
dotnet build tests/Beam.Web.Host/Beam.Web.Host.csproj --configuration Release
Push-Location tests/Beam.Web.Tests
npm ci
npx playwright install webkit
Pop-Location
node scripts/Capture-WebScreenshots.cjs artifacts/screenshots/web
```

The capture script shuts down its demo server and removes its uniquely named
temporary data folder. Generated output belongs under the ignored artifacts
folder. Inspect captures before copying selected images into this directory;
the native smoke harness also emits diagnostic pairing views that are not part
of the public gallery.