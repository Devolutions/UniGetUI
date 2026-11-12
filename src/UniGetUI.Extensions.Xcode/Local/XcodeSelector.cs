namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>Points <c>xcode-select</c> at one verified Apple-signed Xcode, and checks it took.</summary>
internal sealed class XcodeSelector
{
    private readonly XcodeToolRunner _run;
    private readonly InstalledXcodeScanner _scanner;

    public XcodeSelector(XcodeToolRunner run)
    {
        _run = run;
        _scanner = new InstalledXcodeScanner(run);
    }

    public async Task SelectAsync(string appPath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(appPath))
            throw new XcodesException("The Xcode to select has no absolute path.");
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(appPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new XcodesException($"{appPath} is no longer an Xcode.", ex);
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new XcodesException($"{appPath} is a symbolic link and cannot be selected.");

        string app = InstalledXcodeScanner.Resolve(appPath);
        XcodeBundleInfo bundle = await XcodeBundle.ReadAsync(app, _run, cancellationToken).ConfigureAwait(false)
            ?? throw new XcodesException($"{appPath} is no longer an Xcode.");
        await VerifySelectableBundleAsync(app, cancellationToken).ConfigureAwait(false);
        if (await _scanner.GetSelectedAppAsync(cancellationToken).ConfigureAwait(false) == app)
            return;

        await XcodeAdministrator.RunAsync(_run, [ProtectedSelection(app, bundle.ShortVersion, bundle.Build)],
            $"UniGetUI wants to make {Path.GetFileName(app)} the active Xcode.", cancellationToken).ConfigureAwait(false);

        if (await _scanner.GetSelectedAppAsync(cancellationToken).ConfigureAwait(false) != app)
            throw new XcodesException($"xcode-select did not switch to {app}.");
    }

    private async Task VerifySelectableBundleAsync(string app, CancellationToken cancellationToken)
    {
        XcodesResult verified = await _run("/usr/bin/codesign", ["--verify", "--strict", "--verbose=1", app],
            cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            throw new XcodesException($"{app} failed signature verification: " + XcodesError.Summarize(verified.Output, verified.ExitCode));

        XcodesResult assessment = await _run("/usr/sbin/spctl", ["--assess", "--verbose", "--type", "execute", app],
            cancellationToken).ConfigureAwait(false);
        if (!assessment.Success)
            throw new XcodesException($"Gatekeeper rejected {app}: " + XcodesError.Summarize(assessment.Output, assessment.ExitCode));

        XcodesResult signature = await _run("/usr/bin/codesign", ["-vv", "-d", app], cancellationToken).ConfigureAwait(false);
        if (!signature.Success)
            throw new XcodesException($"The signature of {app} could not be read: " + XcodesError.Summarize(signature.Output, signature.ExitCode));
        if (!XcodeArchiveInstaller.IsAppleXcodeSignature(signature.StandardError + "\n" + signature.StandardOutput))
            throw new XcodesException($"{app} is not signed by Apple as Xcode.");

        string xcodebuild = Path.Combine(app, "Contents", "Developer", "usr", "bin", "xcodebuild");
        XcodesResult verifiedXcodebuild = await _run("/usr/bin/codesign", ["--verify", "--strict", "--verbose=1", xcodebuild],
            cancellationToken).ConfigureAwait(false);
        if (!verifiedXcodebuild.Success)
            throw new XcodesException($"{app} has an invalid xcodebuild signature: "
                                      + XcodesError.Summarize(verifiedXcodebuild.Output, verifiedXcodebuild.ExitCode));
    }

    internal static XcodeRootCommand ProtectedSelection(string destination, string version, string build)
        => new("/bin/sh",
        [
            "-c", "script=$1; shift; exec /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME=/var/root LC_ALL=C /bin/sh -c \"$script\" sh \"$@\"",
            "sh", ProtectedSelectionScript, destination, build.ToUpperInvariant(), version,
        ], LiteralScriptIndex: 3);

    internal const string ProtectedSelectionScript = """
        set -eu
        destination=$1
        expected_build=$2
        expected_version=$3
        umask 077
        fail() { /usr/bin/printf '%s\n' "$1" >&2; exit 1; }
        check_select_ancestors() {
            target=$1
            check_select_dir() {
                candidate=$1
                [ -d "$candidate" ] && [ ! -L "$candidate" ] || fail 'The Xcode path contains a link or inaccessible folder.'
                owner=$(/usr/bin/stat -f %u "$candidate") || fail 'Could not inspect an Xcode folder.'
                case "$owner" in ''|*[!0-9]*) fail 'Invalid Xcode folder owner.';; esac
                if [ "$owner" != 0 ]; then
                    case " $(/usr/bin/id -G "$owner" 2>/dev/null || true) " in
                        *' 80 '*|*' 0 '*) ;;
                        *) fail 'Cannot select an Xcode inside a folder owned by a non-administrator user.';;
                    esac
                fi
                mode=$(/usr/bin/stat -f %Lp "$candidate") || fail 'Could not inspect an Xcode folder.'
                case "$mode" in ''|*[!0-7]*) fail 'An Xcode folder has invalid permissions.';; esac
                [ "$((0$mode & 002))" -eq 0 ] || fail 'Cannot select an Xcode inside a world-writable folder.'
                if [ "$((0$mode & 020))" -ne 0 ]; then
                    group=$(/usr/bin/stat -f %g "$candidate") || fail 'Could not inspect an Xcode folder.'
                    [ "$group" = 0 ] || [ "$group" = 80 ] || fail 'Cannot select an Xcode inside a folder writable by a non-administrator group.'
                fi
                listing=$(/bin/ls -lde "$candidate") || fail 'Could not inspect an Xcode folder.'
                [ -z "$(/usr/bin/printf '%s\n' "$listing" | /usr/bin/sed -n '2p')" ] || fail 'Cannot select an Xcode inside a folder with an access control list.'
            }
            parent=${target%/*}
            check_select_dir /
            remaining=${parent#/}
            current=''
            while [ -n "$remaining" ]; do
                segment=${remaining%%/*}
                [ -n "$segment" ] && [ "$segment" != . ] && [ "$segment" != .. ] || fail 'The Xcode path is not normalized.'
                if [ "$remaining" = "$segment" ]; then remaining=''; else remaining=${remaining#*/}; fi
                current="$current/$segment"
                check_select_dir "$current"
            done
            [ -d "$target" ] && [ ! -L "$target" ] || fail 'The Xcode path contains a link or inaccessible folder.'
        }
        check_select_ancestors "$destination"
        /bin/chmod -RN "$destination" || fail 'Could not remove access control lists from the selected Xcode.'
        /usr/sbin/chown -R -P root:wheel "$destination" || fail 'Could not protect the selected Xcode.'
        /bin/chmod -R -P u+rwX,go+rX,go-w,u-s,g-s "$destination" || fail 'Could not protect the selected Xcode.'
        /bin/chmod 755 "$destination" || fail 'Could not make the selected Xcode accessible.'
        dest_listing=$(/bin/ls -lde "$destination") || fail 'Could not inspect the selected Xcode.'
        [ -z "$(/usr/bin/printf '%s\n' "$dest_listing" | /usr/bin/sed -n '2p')" ] || fail 'The selected Xcode still has an access control list.'
        verified_id=$(/usr/bin/stat -f '%d:%i' "$destination") || fail 'Could not inspect the selected Xcode.'
        /usr/bin/codesign --verify --strict --verbose=1 "$destination" || fail 'The selected Xcode failed signature verification.'
        /usr/sbin/spctl --assess --verbose --type execute "$destination" || fail 'Gatekeeper rejected the selected Xcode.'
        signature=$(/usr/bin/codesign -vv -d "$destination" 2>&1) || fail 'Could not read the selected Xcode signature.'
        identifier=$(/usr/bin/printf '%s\n' "$signature" | /usr/bin/sed -n 's/^Identifier=//p')
        team=$(/usr/bin/printf '%s\n' "$signature" | /usr/bin/sed -n 's/^TeamIdentifier=//p')
        authorities=$(/usr/bin/printf '%s\n' "$signature" | /usr/bin/sed -n 's/^Authority=//p')
        software_chain=$(/usr/bin/printf '%s\n' 'Software Signing' 'Apple Code Signing Certification Authority' 'Apple Root CA')
        app_store_chain=$(/usr/bin/printf '%s\n' 'Apple Mac OS Application Signing' 'Apple Worldwide Developer Relations Certification Authority' 'Apple Root CA')
        [ "$identifier" = 'com.apple.dt.Xcode' ] && [ "$team" = '59GAB85EFG' ] || fail 'The selected Xcode is not signed by Apple as Xcode.'
        [ "$authorities" = "$software_chain" ] || [ "$authorities" = "$app_store_chain" ] || fail 'The selected Xcode has an unrecognized signing chain.'
        actual_build=$(/usr/libexec/PlistBuddy -c 'Print :ProductBuildVersion' "$destination/Contents/version.plist") || fail 'The selected Xcode has no build number.'
        actual_build=$(/usr/bin/printf '%s' "$actual_build" | /usr/bin/tr '[:lower:]' '[:upper:]')
        actual_version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$destination/Contents/Info.plist") || fail 'The selected Xcode has no version.'
        [ "$actual_build" = "$expected_build" ] && [ "$actual_version" = "$expected_version" ] || fail 'The selected Xcode does not match the expected version and build.'
        /usr/bin/codesign --verify --strict --verbose=1 "$destination/Contents/Developer/usr/bin/xcodebuild" || fail 'The selected xcodebuild failed signature verification.'
        check_select_ancestors "$destination"
        [ "$(/usr/bin/stat -f '%d:%i' "$destination")" = "$verified_id" ] || fail 'The selected Xcode changed during verification.'
        previous_selected=$(/usr/bin/xcode-select -p 2>/dev/null || true)
        /usr/bin/xcode-select -s "$destination"
        if [ ! -d "$destination" ] || [ -L "$destination" ] || [ "$(/usr/bin/stat -f '%d:%i' "$destination")" != "$verified_id" ]; then
            if [ -n "$previous_selected" ]; then /usr/bin/xcode-select -s "$previous_selected" >/dev/null 2>&1 || true; fi
            fail 'The selected Xcode changed while being selected.'
        fi
        """;
}
