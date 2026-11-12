using System.ComponentModel;
using System.Diagnostics;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Tools;

public enum LinuxUpdateTargetStatus
{
    Ready,
    PackageManaged,
    OwnershipUnknown,
    ReadOnlyFileSystem,
    NotWritable,
}

public sealed record LinuxUpdateTarget(LinuxUpdateTargetStatus Status, string InstallDirectory, string? PackageFormat);

public sealed record PackageOwnership(string? Format, bool IsKnown)
{
    public static readonly PackageOwnership NotOwned = new(null, true);
    public static readonly PackageOwnership Unknown = new(null, false);

    public static PackageOwnership OwnedBy(string format) => new(format, true);
}

public sealed record PackageQueryResult(int ExitCode, string Output);

public static class LinuxUpdateTargetResolver
{
    // Positional args: $1=pid, $2=install dir, $3=new tree, $4=executable name,
    // $5=update log, $6=failure marker, $7=redacted install dir.
    public const string SwapScript = """
        pid="$1"; dir="$2"; newroot="$3"; exe="$4"; log="$5"; marker="$6"; shown="$7"
        reason() { printf 'Update swap failed while %s: %s\n' "$1" "${2##*: }" >> "$log"; }
        i=0
        while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 150 ]; do sleep 0.2; i=$((i+1)); done
        rm -rf "$dir.old"
        run="$dir/$exe"
        if err=$(LC_ALL=C mv "$dir" "$dir.old" 2>&1); then
          if err=$(LC_ALL=C mv "$newroot" "$dir" 2>&1); then
            chmod +x "$dir/$exe" 2>/dev/null
            rm -rf "$dir.old"
          else
            reason "moving the new version into place" "$err"
            rm -rf "$dir"
            if ! err=$(LC_ALL=C mv "$dir.old" "$dir" 2>&1); then
              reason "restoring the previous version, which was left in $shown.old" "$err"
              run="$dir.old/$exe"
            fi
            printf '\n%s %s\n' "$marker" "$shown" >> "$log"
          fi
        else
          reason "moving the installed version aside" "$err"
          printf '\n%s %s\n' "$marker" "$shown" >> "$log"
        fi
        "$run" >/dev/null 2>&1 &
        """;

    public static LinuxUpdateTarget Resolve(
        string executablePath,
        string installDirectory,
        Func<string, PackageOwnership> findOwnership,
        Func<string, bool> isOnReadOnlyFileSystem,
        Func<string, bool> isWritable)
    {
        PackageOwnership ownership = findOwnership(executablePath);
        if (ownership.Format is not null)
        {
            return new LinuxUpdateTarget(LinuxUpdateTargetStatus.PackageManaged, installDirectory, ownership.Format);
        }

        if (!ownership.IsKnown)
        {
            return new LinuxUpdateTarget(LinuxUpdateTargetStatus.OwnershipUnknown, installDirectory, null);
        }

        if (isOnReadOnlyFileSystem(installDirectory))
        {
            return new LinuxUpdateTarget(LinuxUpdateTargetStatus.ReadOnlyFileSystem, installDirectory, null);
        }

        if (!SelfUpdateSwap.CanReplace(installDirectory, isWritable))
        {
            return new LinuxUpdateTarget(LinuxUpdateTargetStatus.NotWritable, installDirectory, null);
        }

        return new LinuxUpdateTarget(LinuxUpdateTargetStatus.Ready, installDirectory, null);
    }
}

public static class LinuxPackageOwnership
{
    public const string DpkgDatabase = "/var/lib/dpkg/status";
    public static readonly string[] RpmDatabases = ["/var/lib/rpm", "/usr/lib/sysimage/rpm"];

    private const string RpmNotOwned = "is not owned by any package";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    public static PackageOwnership Find(string path) =>
        Find(path, File.Exists(DpkgDatabase), Array.Exists(RpmDatabases, Directory.Exists), RunQuery);

    public static PackageOwnership Find(
        string path,
        bool hasDpkgDatabase,
        bool hasRpmDatabase,
        Func<string, string, string, PackageQueryResult?> runQuery)
    {
        bool failed = false;

        if (hasDpkgDatabase)
        {
            PackageQueryResult? dpkg = runQuery("dpkg-query", "-S", EscapeDpkgPattern(path));
            if (dpkg is { ExitCode: 0 })
            {
                return PackageOwnership.OwnedBy(".deb");
            }

            failed |= dpkg is not { ExitCode: 1 };
        }

        if (hasRpmDatabase)
        {
            PackageQueryResult? rpm = runQuery("rpm", "-qf", path);
            if (rpm is { ExitCode: 0 })
            {
                return PackageOwnership.OwnedBy(".rpm");
            }

            failed |= rpm is null || !rpm.Output.Contains(RpmNotOwned, StringComparison.Ordinal);
        }

        return failed ? PackageOwnership.Unknown : PackageOwnership.NotOwned;
    }

    public static string EscapeDpkgPattern(string path)
    {
        System.Text.StringBuilder escaped = new(path.Length);
        foreach (char c in path)
        {
            if (c is '*' or '?' or '[' or ']' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    public static PackageQueryResult? RunQuery(string tool, string flag, string path) =>
        RunQuery(tool, flag, path, QueryTimeout);

    public static PackageQueryResult? RunQuery(string tool, string flag, string path, TimeSpan timeout)
    {
        Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = tool,
                ArgumentList = { flag, path },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.StartInfo.Environment["LC_ALL"] = "C";

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not ask '{tool}' whether '{path}' belongs to a package: {ex.Message}");
            p.Dispose();
            return null;
        }

        using (p)
        {
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!Task.WaitAll([p.WaitForExitAsync(), stdout, stderr], timeout))
            {
                Logger.Warn($"'{tool} {flag} {path}' did not answer within {timeout.TotalSeconds:0} seconds.");
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    Logger.Warn($"Could not stop '{tool}' after it timed out: {ex.Message}");
                }

                return null;
            }

            string answer = (stdout.Result + "\n" + stderr.Result).Trim();
            Logger.Info($"'{tool} {flag} {path}' exited with {p.ExitCode}: {answer}");
            return new PackageQueryResult(p.ExitCode, answer);
        }
    }
}
