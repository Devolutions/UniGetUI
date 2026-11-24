namespace UniGetUI.Core.Tools;

public enum MacUpdateTargetStatus
{
    Ready,
    BundleNotFound,
    TranslocatedUnresolved,
    ReadOnlyVolume,
    NotWritable,
}

public sealed record MacUpdateTarget(MacUpdateTargetStatus Status, string BundlePath, string? RunningBundlePath);

public static class MacUpdateTargetResolver
{
    public const string DefaultInstallPath = "/Applications/UniGetUI.app";

    // Arguments are passed positionally ($1=pid, $2=target, $3=new app, $4=update log,
    // $5=failure marker, $6=redacted target) so no path is ever interpolated into the script text.
    // A leftover .old this user cannot delete (one owned by root) gets a unique backup name instead.
    public const string SwapScript = """
        pid="$1"; target="$2"; newapp="$3"; log="$4"; marker="$5"; shown="$6"
        reason() { printf 'Update swap failed while %s: %s\n' "$1" "${2##*: }" >> "$log"; }
        i=0
        while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 150 ]; do sleep 0.2; i=$((i+1)); done
        backup="$target.old"
        rm -rf "$backup" 2>/dev/null
        if [ -e "$backup" ] || [ -L "$backup" ]; then
          printf 'Update swap could not delete %s.old, so the previous version is set aside under another name\n' "$shown" >> "$log"
          backup="$(mktemp -d "$target.old.XXXXXX")" && rmdir "$backup" || backup=""
        fi
        run="$target"
        if [ -z "$backup" ]; then
          reason "choosing where to set the installed version aside" "no unused name was available"
          printf '\n%s %s\n' "$marker" "$shown" >> "$log"
        elif err=$(LC_ALL=C mv "$target" "$backup" 2>&1); then
          if err=$(LC_ALL=C mv "$newapp" "$target" 2>&1); then
            xattr -dr com.apple.quarantine "$target" 2>/dev/null
            rm -rf "$backup"
          else
            reason "moving the new version into place" "$err"
            rm -rf "$target"
            if ! err=$(LC_ALL=C mv "$backup" "$target" 2>&1); then
              reason "restoring the previous version, which was left in $shown${backup#"$target"}" "$err"
              run="$backup"
            fi
            printf '\n%s %s\n' "$marker" "$shown" >> "$log"
          fi
        else
          reason "moving the installed version aside" "$err"
          printf '\n%s %s\n' "$marker" "$shown" >> "$log"
        fi
        # A backup has no .app name, so LaunchServices would not open it: start its executable instead.
        if [ "$run" = "$target" ]; then /usr/bin/open -na "$target"; else "$run/Contents/MacOS/UniGetUI" >/dev/null 2>&1 & fi
        """;

    // Runs as root for a bundle the user cannot replace ($1=target, $2=archive, $3=SHA-256, $4=pid,
    // $5=code signing requirement). Installs only a root-owned, re-hashed and re-verified copy.
    public const string ElevatedSwapScript = """
        set -e
        target="$1"; archive="$2"; expected="$3"; pid="$4"; requirement="$5"
        work="$(/usr/bin/mktemp -d /private/tmp/unigetui-update.XXXXXX)"
        trap 'rm -rf "$work"' EXIT
        /bin/cp "$archive" "$work/update.tar.gz"
        actual="$(/usr/bin/shasum -a 256 "$work/update.tar.gz" | /usr/bin/cut -d ' ' -f 1)"
        if [ "$actual" != "$expected" ]; then
          echo "The update archive does not match its expected hash." >&2
          exit 1
        fi
        /bin/mkdir "$work/x"
        /usr/bin/tar -xzf "$work/update.tar.gz" -C "$work/x"
        newapp="$work/x/UniGetUI.app"
        if [ -L "$newapp" ] || [ ! -d "$newapp" ]; then
          echo "The update archive does not contain UniGetUI.app." >&2
          exit 1
        fi
        if [ -z "$requirement" ] || ! /usr/bin/codesign --verify --deep --strict -R="$requirement" "$newapp"; then
          echo "The update is not signed by a trusted developer." >&2
          exit 1
        fi
        rm -rf "$target.old"
        /usr/sbin/chown -R "$(/usr/bin/stat -f '%u:%g' "$target")" "$newapp"
        /usr/bin/xattr -dr com.apple.quarantine "$newapp" 2>/dev/null || true
        mv "$target" "$target.old"
        if ! mv "$newapp" "$target"; then
          rm -rf "$target"
          mv "$target.old" "$target"
          exit 1
        fi
        (i=0
         while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 150 ]; do sleep 0.2; i=$((i+1)); done
         rm -rf "$target.old") >/dev/null 2>&1 &
        """;

    public static string? FindEnclosingAppBundle(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return null;
        }

        string dir = executablePath;
        while (true)
        {
            int slash = dir.LastIndexOf('/');
            if (slash <= 0)
            {
                return null;
            }

            dir = dir[..slash];
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }
        }
    }

    public static bool IsTranslocatedPath(string path) =>
        path.Contains("/AppTranslocation/", StringComparison.Ordinal);

    public static MacUpdateTarget Resolve(
        string? executablePath,
        Func<string, string?> resolveTranslocatedOriginal,
        Func<string, bool> directoryExists,
        Func<string, bool> isWritable,
        Func<string, bool> isOnReadOnlyVolume)
    {
        string? running = FindEnclosingAppBundle(executablePath);
        string target = running ?? DefaultInstallPath;

        if (running is not null && IsTranslocatedPath(running))
        {
            string? original = resolveTranslocatedOriginal(running);
            if (original is null || IsTranslocatedPath(original) || !directoryExists(original))
            {
                original = directoryExists(DefaultInstallPath) ? DefaultInstallPath : null;
            }

            if (original is null)
            {
                return new MacUpdateTarget(MacUpdateTargetStatus.TranslocatedUnresolved, running, running);
            }

            target = original;
        }

        if (!directoryExists(target))
        {
            return new MacUpdateTarget(MacUpdateTargetStatus.BundleNotFound, target, running);
        }

        // Not even root can replace a bundle on a mounted disk image, so asking for a password is pointless.
        if (isOnReadOnlyVolume(target))
        {
            return new MacUpdateTarget(MacUpdateTargetStatus.ReadOnlyVolume, target, running);
        }

        if (!SelfUpdateSwap.CanReplace(target, isWritable))
        {
            return new MacUpdateTarget(MacUpdateTargetStatus.NotWritable, target, running);
        }

        return new MacUpdateTarget(MacUpdateTargetStatus.Ready, target, running);
    }
}
