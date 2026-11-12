// Portions adapted from XcodesKit (https://github.com/XcodesOrg/XcodesApp), MIT License.
// See THIRD_PARTY_NOTICES.md.

using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>
/// Turns a downloaded Xcode archive into an installed Xcode. Privileged setup uses a separate root-protected copy,
/// verified again there, and an Xcode already in the installation directory is never replaced.
/// </summary>
internal sealed class XcodeArchiveInstaller(XcodeToolRunner quick, XcodeToolRunner slow)
{
    internal const string XcodeTeam = "59GAB85EFG";

    /// <summary>The chain Apple signs Xcode archives with, and the one the App Store copy carries.</summary>
    internal static readonly IReadOnlyList<IReadOnlyList<string>> AppleChains =
    [
        ["Software Signing", "Apple Code Signing Certification Authority", "Apple Root CA"],
        ["Apple Mac OS Application Signing", "Apple Worldwide Developer Relations Certification Authority", "Apple Root CA"],
    ];

    internal const string AdministratorPrompt =
        "UniGetUI needs administrator rights to finish installing Xcode. Once approved, this step cannot be canceled.";

    /// <summary>The version, build and archive architecture being installed, as the catalog describes them.</summary>
    internal sealed record Target(string Version, string? Build, XcodeArchitecture Architecture = XcodeArchitecture.Unspecified);

    /// <summary>The installation steps after the download, numbered the way the page shows them.</summary>
    internal const int StepCount = 6;

    /// <summary>
    /// Installs the Xcode in <paramref name="archive"/> at the preflighted <paramref name="destination"/> and returns its identity.
    /// <paramref name="step"/> receives each step's number and description.
    /// </summary>
    public async Task<XcodeInstallationResult> InstallAsync(string archive, Target target, XcodeInstallOptions options,
        string installDirectory, string destination, Action<int, string> step, CancellationToken cancellationToken)
    {
        if (target.Build is not { } expectedBuild || !XcodeSimulatorImageFile.IsValidBuild(expectedBuild))
            throw new XcodesException("The exact Xcode build is unknown. Refresh the catalog before installing.");
        RefuseExistingDestination(destination);

        string staging = CreateStaging(Path.GetDirectoryName(archive)!);
        try
        {
            step(2, "Unarchiving Xcode (This can take a while)");
            string app = await ExpandAsync(archive, staging, cancellationToken).ConfigureAwait(false);

            step(3, "Checking security assessment and code signing");
            XcodeBundleInfo bundle = await VerifyAsync(app, target, cancellationToken).ConfigureAwait(false);
            uint? requestingUser = options.SkipAdministratorSteps
                ? null : await CurrentUserAsync(cancellationToken).ConfigureAwait(false);

            step(4, $"Installing Xcode to {destination}");
            bool copiedAsRoot = !await TryCopyAsync(app, destination, cancellationToken).ConfigureAwait(false);
            if (copiedAsRoot && options.SkipAdministratorSteps)
                throw new XcodesException(
                    $"UniGetUI cannot write to {installDirectory} without administrator rights. Allow the administrator steps in Settings, or choose another folder.");

            if (!copiedAsRoot)
                await VerifyAsync(destination, target, cancellationToken).ConfigureAwait(false);

            step(5, options.SkipAdministratorSteps
                ? "Finishing installation"
                : "Finishing installation (administrator rights needed; this step cannot be canceled once started)");
            if (!options.SkipAdministratorSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                XcodeRootCommand command = ProtectedInstallation(
                    app, destination, expectedBuild, bundle.ShortVersion, copiedAsRoot, options.Select, requestingUser!.Value);
                // A root process cannot reliably be stopped by this user once the prompt has been accepted.
                await XcodeAdministrator.RunAsync(slow, [command], AdministratorPrompt, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            if (!Directory.Exists(destination))
                throw new XcodesException($"Xcode could not be moved to {destination}.");

            CancellationToken finishing = options.SkipAdministratorSteps ? cancellationToken : CancellationToken.None;
            XcodeBundleInfo installed = await VerifyAsync(destination, target, finishing).ConfigureAwait(false);
            await MarkComponentsCheckedAsync(destination, finishing).ConfigureAwait(false);

            step(6, options.DeleteArchive ? $"Deleting Xcode archive {Path.GetFileName(archive)}" : "Keeping the Xcode archive for a later reinstall");
            if (options.DeleteArchive)
                TryDelete(archive);
            return new XcodeInstallationResult(destination, installed.Build, installed.Architecture);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>The name of the installed app, such as <c>Xcode-26.4.app</c> or <c>Xcode-26.5-Beta-2.app</c>.</summary>
    internal static string DestinationName(string version)
    {
        if (!XcodeVersion.TryParse(version, out _))
            throw new ArgumentException($"'{version}' is not an Xcode version.", nameof(version));
        string name = string.Join('-', version.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return $"Xcode-{name}.app";
    }

    /// <summary>Chooses one destination before authentication and downloading; existing paths are never replaced.</summary>
    internal async Task<string> FindDestinationAsync(string directory, Target target, CancellationToken cancellationToken)
    {
        if (target.Build is not { } build || !XcodeSimulatorImageFile.IsValidBuild(build))
            throw new XcodesException("The exact Xcode build is unknown. Refresh the catalog before installing.");

        string name = DestinationName(target.Version);
        string destination = Path.Combine(directory, name);
        if (ExistingAttributes(destination) is not { } attributes)
            return destination;

        XcodeBundleInfo? existing = await ExistingBundleAsync(destination, attributes, cancellationToken).ConfigureAwait(false);
        if (existing is null || !XcodeVersion.SameProductVersion(existing.ShortVersion, target.Version)
            || !DifferentIdentity(existing, build, target.Architecture))
            throw ExistingDestination(destination);

        string buildName = $"{name[..^4]}-{build.ToUpperInvariant()}";
        string buildDestination = Path.Combine(directory, $"{buildName}.app");
        if (ExistingAttributes(buildDestination) is not { } buildAttributes)
            return buildDestination;

        XcodeBundleInfo? existingBuild = await ExistingBundleAsync(buildDestination, buildAttributes, cancellationToken).ConfigureAwait(false);
        if (existingBuild is null || !XcodeVersion.SameProductVersion(existingBuild.ShortVersion, target.Version)
            || !string.Equals(existingBuild.Build, build, StringComparison.OrdinalIgnoreCase)
            || !DifferentArchitecture(existingBuild.Architecture, target.Architecture))
            throw ExistingDestination(buildDestination);

        string architecture = target.Architecture switch
        {
            XcodeArchitecture.AppleSilicon => "arm64",
            XcodeArchitecture.Intel => "x86_64",
            XcodeArchitecture.Universal => "universal",
            _ => throw new XcodesException("The exact Xcode architecture is unknown."),
        };
        string architectureDestination = Path.Combine(directory, $"{buildName}-{architecture}.app");
        RefuseExistingDestination(architectureDestination);
        return architectureDestination;
    }

    private async Task<XcodeBundleInfo?> ExistingBundleAsync(string path, FileAttributes attributes, CancellationToken cancellationToken)
        => (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory
            ? await XcodeBundle.ReadAsync(path, quick, cancellationToken).ConfigureAwait(false) : null;

    private static bool DifferentIdentity(XcodeBundleInfo existing, string build, XcodeArchitecture architecture)
        => !string.Equals(existing.Build, build, StringComparison.OrdinalIgnoreCase)
           || DifferentArchitecture(existing.Architecture, architecture);

    private static bool DifferentArchitecture(XcodeArchitecture existing, XcodeArchitecture requested)
        => existing != XcodeArchitecture.Unspecified && requested != XcodeArchitecture.Unspecified && existing != requested;

    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void RefuseExistingDestination(string path)
    {
        if (ExistingAttributes(path) is not null)
            throw ExistingDestination(path);
    }

    private static XcodesException ExistingDestination(string path)
        => new($"{path} already exists. Move or rename it, then try again.");

    private async Task<uint> CurrentUserAsync(CancellationToken cancellationToken)
    {
        XcodesResult result = await quick("/usr/bin/id", ["-u"], cancellationToken).ConfigureAwait(false);
        if (!result.Success || !uint.TryParse(result.StandardOutput.Trim(),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint user))
            throw new XcodesException("Could not identify the user requesting Xcode installation.");
        return user;
    }

    internal static XcodeRootCommand CopyWithoutReplacement(string app, string destination)
        => new("/bin/sh",
        [
            "-c", "/bin/mkdir \"$2\" || { if [ -e \"$2\" ] || [ -L \"$2\" ]; then exit 73; fi; if [ ! -w \"$3\" ]; then exit 77; fi; exit 74; }; /usr/bin/ditto \"$1\" \"$2\"",
            "sh", app, destination, Path.GetDirectoryName(destination)!,
        ]);

    /// <summary>The only elevated code is this fixed system-shell script, not the user-writable UniGetUI executable.</summary>
    internal static XcodeRootCommand ProtectedInstallation(
        string app, string destination, string build, string version, bool copy, bool select, uint requestingUser)
        => new("/bin/sh",
        [
            "-c", "script=$1; shift; exec /usr/bin/env -i PATH=/usr/bin:/bin:/usr/sbin:/sbin HOME=/var/root LC_ALL=C /bin/sh -c \"$script\" sh \"$@\"",
            "sh", ProtectedInstallationScript, app, destination, build.ToUpperInvariant(), version,
            copy ? "1" : "0", select ? "1" : "0",
            requestingUser.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ], LiteralScriptIndex: 3);

    // The destination is only written as root if the requesting user cannot replace any of its ancestors.
    // codesign --strict also rejects symlinks out of the signed bundle before any of its code runs as root.
    internal const string ProtectedInstallationScript = """
        set -eu
        source=$1
        destination=$2
        expected_build=$3
        expected_version=$4
        copy_as_root=$5
        select_xcode=$6
        requesting_user=$7
        umask 077
        fail() { /usr/bin/printf '%s\n' "$1" >&2; exit 1; }
        case "$requesting_user" in ''|*[!0-9]*) fail 'Invalid requesting user.';; esac
        for directory in / /private /private/var /private/var/root; do
            [ -d "$directory" ] && [ ! -L "$directory" ] || fail 'The root staging path is not a protected folder.'
            [ "$(/usr/bin/stat -f %u "$directory")" = 0 ] || fail 'The root staging path is not owned by root.'
            mode=$(/usr/bin/stat -f %Lp "$directory") || fail 'Could not inspect the root staging path.'
            case "$mode" in ''|*[!0-7]*) fail 'The root staging path has invalid permissions.';; esac
            [ "$((0$mode & 022))" -eq 0 ] || fail 'The root staging path is writable by other users.'
            listing=$(/bin/ls -lde "$directory") || fail 'Could not inspect the root staging path.'
            [ -z "$(/usr/bin/printf '%s\n' "$listing" | /usr/bin/sed -n '2p')" ] || fail 'The root staging path has an access control list.'
        done
        stage=$(/usr/bin/mktemp -d /private/var/root/unigetui-xcode.XXXXXXXX) || fail 'Could not create a protected Xcode staging folder.'
        claimed=0
        copied=0
        cleanup() {
            result=$?
            trap - EXIT
            if [ "$claimed" = 1 ] && [ "$copied" = 0 ]; then
                if ! /bin/rm -rf -- "$destination"; then
                    /usr/bin/printf '%s\n' 'Could not remove the incomplete Xcode installation.' >&2
                    result=1
                fi
            fi
            if ! /bin/rm -rf -- "$stage"; then
                /usr/bin/printf '%s\n' 'Could not remove the protected Xcode staging folder.' >&2
                result=1
            fi
            exit "$result"
        }
        trap cleanup EXIT
        trap 'exit 1' HUP INT TERM
        /bin/chmod 700 "$stage" || fail 'Could not protect the Xcode staging folder.'
        /bin/mkdir -m 700 "$stage/tmp" || fail 'Could not create a private temporary folder.'
        TMPDIR="$stage/tmp"
        export TMPDIR
        staged="$stage/Xcode.app"
        /usr/bin/ditto --noacl "$source" "$staged" || fail 'Could not copy Xcode into protected staging.'
        /usr/sbin/chown -R -P root:wheel "$staged" || fail 'Could not protect the staged Xcode.'
        /bin/chmod -R -P u+rwX,go+rX,go-w,u-s,g-s "$staged" || fail 'Could not protect the staged Xcode.'
        verify_xcode() {
            bundle=$1
            label=$2
            /usr/bin/codesign --verify --deep --strict --verbose=1 "$bundle" || fail "The $label Xcode failed signature verification."
            /usr/sbin/spctl --assess --verbose --type execute "$bundle" || fail "Gatekeeper rejected the $label Xcode."
            signature=$(/usr/bin/codesign -vv -d "$bundle" 2>&1) || fail "Could not read the $label Xcode signature."
            identifier=$(/usr/bin/printf '%s\n' "$signature" | /usr/bin/sed -n 's/^Identifier=//p')
            team=$(/usr/bin/printf '%s\n' "$signature" | /usr/bin/sed -n 's/^TeamIdentifier=//p')
            authorities=$(/usr/bin/printf '%s\n' "$signature" | /usr/bin/sed -n 's/^Authority=//p')
            software_chain=$(/usr/bin/printf '%s\n' 'Software Signing' 'Apple Code Signing Certification Authority' 'Apple Root CA')
            app_store_chain=$(/usr/bin/printf '%s\n' 'Apple Mac OS Application Signing' 'Apple Worldwide Developer Relations Certification Authority' 'Apple Root CA')
            [ "$identifier" = 'com.apple.dt.Xcode' ] && [ "$team" = '59GAB85EFG' ] || fail "The $label Xcode is not signed by Apple as Xcode."
            [ "$authorities" = "$software_chain" ] || [ "$authorities" = "$app_store_chain" ] || fail "The $label Xcode has an unrecognized signing chain."
            actual_build=$(/usr/libexec/PlistBuddy -c 'Print :ProductBuildVersion' "$bundle/Contents/version.plist") || fail "The $label Xcode has no build number."
            actual_build=$(/usr/bin/printf '%s' "$actual_build" | /usr/bin/tr '[:lower:]' '[:upper:]')
            actual_version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$bundle/Contents/Info.plist") || fail "The $label Xcode has no version."
            [ "$actual_build" = "$expected_build" ] && [ "$actual_version" = "$expected_version" ] || fail "The $label Xcode does not match the requested version and build."
            /usr/bin/codesign --verify --strict --verbose=1 "$bundle/Contents/Developer/usr/bin/xcodebuild" || fail "The $label xcodebuild failed signature verification."
        }
        check_select_ancestors() {
            target=$1
            check_select_dir() {
                candidate=$1
                [ -d "$candidate" ] && [ ! -L "$candidate" ] || fail 'The installation path contains a link or inaccessible folder.'
                owner=$(/usr/bin/stat -f %u "$candidate") || fail 'Could not inspect an installation folder.'
                case "$owner" in ''|*[!0-9]*) fail 'Invalid installation folder owner.';; esac
                if [ "$owner" != 0 ]; then
                    case " $(/usr/bin/id -G "$owner" 2>/dev/null || true) " in
                        *' 80 '*|*' 0 '*) ;;
                        *) fail 'Cannot select an Xcode inside a folder owned by a non-administrator user.';;
                    esac
                fi
                mode=$(/usr/bin/stat -f %Lp "$candidate") || fail 'Could not inspect an installation folder.'
                case "$mode" in ''|*[!0-7]*) fail 'An installation folder has invalid permissions.';; esac
                [ "$((0$mode & 002))" -eq 0 ] || fail 'Cannot select an Xcode inside a world-writable folder.'
                if [ "$((0$mode & 020))" -ne 0 ]; then
                    group=$(/usr/bin/stat -f %g "$candidate") || fail 'Could not inspect an installation folder.'
                    [ "$group" = 0 ] || [ "$group" = 80 ] || fail 'Cannot select an Xcode inside a folder writable by a non-administrator group.'
                fi
                listing=$(/bin/ls -lde "$candidate") || fail 'Could not inspect an installation folder.'
                [ -z "$(/usr/bin/printf '%s\n' "$listing" | /usr/bin/sed -n '2p')" ] || fail 'An installation folder has an access control list.'
            }
            parent=${target%/*}
            check_select_dir /
            remaining=${parent#/}
            current=''
            while [ -n "$remaining" ]; do
                segment=${remaining%%/*}
                [ -n "$segment" ] && [ "$segment" != . ] && [ "$segment" != .. ] || fail 'The installation path is not normalized.'
                if [ "$remaining" = "$segment" ]; then remaining=''; else remaining=${remaining#*/}; fi
                current="$current/$segment"
                check_select_dir "$current"
            done
            [ -d "$target" ] && [ ! -L "$target" ] || fail 'The installation path contains a link or inaccessible folder.'
        }
        verify_xcode "$staged" staged
        xcodebuild="$staged/Contents/Developer/usr/bin/xcodebuild"
        if [ "$copy_as_root" = 1 ]; then
            [ "$requesting_user" != 0 ] || fail 'Cannot safely install into this folder as root.'
            root_device=$(/usr/bin/stat -f %d /) || fail 'Could not inspect the system volume.'
            check_parent() {
                candidate=$1
                [ -d "$candidate" ] && [ ! -L "$candidate" ] || fail 'The installation path contains a link or inaccessible folder.'
                [ "$(/usr/bin/stat -f %u "$candidate")" = 0 ] || fail 'An installation folder is not owned by root.'
                [ "$(/usr/bin/stat -f %d "$candidate")" = "$root_device" ] || fail 'Administrator installation is only supported on the system volume.'
                listing=$(/bin/ls -lde "$candidate") || fail 'Could not inspect an installation folder.'
                [ -z "$(/usr/bin/printf '%s\n' "$listing" | /usr/bin/sed -n '2p')" ] || fail 'An installation folder has an access control list.'
                if /usr/bin/sudo -n -u "#$requesting_user" /bin/sh -c 'if [ -w "$1" ]; then exit 10; else exit 20; fi' sh "$candidate" >/dev/null 2>&1; then
                    access=0
                else
                    access=$?
                fi
                [ "$access" = 20 ] || fail 'The requesting user can change an installation folder, or its access could not be checked.'
            }
            parent=${destination%/*}
            check_parent /
            remaining=${parent#/}
            current=''
            while [ -n "$remaining" ]; do
                segment=${remaining%%/*}
                [ -n "$segment" ] && [ "$segment" != . ] && [ "$segment" != .. ] || fail 'The installation path is not normalized.'
                if [ "$remaining" = "$segment" ]; then remaining=''; else remaining=${remaining#*/}; fi
                current="$current/$segment"
                check_parent "$current"
            done
            /bin/mkdir -m 700 "$destination" || fail 'The Xcode destination already exists or could not be created.'
            claimed=1
            /usr/bin/ditto --noacl "$staged" "$destination" || fail 'Could not copy Xcode to its installation folder.'
            /bin/chmod 755 "$destination" || fail 'Could not make the installed Xcode accessible.'
            copied=1
        fi
        DEVELOPER_DIR="$staged/Contents/Developer"
        export DEVELOPER_DIR
        /usr/sbin/DevToolsSecurity -enable
        /usr/sbin/dseditgroup -o edit -t group -a staff _developer
        "$xcodebuild" -license accept
        "$xcodebuild" -runFirstLaunch
        if [ "$select_xcode" = 1 ]; then
            if [ "$copy_as_root" = 0 ]; then
                check_select_ancestors "$destination"
                /bin/chmod -RN "$destination" || fail 'Could not remove access control lists from the installed Xcode.'
                /usr/sbin/chown -R -P root:wheel "$destination" || fail 'Could not protect the installed Xcode.'
                /bin/chmod -R -P u+rwX,go+rX,go-w,u-s,g-s "$destination" || fail 'Could not protect the installed Xcode.'
                /bin/chmod 755 "$destination" || fail 'Could not make the installed Xcode accessible.'
                dest_listing=$(/bin/ls -lde "$destination") || fail 'Could not inspect the installed Xcode.'
                [ -z "$(/usr/bin/printf '%s\n' "$dest_listing" | /usr/bin/sed -n '2p')" ] || fail 'The installed Xcode still has an access control list.'
                verified_id=$(/usr/bin/stat -f '%d:%i' "$destination") || fail 'Could not inspect the installed Xcode.'
                verify_xcode "$destination" installed
                check_select_ancestors "$destination"
                [ "$(/usr/bin/stat -f '%d:%i' "$destination")" = "$verified_id" ] || fail 'The installed Xcode changed during verification.'
                previous_selected=$(/usr/bin/xcode-select -p 2>/dev/null || true)
                /usr/bin/xcode-select -s "$destination"
                if [ ! -d "$destination" ] || [ -L "$destination" ] || [ "$(/usr/bin/stat -f '%d:%i' "$destination")" != "$verified_id" ]; then
                    if [ -n "$previous_selected" ]; then /usr/bin/xcode-select -s "$previous_selected" >/dev/null 2>&1 || true; fi
                    fail 'The installed Xcode changed while being selected.'
                fi
            else
                /usr/bin/xcode-select -s "$destination"
            fi
        fi
        """;

    private static string CreateStaging(string parent)
    {
        string staging = Path.Combine(parent, "Expanding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return staging;
    }

    /// <summary>Expands the archive into <paramref name="staging"/> and returns the one Xcode it held.</summary>
    private async Task<string> ExpandAsync(string archive, string staging, CancellationToken cancellationToken)
    {
        // Both tools expand into the working directory, which the runner does not set; a shell sets it.
        bool encrypted = archive.EndsWith(".aea", StringComparison.OrdinalIgnoreCase);
        string[] expand = encrypted
            ? ["/usr/bin/archiveutil", "extractXcodeAEA", archive]
            : ["/usr/bin/xip", "--expand", archive];
        if (encrypted && !File.Exists("/usr/bin/archiveutil"))
            throw new XcodesException("This Xcode comes as an encrypted archive, which needs a newer macOS to expand.");

        XcodesResult result = await slow("/bin/sh", ["-c", "cd -- \"$1\" && shift && exec \"$@\"", "sh", staging, .. expand],
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            string output = result.Output;
            if (output.Contains("damaged", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(archive);
                throw new XcodesException("The downloaded archive is damaged and was deleted. Try the installation again.");
            }
            if (output.Contains("enough free space", StringComparison.OrdinalIgnoreCase))
                throw new XcodesException("There is not enough free space to expand Xcode. Free some space, then try again; the download is kept.");
            throw new XcodesException("Xcode could not be expanded: " + XcodesError.Summarize(output, result.ExitCode));
        }

        string[] apps = Directory.EnumerateDirectories(staging, "*.app")
            .Where(path => !new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            .ToArray();
        if (apps.Length != 1)
            throw new XcodesException("The archive did not contain exactly one Xcode.");
        return apps[0];
    }

    /// <summary>Requires Gatekeeper's approval, Apple's Xcode signature, and the version and build that were asked for.</summary>
    internal async Task<XcodeBundleInfo> VerifyAsync(string app, Target target, CancellationToken cancellationToken)
    {
        XcodeBundleInfo bundle = await XcodeBundle.ReadAsync(app, quick, cancellationToken).ConfigureAwait(false)
            ?? throw new XcodesException("The archive did not contain Xcode.");
        if (target.Build is { } build && !string.Equals(bundle.Build, build, StringComparison.OrdinalIgnoreCase))
            throw new XcodesException($"The archive holds Xcode build {bundle.Build}, not the expected {build}.");
        if (XcodeVersion.TryParse(target.Version, out XcodeVersion expected)
            && XcodeVersion.TryParse(bundle.ShortVersion, out XcodeVersion actual)
            && (expected.Major, expected.Minor, expected.Patch) != (actual.Major, actual.Minor, actual.Patch))
            throw new XcodesException($"The archive holds Xcode {bundle.ShortVersion}, not the expected {target.Version}.");

        XcodesResult verified = await quick("/usr/bin/codesign", ["--verify", "--deep", "--strict", "--verbose=1", app],
            cancellationToken).ConfigureAwait(false);
        if (!verified.Success)
            throw new XcodesException("The downloaded Xcode failed signature verification: "
                                     + XcodesError.Summarize(verified.Output, verified.ExitCode));

        XcodesResult assessment = await quick("/usr/sbin/spctl", ["--assess", "--verbose", "--type", "execute", app],
            cancellationToken).ConfigureAwait(false);
        if (!assessment.Success)
            throw new XcodesException("Gatekeeper rejected the downloaded Xcode: " + XcodesError.Summarize(assessment.Output, assessment.ExitCode));

        XcodesResult signature = await quick("/usr/bin/codesign", ["-vv", "-d", app], cancellationToken).ConfigureAwait(false);
        if (!signature.Success)
            throw new XcodesException("The downloaded Xcode's signature could not be read: " + XcodesError.Summarize(signature.Output, signature.ExitCode));
        if (!IsAppleXcodeSignature(signature.StandardError + "\n" + signature.StandardOutput))
            throw new XcodesException("The downloaded Xcode is not signed by Apple as Xcode. It was not installed.");
        return bundle;
    }

    /// <summary>Whether <c>codesign -d</c>'s description names Xcode, Apple's team, and one of Apple's chains, in order.</summary>
    internal static bool IsAppleXcodeSignature(string description)
    {
        List<string> authorities = [];
        string? team = null, identifier = null;
        foreach (string line in description.Split('\n'))
        {
            string[] parts = line.Trim().Split('=', 2);
            if (parts.Length != 2) continue;
            switch (parts[0])
            {
                case "Authority": authorities.Add(parts[1]); break;
                case "TeamIdentifier": team = parts[1]; break;
                case "Identifier": identifier = parts[1]; break;
            }
        }
        return team == XcodeTeam && identifier == "com.apple.dt.Xcode"
               && AppleChains.Any(chain => chain.SequenceEqual(authorities));
    }

    /// <summary>Copies the app as this user. False when this user may not create the destination.</summary>
    private async Task<bool> TryCopyAsync(string app, string destination, CancellationToken cancellationToken)
    {
        // Claim the destination atomically before copying: mv -n can instead move inside an existing directory.
        XcodeRootCommand copy = CopyWithoutReplacement(app, destination);
        XcodesResult copied = await slow(copy.Executable, copy.Arguments, cancellationToken).ConfigureAwait(false);
        if (copied.Success && Directory.Exists(destination))
            return true;
        if (!Path.Exists(destination) && copied.ExitCode == 77)
            return false;
        throw new XcodesException($"Xcode could not be copied to {destination}. Check the destination before trying again: "
                                 + XcodesError.Summarize(copied.Output, copied.ExitCode));
    }

    /// <summary>
    /// Records, as Xcode itself does, that this Mac already has the components for this build, so its first launch
    /// does not offer to install them again. Failing to is harmless.
    /// </summary>
    private async Task MarkComponentsCheckedAsync(string app, CancellationToken cancellationToken)
    {
        try
        {
            XcodeBundleInfo? bundle = await XcodeBundle.ReadAsync(app, quick, cancellationToken).ConfigureAwait(false);
            XcodesResult cache = await quick("/usr/bin/getconf", ["DARWIN_USER_CACHE_DIR"], cancellationToken).ConfigureAwait(false);
            XcodesResult macOS = await quick("/usr/bin/sw_vers", ["-buildVersion"], cancellationToken).ConfigureAwait(false);
            string directory = cache.StandardOutput.Trim();
            string build = macOS.StandardOutput.Trim();
            if (bundle is null || !cache.Success || !macOS.Success || !Path.IsPathFullyQualified(directory)
                || build.Length == 0 || build.Any(c => !char.IsAsciiLetterOrDigit(c)))
                return;
            string marker = Path.Combine(directory, $"com.apple.dt.Xcode.InstallCheckCache_{build}_{bundle.Build}");
            File.WriteAllBytes(marker, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XcodesException)
        {
            Logger.Warn($"Xcode: could not record the installed components: {ex.Message}");
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Xcode: could not delete {file}: {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Xcode: could not delete {directory}: {ex.Message}");
        }
    }
}
