# UniGetUI Auto-Update Testing Guide (ProductInfo-only path)

This guide validates the ProductInfo-only auto-update flow that reads from `productinfo.json`.

UniGetUI no longer falls back to the legacy updater logic. If the ProductInfo lookup fails, the update check fails.

## Files used

- `testing/productinfo.unigetui.test.json`
- Test artifacts expected by that file:
	- `UniGetUI.Installer.x64.exe`
	- `UniGetUI.Installer.arm64.exe`
	- `UniGetUI.x64.zip`
	- `UniGetUI.arm64.zip`

## What is being tested

- Default updater source is ProductInfo-based.
- Product key lookup for `Devolutions.UniGetUI`.
- Architecture-aware installer selection (`x64`/`arm64`, `exe` preferred).
- Hash validation (enabled by default).
- Debug-only override behavior via registry keys under `HKLM\Software\Devolutions\UniGetUI`.

## Release vs Debug behavior

- Release builds honor only `UpdaterProductInfoUrl` from `HKLM\Software\Devolutions\UniGetUI`.
- Release builds ignore `UpdaterProductKey` and all validation-bypass flags.
- Debug builds can read updater overrides from `HKLM\Software\Devolutions\UniGetUI` for local testing.
- Dangerous validation bypasses are for Debug/dev testing only:
	- `UpdaterAllowUnsafeUrls`
	- `UpdaterSkipHashValidation`
	- `UpdaterSkipSignerThumbprintCheck`
	- `UpdaterDisableTlsValidation`

## 1) Host the test files locally

From repository root:

```powershell
Push-Location testing
python -m http.server 8080
Pop-Location
```

Make sure these URLs are reachable:

- `http://127.0.0.1:8080/productinfo.unigetui.test.json`
- `http://127.0.0.1:8080/UniGetUI.Installer.x64.exe`
- `http://127.0.0.1:8080/UniGetUI.Installer.arm64.exe`

## 2) Configure updater overrides (test mode)

These steps apply to a Debug build only.

Run in PowerShell:

```powershell
$regPath = 'HKLM:\Software\Devolutions\UniGetUI'
New-Item -Path $regPath -Force | Out-Null

# Point updater to local productinfo
Set-ItemProperty -Path $regPath -Name 'UpdaterProductInfoUrl' -Value 'http://127.0.0.1:8080/productinfo.unigetui.test.json'

# Product key inside productinfo JSON
Set-ItemProperty -Path $regPath -Name 'UpdaterProductKey' -Value 'Devolutions.UniGetUI'

# Allow local http URL and local domain for package downloads
Set-ItemProperty -Path $regPath -Name 'UpdaterAllowUnsafeUrls' -Type DWord -Value 1

# Keep hash validation enabled for normal test pass
Set-ItemProperty -Path $regPath -Name 'UpdaterSkipHashValidation' -Type DWord -Value 0

# Keep signer thumbprint validation enabled for normal test pass
Set-ItemProperty -Path $regPath -Name 'UpdaterSkipSignerThumbprintCheck' -Type DWord -Value 0

# Optional only for HTTPS cert troubleshooting in test environments
Set-ItemProperty -Path $regPath -Name 'UpdaterDisableTlsValidation' -Type DWord -Value 0
```

## 3) Trigger update check in UniGetUI

1. Launch UniGetUI.
2. Go to **Settings → General**.
3. Click **Check for updates**.

Expected result:

- Updater reads the local `productinfo.unigetui.test.json`.
- It picks the correct architecture `exe` installer.
- Download starts and hash is validated.
- Update banner/toast appears when update is ready.

## 4) Negative test: hash mismatch protection

Use one of these methods:

- Replace installer file content but keep original hash in JSON, or
- Edit hash in JSON to an incorrect value.

Expected result:

- Hash validation fails.
- Update is aborted with installer authenticity error.

## 5) Negative test: block unsafe URLs

Set:

```powershell
Set-ItemProperty -Path 'HKLM:\Software\Devolutions\UniGetUI' -Name 'UpdaterAllowUnsafeUrls' -Type DWord -Value 0
```

Expected result with local `http://127.0.0.1` URLs:

- Updater rejects source/download URL as unsafe.
- No installer launch.

## 6) Negative test: broken ProductInfo source

Use one of these methods:

- Point `UpdaterProductInfoUrl` to a missing URL, or
- Point `UpdaterProductInfoUrl` to a malformed JSON file, or
- In a Debug build only, point `UpdaterProductKey` to a non-existent product.

Example:

```powershell
Set-ItemProperty -Path 'HKLM:\Software\Devolutions\UniGetUI' -Name 'UpdaterProductInfoUrl' -Value 'http://127.0.0.1:8080/does-not-exist.json'
```

Expected result:

- Productinfo check fails.
- UniGetUI reports the update-check failure.
- No fallback source is used.

## 7) Optional: disable signer thumbprint check (test-only)

Use this only if your local installer is unsigned or signed with a non-Devolutions certificate.

```powershell
Set-ItemProperty -Path 'HKLM:\Software\Devolutions\UniGetUI' -Name 'UpdaterSkipSignerThumbprintCheck' -Type DWord -Value 1
```

## 8) Release-build hardening check

Run the same registry override setup against a Release build.

Expected result:

- Release build honors `UpdaterProductInfoUrl` only if it still passes normal source validation.
- Release build ignores `UpdaterProductKey`.
- Release build ignores validation bypass flags.
- Updater uses the configured ProductInfo URL and the built-in `Devolutions.UniGetUI` product key.

## 9) Cleanup after testing

Reset to default production behavior:

```powershell
$regPath = 'HKLM:\Software\Devolutions\UniGetUI'
Remove-ItemProperty -Path $regPath -Name 'UpdaterProductInfoUrl' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $regPath -Name 'UpdaterProductKey' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $regPath -Name 'UpdaterAllowUnsafeUrls' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $regPath -Name 'UpdaterSkipHashValidation' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $regPath -Name 'UpdaterSkipSignerThumbprintCheck' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $regPath -Name 'UpdaterDisableTlsValidation' -ErrorAction SilentlyContinue
```

With all override values removed, UniGetUI uses:

- `https://devolutions.net/productinfo.json`
- product key `Devolutions.UniGetUI`
- safety checks enabled
- hash validation enabled
