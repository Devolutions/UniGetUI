# Publishing Windows apps (.exe and .msi)

The software catalog can offer apps that no package manager carries: an administrator publishes
the vendor's `.exe` or `.msi` unchanged, and users install, update and uninstall it from the
**Software Catalog** page. This is the Windows counterpart of the macOS feeds described in
[PKG_FEEDS.md](PKG_FEEDS.md), and one feed folder can serve both.

For most installers there is nothing to write. UniGetUI reads each file's identity, version and
silent switches from the file itself, then looks for the app in **Apps and Features** (the
`Uninstall` registry keys of the machine and of the user):

- **`.msi`:** identified by its `UpgradeCode` (or its `ProductCode` when it has none), at its
  `ProductVersion`. Installed when Windows Installer lists a product of that upgrade code, or the
  product itself. Installed with `msiexec /i <file> /qn /norestart`.
- **`.exe`:** identified by the product name in its version resource, at its product version.
  Installed when an Apps and Features entry carries that name (a trailing version or architecture
  is ignored) from the same publisher. UniGetUI recognizes these installer technologies and their
  silent switches:

  | Technology | Recognized by | Install | Uninstall |
  |---|---|---|---|
  | Inno Setup | its setup data or version comment | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` |
  | NSIS | the header of its appended data | `/S` | `/S` |
  | WiX bundle (Burn) | its `.wixburn` section | `/quiet /norestart` | `/quiet /norestart` |

  Any other `.exe` (InstallShield, Advanced Installer, a vendor's own bootstrapper) needs its
  silent switches written in a manifest, below.

So an app that was deployed by Intune, Configuration Manager or by hand is recognized, and updates
are offered for it.

## Set up a feed once

A feed is a folder, a share or a web folder holding `.exe` and `.msi` files. In the Catalog Editor,
select a catalog and choose **Package feed**: enter the folder or web address, then **Check feed**
lists every package it publishes and every file it skips, with the reason. Or declare it as the
`pkgFeed` of a catalog in the machine-wide catalog file, `%ProgramData%\Devolutions\UniGetUI\SoftwareCatalog.json`:

```json
{
  "version": 1,
  "catalogs": [
    {
      "id": "windows-apps",
      "name": "Windows apps",
      "packages": [],
      "pkgFeed": "\\\\fileserver\\software\\windows"
    }
  ]
}
```

Every package in the feed appears in that catalog. `packages` may stay empty; any entry listed
there as well is shown alongside the feed's packages. Outside the catalog page the id is the
package's source, as in `Installer packages: windows-apps`.

A feed can be:

| Location | Example | What UniGetUI reads |
|---|---|---|
| A web folder (https) | `https://software.example.com/windows/` | `index.json` in that folder |
| A local folder or a share | `\\fileserver\software\windows` | the `.exe` and `.msi` files directly |

Subfolders up to three levels deep are read too. Files whose name contains `uninst` are skipped,
since they are uninstallers.

### Signatures

Before installing, UniGetUI checks the installer's Authenticode signature the way Windows checks a
download (`WinVerifyTrust`, with revocation checks). Vendors' installers are signed. An installer
that is unsigned, tampered with, or signed by a certificate this computer does not trust is not
installed.

To publish your own unsigned installers, add `"allowUnsignedPackages": true` to that catalog, and
keep its feed where only administrators can change it.

## When the file cannot say it: the manifest

A `.json` file with the installer's name (`Vendor.exe` → `Vendor.json`) fills in or overrides what
UniGetUI reads. Every field is optional:

```json
{
  "id": "Vendor.Tool",
  "name": "Vendor Tool",
  "version": "5.1",
  "installArguments": "-s -norestart",
  "uninstallArguments": "-s",
  "displayName": "Vendor Tool Pro",
  "uninstallKey": "{6B1F...}_is1",
  "upgradeCode": "{11111111-2222-3333-4444-555555555555}",
  "publisher": "Vendor Inc.",
  "architectures": ["x64"],
  "scope": "user"
}
```

- `installArguments`: the switches that install without any window or restart. Required for an
  installer UniGetUI does not recognize; for an `.msi` they replace `/qn /norestart`.
- `uninstallArguments`: the switches added to the uninstaller the app registers (its
  `UninstallString`) to make it silent. Given, they are used even when the app registers a silent
  command of its own. For an `.msi` they replace `/qn /norestart`.
- `uninstallKey`: the exact name of the key the installer writes under `...\CurrentVersion\Uninstall`.
  The most reliable way to recognize an installed `.exe`; with it, names are not compared.
- `displayName`: the name the app shows in Apps and Features, when it differs from its product name.
- `upgradeCode`: for a WiX bundle, its upgrade code, which recognizes every version of it.
- `architectures`: `x86`, `x64` or `arm64`. An `.msi` says it itself; an `.exe` is usually a 32-bit
  installer whatever it installs, so give it when the feed holds one build per architecture.
- `scope`: `user` or `machine`. An `.exe` starts without administrator rights and asks for them
  itself when it needs them, as Inno Setup, WiX bundles and most installers do; give `machine` for
  one that fails instead of asking, so that UniGetUI shows the UAC prompt first. An `.msi` is
  installed for every user, behind the UAC prompt, unless it is a per-user package
  (no `ALLUSERS`, or `ALLUSERS=2` with `MSIINSTALLPERUSER=1`) or the manifest gives `user`.

The manifest is read with the file: changing it is enough, no restart needed.

## Publish a package

**Folder or share feed:** copy the installer, and its manifest if it needs one, into the folder.

**Web feed:** copy the files into the folder you publish, then regenerate its index and upload the folder:

```powershell
& "$env:ProgramFiles\UniGetUI\UniGetUI.exe" pkg-index D:\publish\windows
```

The command reads every `.exe` and `.msi` in the folder, with their manifests, and writes
`index.json` next to them, with each package's identity, version, silent switches and SHA-256.
Run it again after every change. A file it cannot read is reported and left out of the index.

A folder can hold the packages of both platforms. Run `pkg-index` on Windows for the `.exe` and
`.msi` files and on a Mac for the `.pkg`, `.dmg` and `.zip` files: each keeps the entries the other
wrote, as long as those files are unchanged. Windows entries are listed separately in `index.json`,
so Macs, including those on older UniGetUI releases, never offer a Windows installer, and Windows
never offers a Mac package.

Each package shows the icon of its installer: an `.exe`'s own icon, or the product icon an `.msi`
gives Apps and Features (`ARPPRODUCTICON`, else its first shortcut's icon). To use another icon,
put a `.png`, `.jpg` or `.ico` with the same name next to the file (`Zoom.exe` → `Zoom.png`).

To offer an update, publish the newer file. When the feed holds several versions of a package,
users get the newest one that runs on their computer, and a native build is preferred: on an Arm
computer an `arm64` build wins over an `x64` one, which wins over an `x86` one.

## What users see

- **Install** and **Update** copy the installer from the share (or download it), check its hash,
  its identity and its signature, then run it silently. An installer for every user asks for
  administrator rights through the UAC prompt; a per-user one installs without it. Once it runs it
  cannot be canceled.
- **Uninstall** runs what the app registered in Apps and Features, made silent: `msiexec /x` for an
  `.msi`, the app's silent uninstall command when it registers one, else its uninstaller with the
  switches of its technology or of the manifest. An app UniGetUI cannot uninstall silently is refused.
- Some installers register a slightly different version than their own (with a build number added
  or dropped). After UniGetUI installs a package, it remembers which version that left, so the same
  update is not offered again.
- When the feed is unreachable (off the VPN, share not reachable), UniGetUI keeps showing the
  packages it last listed. Installing needs the feed again.

## Limits

- An `.msi` must be a single file: one that installs from a cabinet beside it (`data1.cab`) or from
  uncompressed files is skipped, with the reason shown by **Check feed** and `pkg-index`.
- An `.exe` without a version in its version resource needs `version` in its manifest.
- An `.exe` is recognized by name only when the manifest gives no `uninstallKey`, so an app renamed
  in Apps and Features is not recognized without it.
- When the name matches only once a version is removed from it (`Microsoft .NET SDK 8.0.133`), the
  entry must share the package's major version. Products installed side by side within one major
  version (SDK feature bands, runtimes) cannot be told apart by name: give them an `uninstallKey`
  or `upgradeCode`.
- InstallShield and other installers have no standard silent switches; their manifest must give them.
- There is no helper service as on macOS: an installer for every user always asks for administrator rights.
