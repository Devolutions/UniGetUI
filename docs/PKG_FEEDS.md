# Publishing macOS apps (.pkg and .dmg)

The software catalog can offer apps that no package manager carries, the way Jamf Self Service
does: an administrator publishes the vendor's `.pkg` or `.dmg` unchanged, and users install and
update it from the **Software Catalog** page.

There is nothing to repackage and no detection rule to write. UniGetUI reads each file's
identifier, version and app from the file itself, then checks the Mac:

- **`.pkg`:** installed when macOS holds its receipt (`pkgutil --pkgs`), at the receipt's version.
  Without a receipt, the app the package installs counts too when it is in place under its bundle
  id, so an app that Jamf or the user installed another way is recognized.
- **`.dmg`:** a disk image with one app at its root (the usual "drag to Applications" image).
  Installed when that app is in `/Applications` under its bundle id, at its bundle version.

## Set up a feed once

A feed is a location holding `.pkg` and `.dmg` files. Declare it as the `pkgFeed` of a catalog in the
machine-wide catalog file, `/Library/Application Support/Devolutions/UniGetUI/SoftwareCatalog.json`:

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

A feed can be:

| Location | Example | What UniGetUI reads |
|---|---|---|
| A web folder (https) | `https://software.example.com/mac/` | `index.json` in that folder |
| A local folder or a mounted share | `/Volumes/Software/mac` | the `.pkg` and `.dmg` files directly |

Plain `http` is refused (except on `localhost`), because the index is what pins each package's hash.

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

- **Install** and **Update** download the file (or copy it from the share) and check its hash and
  identity. Behind the standard macOS administrator prompt, a `.pkg` then runs through
  `installer`, and a `.dmg`'s app is copied to `/Applications`, replacing the previous version.
- Packages cannot be uninstalled from UniGetUI, as with Jamf Self Service.
- When the feed is unreachable (off the VPN, share not mounted), UniGetUI keeps showing the
  packages it last listed. Installing needs the feed again.

## Limits

- Only flat packages are read; legacy bundle packages (a `.pkg` that is a folder) are skipped.
- A `.dmg` must hold exactly one app at its root (an uninstaller app beside it is ignored). An
  image that holds a `.pkg` instead is not supported.
- A product archive is identified by its first component. Without that component's receipt or the
  app it installs, the product shows as not installed, even if other components are present.
- An app is looked for only where the package installs it: an app renamed or moved elsewhere is
  not recognized.
- A `.pkg` receipt survives the user deleting the app by hand, so the app still shows as installed.
