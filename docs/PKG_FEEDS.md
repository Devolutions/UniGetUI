# Publishing macOS apps (.pkg, .dmg and .zip)

The software catalog can offer apps that no package manager carries: an administrator publishes
the vendor's `.pkg`, `.dmg` or `.zip` unchanged, and users install and update it from the
**Software Catalog** page.

Windows `.exe` and `.msi` installers are published the same way, from the same feed if you like:
see [WINDOWS_INSTALLER_FEEDS.md](WINDOWS_INSTALLER_FEEDS.md).

There is nothing to repackage and no detection rule to write. UniGetUI reads each file's
identifier, version and app from the file itself, then checks the Mac:

- **`.pkg`:** installed when macOS holds its receipt (`pkgutil --pkgs`), at the receipt's version.
  Without a receipt, the app the package installs counts too when it is in place under its bundle
  id, so an app that a management tool or the user installed another way is recognized.
- **`.dmg`:** a disk image with one app at its root (the usual "drag to Applications" image).
  Installed when that app is in `/Applications` under its bundle id, at its bundle version. A disk
  image holding an installer package instead is treated as that `.pkg`.
- **`.zip`:** an archive holding one app, at its root or one folder down, as some vendors ship
  it (Visual Studio Code, for one). Recognized like a `.dmg`'s app.

## Set up a feed once

A feed is a location holding `.pkg`, `.dmg` and `.zip` files. In the Catalog Editor, select a
catalog and choose **Package feed**: enter the folder or web address, then **Check feed** lists
every package it publishes and every file it skips, with the reason. Or declare it as the
`pkgFeed` of a catalog in the machine-wide catalog file, `/Library/Application Support/Devolutions/UniGetUI/SoftwareCatalog.json`:

```json
{
  "version": 1,
  "catalogs": [
    {
      "id": "mac-apps",
      "name": "Mac apps",
      "packages": [],
      "pkgFeed": "https://software.example.com/mac/"
    }
  ]
}
```

Every package in the feed appears in that catalog. `packages` may stay empty; any entry listed
there as well is shown alongside the feed's packages. Keep `"packages": []` even when it is empty:
UniGetUI versions without feed support reject a catalog without it.

A feed is identified by its catalog's `id`, so renaming the catalog keeps everything in place, and
two catalogs may share a name or even a feed. Outside the catalog page (Installed, Updates, bundles,
the command line) the id is the package's source, as in `Installer packages: mac-apps`, so choose
a readable one. To list a feed's package by hand in another catalog, use `"ManagerName": "pkg"`
and the feed catalog's `id` as `"Source"`.

### Signatures

Before installing, UniGetUI asks Gatekeeper about the package the same way macOS checks anything
downloaded: it must be signed with a Developer ID and notarized by Apple. Vendors' own packages
and disk images usually are. A package Gatekeeper rejects is not installed.

To publish your own unsigned packages, add `"allowUnsignedPackages": true` to that catalog. Its
packages are then installed as they are, so keep its feed where only administrators can change
it: UniGetUI refuses an unsigned file, or a file in a folder, that any user on the Mac can write
to (such as `/Users/Shared`).

A feed can be:

| Location | Example | What UniGetUI reads |
|---|---|---|
| A web folder (https) | `https://software.example.com/mac/` | `index.json` in that folder |
| A local folder or a mounted share | `/Volumes/Software/mac` | the `.pkg` and `.dmg` files directly |

Plain `http` is refused (except on `localhost`, for testing, and never by the package helper below),
because the index is what pins each package's hash.

## Publish a package

**Folder or share feed:** copy the `.pkg` or `.dmg` into the folder. Subfolders up to three levels
deep are read too.

**Web feed:** copy the file into the folder you publish, then regenerate its index and upload the
folder:

```bash
/Applications/UniGetUI.app/Contents/MacOS/uniget pkg-index /path/to/mac
```

The command reads every `.pkg` and `.dmg` in the folder and writes `index.json` next to them, with
each package's identifier, version, supported architectures, app and SHA-256. Run it on a Mac (disk
images are read with `hdiutil`), and again after every change. A file it cannot read is reported and
left out of the index.

Each package shows the icon of the app it installs, taken from the `.dmg`'s app or from the
`.pkg`'s payload. To use another icon, put a `.png` or `.jpg` with the same name next to the file
(`Zoom.pkg` → `Zoom.png`). `pkg-index` saves the app's icon next to each file that has none, so a
web feed serves it too.

To offer an update, publish the newer file. You can delete the older one or keep it. When the
feed holds several versions of a package, users get the newest one that runs on their Mac. A
native build is preferred over one that needs Rosetta, and an `arm64`-only build is never offered
to an Intel Mac.

## What users see

- **Install** and **Update** ask for an administrator password with the standard macOS prompt,
  then, as root, download the file (or copy it from the share) and check its hash, identity and
  signature. A `.pkg` then runs through
  `installer`, and a `.dmg`'s app is copied to `/Applications`, replacing the previous version
  only once the new one is in place. Once that step starts it cannot be canceled, since stopping
  it halfway would leave a broken app.
- Packages cannot be uninstalled from UniGetUI.
- With the package helper installed (below), no password is asked, and standard users can
  install too.
- When the feed is unreachable (off the VPN, share not mounted), UniGetUI keeps showing the
  packages it last listed. Installing needs the feed again.

## Installing without an administrator password

By default, installing asks for an administrator password, which standard users do not have. The
package helper is a small root service, like a management agent's, that installs what the catalog
publishes without asking. Set it up once per Mac from a deployment script, after UniGetUI itself:

```bash
sudo /Applications/UniGetUI.app/Contents/MacOS/UniGetUI pkg-helper install-daemon
```

It runs from `/Library/LaunchDaemons/com.devolutions.unigetui.pkghelper.plist` and logs to
`/Library/Logs/UniGetUI-pkg-helper.log`. `pkg-helper remove-daemon` removes it.

A user can only ask it for a package by catalog id, package id and version. The helper reads the
catalog, lists the feed, stages and checks the package itself, with the same hash, identity and
Gatekeeper checks as any install, and refuses:

- anything a catalog does not publish;
- a catalog file, folder feed or package file that a standard user could change: each must belong
  to root or an administrator and be writable by no one else. So install UniGetUI with its
  installer package (so that the app belongs to root), and keep feeds out of shared folders such as
  `/Users/Shared`.
- a web feed that is not served over `https`, since any user could answer on a `localhost` port
  the real server left free.

When the helper is not running, or refuses a package (such as one from an unprotected feed), UniGetUI
falls back to the administrator prompt, which an administrator can still approve.

## Limits

- Only flat packages are read; legacy bundle packages (a `.pkg` that is a folder) are skipped.
- A `.dmg` must hold exactly one installer package or one app at its root (an uninstaller beside
  it is ignored); a `.zip` exactly one app.
- A product archive is identified by its first component. Without that component's receipt or the
  app it installs, the product shows as not installed, even if other components are present.
- An app is looked for only where the package installs it: an app renamed or moved elsewhere is
  not recognized.
- A `.pkg` receipt survives the user deleting the app by hand, so the app still shows as installed.
