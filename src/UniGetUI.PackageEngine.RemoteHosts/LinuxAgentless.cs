namespace UniGetUI.PackageEngine.RemoteHosts;

public static class LinuxAgentless
{
    private static readonly HashSet<string> CommandDirectories =
    [
        "/bin", "/sbin", "/usr/bin", "/usr/sbin", "/usr/local/bin", "/usr/local/sbin",
    ];

    public static readonly HashSet<string> SystemManagerIds =
        new(StringComparer.OrdinalIgnoreCase) { "apt", "dnf", "pacman", "snap", "flatpak" };

    public const string InventoryScript = """
export PATH="$HOME/.local/bin:$HOME/bin:/usr/local/bin:/usr/bin:/usr/local/sbin:/usr/sbin:$PATH"
printf '__UGUI_LINUX_V1__\n__UGUI_PROFILE__\n'
pretty=Linux
if [ -r /etc/os-release ]; then . /etc/os-release; pretty=${PRETTY_NAME:-${NAME:-Linux}}; fi
pretty=$(printf '%s' "$pretty" | tr '\t\r\n' '   ')
can_sudo=0; sudo -n true >/dev/null 2>&1 && can_sudo=1
system_manager=''
case " ${ID:-} ${ID_LIKE:-} " in
  *" debian "*|*" ubuntu "*) command -v apt-get >/dev/null 2>&1 && command -v dpkg-query >/dev/null 2>&1 && system_manager=apt ;;
  *" fedora "*|*" rhel "*|*" centos "*|*" amzn "*) command -v dnf >/dev/null 2>&1 && command -v rpm >/dev/null 2>&1 && system_manager=dnf ;;
  *" arch "*) command -v pacman >/dev/null 2>&1 && system_manager=pacman ;;
esac
if [ -z "$system_manager" ]; then
  if command -v dnf >/dev/null 2>&1 && command -v rpm >/dev/null 2>&1; then system_manager=dnf
  elif command -v apt-get >/dev/null 2>&1 && command -v dpkg-query >/dev/null 2>&1; then system_manager=apt
  elif command -v pacman >/dev/null 2>&1; then system_manager=pacman
  fi
fi
printf '%s\t%s\t%s\t%s\n' "$pretty" "$(uname -m)" "$can_sudo" "$system_manager"
if [ "$system_manager" = dnf ]; then
  printf '__UGUI_RPM_FILES__\n'
  rpm -qa --qf '[%{=NAME}\t%{=EPOCHNUM}:%{=VERSION}-%{=RELEASE}.%{=ARCH}\t%{FILEMODES:perms}\t%{FILENAMES}\n]' 2>/dev/null |
    awk -F '\t' '$3 ~ /x/ && $4 ~ /^\/(bin|sbin|usr\/bin|usr\/sbin|usr\/local\/bin|usr\/local\/sbin)\/[^\/]+$/'
  printf '__UGUI_SYSTEM_UPDATES__\n'
  if dnf -q makecache >/dev/null 2>&1; then
    dnf -q repoquery --upgrades --qf '%{name}\t%{epoch}:%{version}-%{release}.%{arch}' 2>/dev/null || true
  else
    printf '__UGUI_ERRORS__\nDNF could not refresh repository metadata.\n'
  fi
fi
if [ "$system_manager" = apt ]; then
  printf '__UGUI_APT_VERSIONS__\n'
  dpkg-query -W -f='${binary:Package}\t${Version}\t${db:Status-Abbrev}\n' 2>/dev/null |
    awk -F '\t' '$3 ~ /^ii/ { print $1 "\t" $2 }'
  printf '__UGUI_APT_FILES__\n'
  dpkg-query -S '/bin/*' '/sbin/*' '/usr/bin/*' '/usr/sbin/*' '/usr/local/bin/*' '/usr/local/sbin/*' 2>/dev/null |
    while IFS= read -r line; do path=${line#*: }; [ -f "$path" ] && [ -x "$path" ] && printf '%s\n' "$line"; done || true
  printf '__UGUI_APT_UPDATES__\n'
  refreshed=1
  [ "$can_sudo" = 0 ] || sudo -n apt-get update >/dev/null 2>&1 || refreshed=0
  LC_ALL=C apt-get -s -o Debug::NoLocking=1 upgrade 2>/dev/null | awk '/^Inst /' || true
  [ "$refreshed" = 1 ] || printf '__UGUI_ERRORS__\nAPT could not refresh repository metadata.\n'
fi
if [ "$system_manager" = pacman ]; then
  printf '__UGUI_PACMAN_PACKAGES__\n'
  pacman -Q 2>/dev/null | awk '{ print $1 "\t" $2 }' || true
  printf '__UGUI_PACMAN_FILES__\n'
  pacman -Ql 2>/dev/null | awk '$2 ~ /^\/(bin|sbin|usr\/bin|usr\/sbin|usr\/local\/bin|usr\/local\/sbin)\/[^\/]+$/ { print $1 "\t" $2 }' || true
  printf '__UGUI_PACMAN_UPDATES__\n'
  pacman -Qu 2>/dev/null | awk '{ print $1 "\t" $4 }' || true
fi
if command -v snap >/dev/null 2>&1; then
  printf '__UGUI_SNAP_LIST__\n'
  snap list --unicode=never 2>/dev/null || true
fi
if command -v flatpak >/dev/null 2>&1; then
  printf '__UGUI_FLATPAK_LIST__\n'
  flatpak list --app --columns=application,version,name 2>/dev/null || true
fi
if command -v npm >/dev/null 2>&1; then
  printf '__UGUI_NPM_ROOT__\n'; npm root -g 2>/dev/null || true
  printf '__UGUI_NPM_INSTALLED__\n'; npm ls -g --depth=0 --parseable --long 2>/dev/null || true
  printf '__UGUI_NPM_OUTDATED__\n'; npm outdated -g --parseable 2>/dev/null || true
fi
if command -v pip >/dev/null 2>&1 || command -v pip3 >/dev/null 2>&1; then
  printf '__UGUI_PIP_LIST__\n'
  (pip3 list --format=freeze 2>/dev/null || pip list --format=freeze 2>/dev/null) || true
fi
if command -v cargo >/dev/null 2>&1; then
  printf '__UGUI_CARGO__\n'; cargo install --list --color never 2>/dev/null || true
fi
printf '__UGUI_END__\n'
""";

    public static string ActionScript(string action, string managerId, string packageId)
    {
        string token = RemoteSshClient.ShellQuote(packageId);
        string command = (action, managerId.ToLowerInvariant()) switch
        {
            ("update", "apt") => $"sudo -n apt-get update 1>&2 && sudo -n apt-get -y --only-upgrade install {token}",
            ("uninstall", "apt") => $"sudo -n apt-get -y remove {token}",
            ("update", "dnf") => $"sudo -n dnf -y upgrade {token}",
            ("uninstall", "dnf") => $"sudo -n dnf -y remove {token}",
            ("update", "pacman") => $"sudo -n pacman -Sy --noconfirm {token}",
            ("uninstall", "pacman") => $"sudo -n pacman -R --noconfirm {token}",
            ("update", "snap") => $"sudo -n snap refresh {token}",
            ("uninstall", "snap") => $"sudo -n snap remove {token}",
            ("update", "flatpak") => $"flatpak update -y {token}",
            ("uninstall", "flatpak") => $"flatpak uninstall -y {token}",
            ("update", "npm") =>
                $"if [ -w \"$(npm root -g)\" ]; then npm install -g {RemoteSshClient.ShellQuote(packageId + "@latest")}; else sudo -n \"$(command -v npm)\" install -g {RemoteSshClient.ShellQuote(packageId + "@latest")}; fi",
            ("uninstall", "npm") =>
                $"if [ -w \"$(npm root -g)\" ]; then npm uninstall -g {token}; else sudo -n \"$(command -v npm)\" uninstall -g {token}; fi",
            ("uninstall", "cargo") => $"cargo uninstall {token} --color always",
            ("uninstall", "pip") => $"(pip3 uninstall -y {token} || pip uninstall -y {token})",
            _ => "echo 'This package action is not supported on Linux without UniGetUI.' >&2; exit 64",
        };

        return "set -e; export PATH=\"$HOME/.local/bin:$HOME/bin:/usr/local/bin:/usr/bin:/usr/local/sbin:/usr/sbin:$PATH\"; "
            + command
            + " 1>&2; printf '\\n__UGUI_LINUX_ACTION_OK__\\n'";
    }

    public const string UpdateAllScript = """
set -e
export PATH="$HOME/.local/bin:$HOME/bin:/usr/local/bin:/usr/bin:/usr/local/sbin:/usr/sbin:$PATH"
[ ! -r /etc/os-release ] || . /etc/os-release
manager=''
case " ${ID:-} ${ID_LIKE:-} " in
  *" debian "*|*" ubuntu "*) manager=apt ;;
  *" fedora "*|*" rhel "*|*" centos "*|*" amzn "*) manager=dnf ;;
  *" arch "*) manager=pacman ;;
esac
if [ -z "$manager" ]; then
  if command -v dnf >/dev/null 2>&1; then manager=dnf
  elif command -v apt-get >/dev/null 2>&1; then manager=apt
  elif command -v pacman >/dev/null 2>&1; then manager=pacman
  fi
fi
case "$manager" in
  apt) sudo -n apt-get update 1>&2; sudo -n apt-get -y upgrade 1>&2 ;;
  dnf) sudo -n dnf -y upgrade 1>&2 ;;
  pacman) sudo -n pacman -Syu --noconfirm 1>&2 ;;
  *) echo 'No supported Linux system package manager was found.' >&2; exit 64 ;;
esac
printf '\n__UGUI_LINUX_ACTION_OK__\n'
""";

    public static bool TryParseInventory(string output, out RemoteControlResponse? response)
    {
        response = null;
        if (!output.Contains(RemoteControlProtocol.LinuxInventoryMarker, StringComparison.Ordinal))
            return false;

        Dictionary<string, string> sections = ParseSections(output);
        string[] profile = (sections.GetValueOrDefault("PROFILE") ?? "")
            .Split('\t', StringSplitOptions.None);
        string? description = profile.Length > 0 && profile[0].Length > 0
            ? (profile.Length > 1 ? $"{profile[0]} ({profile[1]})" : profile[0])
            : null;
        bool canElevate = profile.Length > 2 && profile[2] == "1";
        string? systemManager = profile.Length > 3 && profile[3].Length > 0 ? profile[3] : null;

        if (systemManager is "apk" or "zypper")
        {
            response = new RemoteControlResponse
            {
                Protocol = RemoteControlProtocol.Version,
                Ok = false,
                Backend = "linux-agentless",
                Os = "linux",
                HostDescription = description,
                CanElevate = canElevate,
                SystemPackageManager = systemManager,
                Errors = ["This Linux distribution's system package manager is not supported yet."],
            };
            return true;
        }

        List<RemoteInventoryPackageDto> packages = [];
        if (systemManager is not null)
            packages.AddRange(ParseSystemPackages(systemManager, sections));
        packages.AddRange(ParseSnap(sections.GetValueOrDefault("SNAP_LIST")));
        packages.AddRange(ParseFlatpak(sections.GetValueOrDefault("FLATPAK_LIST")));
        packages.AddRange(ParseNpm(sections));
        packages.AddRange(ParsePip(sections.GetValueOrDefault("PIP_LIST")));
        packages.AddRange(ParseCargo(sections.GetValueOrDefault("CARGO")));

        List<string> errors = Lines(sections.GetValueOrDefault("ERRORS")).ToList();
        response = new RemoteControlResponse
        {
            Protocol = RemoteControlProtocol.Version,
            Ok = true,
            Backend = "linux-agentless",
            Os = "linux",
            HostDescription = description,
            CanElevate = canElevate,
            SystemPackageManager = systemManager,
            Packages = packages,
            Errors = errors,
        };
        return true;
    }

    internal static Dictionary<string, string> ParseSections(string output)
    {
        Dictionary<string, List<string>> buckets = [];
        string current = "";
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("__UGUI_", StringComparison.Ordinal) && line.EndsWith("__", StringComparison.Ordinal))
            {
                current = line["__UGUI_".Length..^2];
                if (current is "LINUX_V1" or "END")
                    current = "";
                else if (!buckets.ContainsKey(current))
                    buckets[current] = [];
                continue;
            }
            if (current.Length > 0)
                buckets[current].Add(line);
        }

        return buckets.ToDictionary(
            pair => pair.Key,
            pair => string.Join('\n', pair.Value).Trim(),
            StringComparer.Ordinal
        );
    }

    private static List<RemoteInventoryPackageDto> ParseSystemPackages(
        string manager,
        Dictionary<string, string> sections
    )
    {
        return manager switch
        {
            "dnf" => ParseRpm(manager, sections),
            "apt" => ParseApt(sections),
            "pacman" => ParsePacman(sections),
            _ => [],
        };
    }

    private static List<RemoteInventoryPackageDto> ParseRpm(string manager, Dictionary<string, string> sections)
    {
        Dictionary<string, string> latest = TabMap(sections.GetValueOrDefault("SYSTEM_UPDATES"));
        Dictionary<string, (string Version, List<string> Paths)> native = [];
        foreach (string line in Lines(sections.GetValueOrDefault("RPM_FILES")))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 4 || !fields[2].Contains('x', StringComparison.Ordinal))
                continue;
            string directory = Path.GetDirectoryName(fields[3])?.Replace('\\', '/') ?? "";
            if (!CommandDirectories.Contains(directory))
                continue;
            if (!native.TryGetValue(fields[0], out var entry))
                entry = (fields[1], []);
            entry.Paths.Add(fields[3]);
            native[fields[0]] = entry;
        }

        return native.Select(pair => ToDto(manager, pair.Key, pair.Value.Version, latest.GetValueOrDefault(pair.Key), true)).ToList();
    }

    private static List<RemoteInventoryPackageDto> ParseApt(Dictionary<string, string> sections)
    {
        Dictionary<string, string> versions = TabMap(sections.GetValueOrDefault("APT_VERSIONS"));
        Dictionary<string, string> latest = ParseAptLatest(sections.GetValueOrDefault("APT_UPDATES"));
        HashSet<string> names = [];
        foreach (string line in Lines(sections.GetValueOrDefault("APT_FILES")))
        {
            int delimiter = line.LastIndexOf(": /", StringComparison.Ordinal);
            if (delimiter < 0)
                continue;
            string path = line[(delimiter + 2)..].Trim();
            string directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            if (!CommandDirectories.Contains(directory))
                continue;
            foreach (string name in line[..delimiter].Split(',', StringSplitOptions.TrimEntries))
            {
                if (versions.ContainsKey(name))
                    names.Add(name);
            }
        }

        return names
            .Select(name => ToDto("apt", name, versions[name], latest.GetValueOrDefault(name), true))
            .ToList();
    }

    private static Dictionary<string, string> ParseAptLatest(string? output)
    {
        Dictionary<string, string> latest = [];
        foreach (string line in Lines(output))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[0] != "Inst")
                continue;
            int open = line.IndexOf('(');
            if (open < 0)
                continue;
            string version = line[(open + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (version.Length > 0)
                latest[fields[1]] = version;
        }
        return latest;
    }

    private static List<RemoteInventoryPackageDto> ParsePacman(Dictionary<string, string> sections)
    {
        Dictionary<string, string> versions = TabMap(sections.GetValueOrDefault("PACMAN_PACKAGES"));
        Dictionary<string, string> latest = TabMap(sections.GetValueOrDefault("PACMAN_UPDATES"));
        HashSet<string> names = [];
        foreach (string line in Lines(sections.GetValueOrDefault("PACMAN_FILES")))
        {
            string[] fields = line.Split('\t');
            if (fields.Length < 2)
                continue;
            string directory = Path.GetDirectoryName(fields[1])?.Replace('\\', '/') ?? "";
            if (CommandDirectories.Contains(directory) && versions.ContainsKey(fields[0]))
                names.Add(fields[0]);
        }

        return names
            .Select(name => ToDto("pacman", name, versions[name], latest.GetValueOrDefault(name), true))
            .ToList();
    }

    private static List<RemoteInventoryPackageDto> ParseSnap(string? output)
    {
        List<RemoteInventoryPackageDto> packages = [];
        foreach (string line in Lines(output).Skip(1))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0] is "Name" or "Notes")
                continue;
            packages.Add(ToDto("snap", fields[0], fields[1], null, true));
        }
        return packages;
    }

    private static List<RemoteInventoryPackageDto> ParseFlatpak(string? output)
    {
        List<RemoteInventoryPackageDto> packages = [];
        foreach (string line in Lines(output))
        {
            string[] fields = line.Split('\t', StringSplitOptions.None);
            if (fields.Length < 2 || fields[0] == "Application ID")
                continue;
            string name = fields.Length > 2 && fields[2].Length > 0 ? fields[2] : fields[0];
            packages.Add(ToDto("flatpak", fields[0], fields[1], null, true, name));
        }
        return packages;
    }

    private static List<RemoteInventoryPackageDto> ParseNpm(Dictionary<string, string> sections)
    {
        Dictionary<string, string> latest = [];
        foreach (string line in Lines(sections.GetValueOrDefault("NPM_OUTDATED")))
        {
            string[] fields = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 4)
                latest[Path.GetFileName(fields[0].Trim())] = fields[3].Trim();
        }

        List<RemoteInventoryPackageDto> packages = [];
        foreach (string line in Lines(sections.GetValueOrDefault("NPM_INSTALLED")))
        {
            // parseable --long: /path:name@version:extra
            string[] fields = line.Split(':');
            if (fields.Length < 2)
                continue;
            string nameVersion = fields[1];
            int at = nameVersion.LastIndexOf('@');
            if (at <= 0)
                continue;
            string name = nameVersion[..at];
            string version = nameVersion[(at + 1)..];
            if (name is "npm" or "")
                continue;
            packages.Add(ToDto("npm", name, version, latest.GetValueOrDefault(name), false));
        }
        return packages;
    }

    private static List<RemoteInventoryPackageDto> ParsePip(string? output)
    {
        List<RemoteInventoryPackageDto> packages = [];
        foreach (string line in Lines(output))
        {
            int eq = line.IndexOf("==", StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            packages.Add(ToDto("pip", line[..eq], line[(eq + 2)..], null, false));
        }
        return packages;
    }

    private static List<RemoteInventoryPackageDto> ParseCargo(string? output)
    {
        List<RemoteInventoryPackageDto> packages = [];
        foreach (string line in Lines(output))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;
            string trimmed = line.TrimEnd(':');
            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[^1].StartsWith('v'))
                continue;
            packages.Add(ToDto("cargo", string.Join(' ', parts[..^1]), parts[^1][1..], null, false));
        }
        return packages;
    }

    private static RemoteInventoryPackageDto ToDto(
        string managerId,
        string id,
        string version,
        string? newVersion,
        bool canRunAsAdmin,
        string? name = null
    )
    {
        bool upgradable = !string.IsNullOrEmpty(newVersion) && newVersion != version;
        return new RemoteInventoryPackageDto
        {
            ManagerId = managerId,
            Id = id,
            Name = name ?? id,
            Version = version,
            NewVersion = upgradable ? newVersion : null,
            Source = managerId,
            IsUpgradable = upgradable,
            CanRunAsAdmin = canRunAsAdmin,
        };
    }

    private static Dictionary<string, string> TabMap(string? output)
    {
        Dictionary<string, string> map = [];
        foreach (string line in Lines(output))
        {
            string[] fields = line.Split('\t');
            if (fields.Length >= 2)
                map[fields[0]] = fields[1];
        }
        return map;
    }

    private static IEnumerable<string> Lines(string? output)
        => string.IsNullOrEmpty(output)
            ? []
            : output.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0);
}
