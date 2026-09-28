// Skill installation (symlink/copy) and installed-skill listing (port of installer.ts).

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Skills;

internal enum InstallMode
{
    Symlink,
    Copy,
}

internal sealed class InstallOptions
{
    public bool Global { get; init; }
    public string? Cwd { get; init; }
    public InstallMode? Mode { get; init; }
    public string? EveSubagent { get; init; }

    /// Create a missing agent-specific project root because the user selected this agent.
    public bool CreateMissingAgentRoot { get; init; }
}

internal sealed class InstallResult
{
    public bool Success { get; init; }
    public string Path { get; init; } = "";
    public string? CanonicalPath { get; init; }
    public InstallMode Mode { get; init; }
    public bool SymlinkFailed { get; init; }
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
    public string? Error { get; init; }

    public static InstallResult Ok(string path, string? canonical, InstallMode mode) => new() { Success = true, Path = path, CanonicalPath = canonical, Mode = mode };

    public static InstallResult Fail(string path, InstallMode mode, string error) => new() { Success = false, Path = path, Mode = mode, Error = error };
}

internal sealed record InstalledSkill(string Name, string Description, string Path, string CanonicalPath, string Scope, List<string> Agents);

internal static partial class Installer
{
    public static string ModeName(InstallMode m) => m == InstallMode.Symlink ? "symlink" : "copy";

    [GeneratedRegex("[^a-z0-9._]+")] private static partial Regex NonName();
    [GeneratedRegex(@"^[.\-]+|[.\-]+$")] private static partial Regex EdgeDotsDashes();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"[\/\\:\0]")] private static partial Regex PathChars();
    [GeneratedRegex(@"^\r?\n")] private static partial Regex LeadingNewline();

    /// Sanitize a skill name into a safe kebab-case directory name.
    public static string SanitizeName(string name)
    {
        var s = EdgeDotsDashes().Replace(NonName().Replace(name.ToLowerInvariant(), "-"), "");
        s = s.Length > 255 ? s[..255] : s;
        return s.Length == 0 ? "unnamed-skill" : s;
    }

    private static bool PathsOverlap(string a, string b) => NodePath.IsPathSafe(a, b) || NodePath.IsPathSafe(b, a);

    private static bool ShouldSkipProjectAgentSymlink(string agentType, bool isGlobal, string cwd, bool createMissing)
    {
        var a = Agents.Get(agentType);
        if (isGlobal || Agents.IsUniversalAgent(agentType) || createMissing || a.CreateProjectSkillsDirByDefault) return false;
        return !Fs.Exists(NodePath.Join(cwd, a.SkillsDir.Split('/')[0]));
    }

    private static bool IsDirOrLinkToDir(DirEntry e) => e.IsDirectory || (e.IsSymlink && Fs.IsDir(e.FullPath));

    private static string BaseDir(bool global, string? cwd) => global ? Sys.HomeDir() : string.IsNullOrEmpty(cwd) ? Sys.Cwd() : cwd;

    public static string GetCanonicalSkillsDir(bool global, string? cwd) => NodePath.Join(BaseDir(global, cwd), Constants.AgentsDir, Constants.SkillsSubdir);

    public static string GetEveSubagentSkillsDir(string subagent, string? cwd) =>
        NodePath.Join(BaseDir(false, cwd), Agents.EveSubagentsDir, SanitizeName(subagent), "skills");

    public static string GetAgentBaseDir(string agentType, bool global, string? cwd, string? eveSubagent)
    {
        if (Agents.IsUniversalAgent(agentType)) return GetCanonicalSkillsDir(global, cwd);
        if (agentType == "eve" && !string.IsNullOrEmpty(eveSubagent)) return GetEveSubagentSkillsDir(eveSubagent, cwd);
        var a = Agents.Get(agentType);
        var b = BaseDir(global, cwd);
        if (global) return a.GlobalSkillsDir ?? NodePath.Join(b, a.SkillsDir);
        return NodePath.Join(b, a.SkillsDir);
    }

    private static void CleanAndCreateDirectory(string path)
    {
        Forget(path);
        try { Fs.RemoveAll(path); } catch { /* mkdir will fail if there's a real problem */ }
        Directory.CreateDirectory(path);
    }

    // ─── Populated directories ───
    //
    // The TS installer cleans and re-copies the canonical directory once per
    // target agent, and every universal agent shares that directory, so a
    // 20-agent install copies each skill 20 times. Remember which directories
    // this run already filled from which source and skip identical refills: the
    // resulting tree is the same, only the redundant I/O is gone.

    private sealed record PopulatedFrom(string? SourceDir, List<SnapshotFile>? Files, bool Eve)
    {
        public bool SameAs(PopulatedFrom o) => SourceDir == o.SourceDir && ReferenceEquals(Files, o.Files) && Eve == o.Eve;
    }

    private static readonly Dictionary<string, PopulatedFrom> ProcessPopulated = new(StringComparer.Ordinal);

    /// One run per process in the CLI; one per call for the public API, so
    /// concurrent calls never reset each other's state.
    private static Dictionary<string, PopulatedFrom> Populated => Sys.Context is { } c
        ? (Dictionary<string, PopulatedFrom>)LazyInitializer.EnsureInitialized(ref c.PopulatedCache, () => new Dictionary<string, PopulatedFrom>(StringComparer.Ordinal))
        : ProcessPopulated;

    /// Start a new install run: forget every directory populated so far.
    public static void ResetPopulated()
    {
        lock (Populated) Populated.Clear();
    }

    private static void Forget(string dir)
    {
        lock (Populated) Populated.Remove(NodePath.Resolve(dir));
    }

    private static bool AlreadyPopulated(string dir, PopulatedFrom from)
    {
        lock (Populated)
            return Populated.TryGetValue(NodePath.Resolve(dir), out var prev) && prev.SameAs(from) && Fs.IsDir(dir);
    }

    private static void MarkPopulated(string dir, PopulatedFrom from)
    {
        lock (Populated) Populated[NodePath.Resolve(dir)] = from;
    }

    /// Clean `dest` and copy the skill directory `src` into it, unless this run
    /// already did exactly that.
    private static void PopulateFromDirectory(string dest, string src, string? agentType)
    {
        var from = new PopulatedFrom(NodePath.Resolve(src), null, agentType == "eve");
        if (AlreadyPopulated(dest, from)) return;
        CleanAndCreateDirectory(dest);
        CopyDirectory(src, dest, agentType);
        MarkPopulated(dest, from);
    }

    /// Clean `dest` and write the in-memory `files` into it, unless this run
    /// already did exactly that.
    private static void PopulateFromFiles(string dest, List<SnapshotFile> files, string agentType)
    {
        var from = new PopulatedFrom(null, files, agentType == "eve");
        if (AlreadyPopulated(dest, from)) return;
        CleanAndCreateDirectory(dest);
        WriteFiles(dest, files, agentType);
        MarkPopulated(dest, from);
    }

    private static string ResolveParentSymlinks(string p)
    {
        var resolved = NodePath.Resolve(p);
        var dir = NodePath.Dirname(resolved);
        return Fs.RealPath(dir) is { } real ? NodePath.Join(real, NodePath.Basename(resolved)) : resolved;
    }

    /// Create a symlink (a junction on Windows). False means fall back to copying.
    private static bool CreateSymlink(string target, string linkPath)
    {
        try
        {
            var resolvedTarget = NodePath.Resolve(target);
            var resolvedLink = NodePath.Resolve(linkPath);
            var realTarget = Fs.RealPath(resolvedTarget) ?? resolvedTarget;
            var realLink = Fs.RealPath(resolvedLink) ?? resolvedLink;
            if (realTarget == realLink) return true;
            if (ResolveParentSymlinks(target) == ResolveParentSymlinks(linkPath)) return true;
            Forget(linkPath);
            if (Fs.LExists(linkPath))
            {
                if (Fs.IsSymlink(linkPath) && Fs.ReadLink(linkPath) is { } existing
                    && NodePath.Resolve(NodePath.Dirname(linkPath), existing) == resolvedTarget)
                    return true;
                Fs.RemoveAll(linkPath);
            }
            var linkDir = NodePath.Dirname(linkPath);
            Directory.CreateDirectory(linkDir);
            var relative = NodePath.Relative(ResolveParentSymlinks(linkDir), target);
            Fs.CreateDirLink(resolvedTarget, linkPath, relative);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExcluded(string name, bool isDir) => name == "metadata.json" || (isDir && name is ".git" or "__pycache__" or "__pypackages__");

    /// Keep only the frontmatter fields Eve understands.
    public static string StripIgnoredEveFrontmatter(string raw)
    {
        JsonObject data;
        string content;
        try
        {
            var fm = Frontmatter.Parse(raw);
            (data, content) = (fm.Data, fm.Content);
        }
        catch (YamlParseException)
        {
            (data, content) = (new JsonObject(), raw);
        }
        var eve = new JsonObject();
        if (Json.Str(data, "name") is { } n) eve["name"] = n;
        if (Json.Str(data, "description") is { } d) eve["description"] = d;
        if (Json.Str(data, "license") is { } l) eve["license"] = l;
        if (data.ContainsKey("compatibility")) eve["compatibility"] = Json.Clone(data["compatibility"]);
        if (data["version"] is JsonValue v && (Json.AsString(v) != null || Json.AsNumber(v) != null)) eve["version"] = Json.Clone(v);
        if (data["metadata"] is JsonObject m) eve["metadata"] = Json.Clone(m);
        var body = LeadingNewline().Replace(content, "", 1);
        if (eve.Count == 0) return body;
        return $"---\n{Frontmatter.Stringify(eve).TrimEnd()}\n---\n{body}";
    }

    /// Recursive copy following links, with no exclusions (cp dereference+recursive).
    /// Directories are created now; files are queued in `files`.
    private static void CpDereference(string src, string dest, List<(string Src, string Dest)> files)
    {
        if (Fs.IsDir(src))
        {
            Directory.CreateDirectory(dest);
            foreach (var e in Fs.ReadDir(src)) CpDereference(e.FullPath, NodePath.Join(dest, e.Name), files);
            return;
        }
        if (!Fs.IsFile(src)) throw new FileNotFoundException($"ENOENT: no such file or directory, stat '{src}'");
        files.Add((src, dest));
    }

    /// Copy a skill directory. The tree walk (exclusions, links, Eve rewrites,
    /// directory creation) is sequential; file copies then run in parallel, as
    /// the TS implementation's concurrent `copyDirectory` does.
    private static void CopyDirectory(string src, string dest, string? agentType)
    {
        var files = new List<(string Src, string Dest)>();
        CollectDirectory(src, dest, agentType, files);
        ForEachParallel(files, f => Fs.CopyFile(f.Src, f.Dest));
    }

    /// Run `action` over `items`, in parallel when there are enough of them.
    /// The first failure is rethrown unwrapped so callers' catch filters apply.
    private static void ForEachParallel<T>(IReadOnlyList<T> items, Action<T> action)
    {
        if (items.Count < 16)
        {
            foreach (var item in items) action(item);
            return;
        }
        try
        {
            Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16) }, action);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
        }
    }

    private static void CollectDirectory(string src, string dest, string? agentType, List<(string Src, string Dest)> files)
    {
        Directory.CreateDirectory(dest);
        foreach (var e in Fs.ReadDir(src))
        {
            if (IsExcluded(e.Name, e.IsDirectory)) continue;
            var destPath = NodePath.Join(dest, e.Name);
            if (e.IsDirectory)
            {
                CollectDirectory(e.FullPath, destPath, agentType, files);
                continue;
            }
            if (agentType == "eve" && e.Name.ToLowerInvariant() == "skill.md")
            {
                File.WriteAllText(destPath, StripIgnoredEveFrontmatter(SkillDiscovery.ReadUtf8(e.FullPath)));
                continue;
            }
            try
            {
                CpDereference(e.FullPath, destPath, files);
            }
            catch (FileNotFoundException) when (e.IsSymlink)
            {
                Sys.Warn($"Skipping broken symlink: {e.FullPath}");
            }
            catch (DirectoryNotFoundException) when (e.IsSymlink)
            {
                Sys.Warn($"Skipping broken symlink: {e.FullPath}");
            }
        }
    }

    private static string ToEveFlatSkillFileName(string installName) => $"{SanitizeName(installName)}.md";

    private static string GetEveFlatSkillMarkdown(List<SnapshotFile> files)
    {
        var f = files.FirstOrDefault(x => NodePath.Basename(x.Path).ToLowerInvariant() == "skill.md")
                ?? files.FirstOrDefault(x => NodePath.Extname(x.Path).ToLowerInvariant() == ".md");
        return f == null ? "" : StripIgnoredEveFrontmatter(Encoding.UTF8.GetString(f.Contents));
    }

    public static bool IsSkillInstalled(string skillName, string agentType, bool global, string? cwd = null, string? eveSubagent = null)
    {
        var a = Agents.Get(agentType);
        var sanitized = SanitizeName(skillName);
        if (global && a.GlobalSkillsDir == null) return false;
        var targetBase = global ? a.GlobalSkillsDir!
            : agentType == "eve" && !string.IsNullOrEmpty(eveSubagent) ? GetEveSubagentSkillsDir(eveSubagent, cwd)
            : NodePath.Join(BaseDir(false, cwd), a.SkillsDir);
        var skillDir = NodePath.Join(targetBase, sanitized);
        return NodePath.IsPathSafe(targetBase, skillDir) && Fs.Exists(skillDir);
    }

    private const string TraversalError = "Invalid skill name: potential path traversal detected";

    /// <exception cref="InvalidOperationException">Traversal.</exception>
    public static string GetInstallPath(string skillName, string agentType, bool global, string? cwd = null, string? eveSubagent = null)
    {
        var b = GetAgentBaseDir(agentType, global, cwd, eveSubagent);
        var p = NodePath.Join(b, SanitizeName(skillName));
        if (!NodePath.IsPathSafe(b, p)) throw new InvalidOperationException(TraversalError);
        return p;
    }

    /// The canonical .agents/skills/&lt;skill&gt; path (Eve uses its own dir).
    public static string GetCanonicalPath(string skillName, bool global, string? cwd = null, string? agentType = null, string? eveSubagent = null)
    {
        var b = agentType == "eve" ? GetAgentBaseDir("eve", global, cwd, eveSubagent) : GetCanonicalSkillsDir(global, cwd);
        var p = NodePath.Join(b, SanitizeName(skillName));
        if (!NodePath.IsPathSafe(b, p)) throw new InvalidOperationException(TraversalError);
        return p;
    }

    private sealed record Dirs(string CanonicalBase, string CanonicalDir, string AgentBase, string AgentDir);

    private static Dirs ComputeDirs(string skillName, string agentType, bool isGlobal, string cwd, InstallMode mode, string? eveSubagent)
    {
        var canonicalBase = agentType == "eve" && mode == InstallMode.Symlink ? GetAgentBaseDir(agentType, isGlobal, cwd, eveSubagent) : GetCanonicalSkillsDir(isGlobal, cwd);
        var agentBase = GetAgentBaseDir(agentType, isGlobal, cwd, eveSubagent);
        return new Dirs(canonicalBase, NodePath.Join(canonicalBase, skillName), agentBase, NodePath.Join(agentBase, skillName));
    }

    private static InstallResult? TraversalCheck(Dirs d, InstallMode mode) =>
        !NodePath.IsPathSafe(d.CanonicalBase, d.CanonicalDir) || !NodePath.IsPathSafe(d.AgentBase, d.AgentDir) ? InstallResult.Fail(d.AgentDir, mode, TraversalError) : null;

    private static InstallResult NoGlobalSupport(string agentType, InstallMode mode) =>
        InstallResult.Fail("", mode, $"{Agents.Get(agentType).DisplayName} does not support global skill installation");

    /// Install a skill from a directory on disk.
    public static InstallResult InstallSkillForAgent(Skill skill, string agentType, InstallOptions o)
    {
        var a = Agents.Get(agentType);
        var isGlobal = o.Global;
        var cwd = string.IsNullOrEmpty(o.Cwd) ? Sys.Cwd() : o.Cwd;
        var mode = o.Mode ?? InstallMode.Symlink;
        if (isGlobal && a.GlobalSkillsDir == null) return NoGlobalSupport(agentType, mode);
        var skillName = SanitizeName(skill.Name.Length == 0 ? NodePath.Basename(skill.Path) : skill.Name);
        var d = ComputeDirs(skillName, agentType, isGlobal, cwd, mode, o.EveSubagent);
        if (TraversalCheck(d, mode) is { } err) return err;
        try
        {
            if (PathsOverlap(skill.Path, d.AgentDir)) return new InstallResult { Success = true, Path = d.AgentDir, Mode = mode, Skipped = true };
            if (mode == InstallMode.Copy)
            {
                PopulateFromDirectory(d.AgentDir, skill.Path, agentType);
                return InstallResult.Ok(d.AgentDir, null, InstallMode.Copy);
            }
            if (PathsOverlap(skill.Path, d.CanonicalDir))
                return new InstallResult { Success = true, Path = d.CanonicalDir, CanonicalPath = d.CanonicalDir, Mode = InstallMode.Symlink, Skipped = true };
            PopulateFromDirectory(d.CanonicalDir, skill.Path, agentType);
            if (isGlobal && Agents.IsUniversalAgent(agentType)) return InstallResult.Ok(d.CanonicalDir, d.CanonicalDir, InstallMode.Symlink);
            if (ShouldSkipProjectAgentSymlink(agentType, isGlobal, cwd, o.CreateMissingAgentRoot))
                return new InstallResult { Success = true, Path = d.CanonicalDir, CanonicalPath = d.CanonicalDir, Mode = InstallMode.Symlink, Skipped = true, SkipReason = "missing-agent-project-directory" };
            if (!CreateSymlink(d.CanonicalDir, d.AgentDir))
            {
                PopulateFromDirectory(d.AgentDir, skill.Path, agentType);
                return new InstallResult { Success = true, Path = d.AgentDir, CanonicalPath = d.CanonicalDir, Mode = InstallMode.Symlink, SymlinkFailed = true };
            }
            return InstallResult.Ok(d.AgentDir, d.CanonicalDir, InstallMode.Symlink);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return InstallResult.Fail(d.AgentDir, mode, e.Message);
        }
    }

    private static void WriteFiles(string targetDir, IEnumerable<SnapshotFile> files, string agentType)
    {
        // Snapshot paths are unique, so after creating the parent directories
        // in order the writes are independent and can run in parallel.
        var writes = new List<(string Path, SnapshotFile File)>();
        foreach (var f in files)
        {
            var full = NodePath.Join(targetDir, f.Path);
            if (!NodePath.IsPathSafe(targetDir, full)) continue;
            var parent = NodePath.Dirname(full);
            if (parent != targetDir) Directory.CreateDirectory(parent);
            writes.Add((full, f));
        }
        ForEachParallel(writes, w =>
        {
            if (agentType == "eve" && NodePath.Basename(w.File.Path).ToLowerInvariant() == "skill.md")
                File.WriteAllText(w.Path, StripIgnoredEveFrontmatter(Encoding.UTF8.GetString(w.File.Contents)));
            else
                File.WriteAllBytes(w.Path, w.File.Contents);
        });
    }

    /// Install an in-memory skill (well-known or blob snapshot).
    private static InstallResult InstallFiles(string installName, List<SnapshotFile> files, string agentType, InstallOptions o, bool skipMissingProjectDirs)
    {
        var a = Agents.Get(agentType);
        var isGlobal = o.Global;
        var cwd = string.IsNullOrEmpty(o.Cwd) ? Sys.Cwd() : o.Cwd;
        var mode = o.Mode ?? InstallMode.Symlink;
        if (isGlobal && a.GlobalSkillsDir == null) return NoGlobalSupport(agentType, mode);
        var d = ComputeDirs(SanitizeName(installName), agentType, isGlobal, cwd, mode, o.EveSubagent);
        if (TraversalCheck(d, mode) is { } err) return err;
        try
        {
            if (mode == InstallMode.Copy)
            {
                PopulateFromFiles(d.AgentDir, files, agentType);
                return InstallResult.Ok(d.AgentDir, null, InstallMode.Copy);
            }
            PopulateFromFiles(d.CanonicalDir, files, agentType);
            if (isGlobal && Agents.IsUniversalAgent(agentType)) return InstallResult.Ok(d.CanonicalDir, d.CanonicalDir, InstallMode.Symlink);
            if (skipMissingProjectDirs && ShouldSkipProjectAgentSymlink(agentType, isGlobal, cwd, o.CreateMissingAgentRoot))
                return new InstallResult { Success = true, Path = d.CanonicalDir, CanonicalPath = d.CanonicalDir, Mode = InstallMode.Symlink, Skipped = true, SkipReason = "missing-agent-project-directory" };
            if (!CreateSymlink(d.CanonicalDir, d.AgentDir))
            {
                PopulateFromFiles(d.AgentDir, files, agentType);
                return new InstallResult { Success = true, Path = d.AgentDir, CanonicalPath = d.CanonicalDir, Mode = InstallMode.Symlink, SymlinkFailed = true };
            }
            return InstallResult.Ok(d.AgentDir, d.CanonicalDir, InstallMode.Symlink);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return InstallResult.Fail(d.AgentDir, mode, e.Message);
        }
    }

    public static InstallResult InstallWellKnownSkillForAgent(string installName, List<SnapshotFile> files, string agentType, InstallOptions o) =>
        InstallFiles(installName, files, agentType, o, false);

    public static InstallResult InstallBlobSkillForAgent(string installName, List<SnapshotFile> files, string agentType, InstallOptions o)
    {
        var a = Agents.Get(agentType);
        var mode = o.Mode ?? InstallMode.Symlink;
        if (o.Global && a.GlobalSkillsDir == null) return NoGlobalSupport(agentType, mode);
        var isPackaged = files.Any(f => NodePath.Basename(f.Path).ToLowerInvariant() == "skill.md");
        if (agentType == "eve" && !isPackaged)
        {
            var cwd = string.IsNullOrEmpty(o.Cwd) ? Sys.Cwd() : o.Cwd;
            var agentBase = GetAgentBaseDir(agentType, o.Global, cwd, o.EveSubagent);
            var flat = NodePath.Join(agentBase, ToEveFlatSkillFileName(installName));
            if (!NodePath.IsPathSafe(agentBase, flat)) return InstallResult.Fail(flat, mode, TraversalError);
            try
            {
                Directory.CreateDirectory(agentBase);
                try { Fs.RemoveAll(flat); } catch { /* force */ }
                File.WriteAllText(flat, GetEveFlatSkillMarkdown(files));
                return InstallResult.Ok(flat, null, InstallMode.Copy);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return InstallResult.Fail(flat, mode, e.Message);
            }
        }
        return InstallFiles(installName, files, agentType, o, true);
    }

    private sealed record Scope(bool Global, string Path, string? AgentType);

    /// All installed skills in canonical and agent-specific directories.
    public static List<InstalledSkill> ListInstalledSkills(bool? global, string? cwd = null, IReadOnlyCollection<string>? agentFilter = null)
    {
        cwd ??= Sys.Cwd();
        var skills = new List<InstalledSkill>();
        var scopes = new List<Scope>();
        var detected = Agents.DetectInstalledAgents();
        var toCheck = agentFilter != null ? detected.Where(agentFilter.Contains).ToList() : detected;
        var scopeTypes = global.HasValue ? new[] { global.Value } : [false, true];
        foreach (var isGlobal in scopeTypes)
        {
            scopes.Add(new Scope(isGlobal, GetCanonicalSkillsDir(isGlobal, cwd), null));
            foreach (var at in toCheck)
            {
                var a = Agents.Get(at);
                if (isGlobal && a.GlobalSkillsDir == null) continue;
                var dir = isGlobal ? a.GlobalSkillsDir! : NodePath.Join(cwd, a.SkillsDir);
                if (!scopes.Any(s => s.Path == dir && s.Global == isGlobal)) scopes.Add(new Scope(isGlobal, dir, at));
                if (at == "eve" && !isGlobal)
                {
                    foreach (var sub in Agents.GetEveSubagents(cwd))
                    {
                        var subDir = GetEveSubagentSkillsDir(sub, cwd);
                        if (!scopes.Any(s => s.Path == subDir && s.Global == isGlobal)) scopes.Add(new Scope(isGlobal, subDir, at));
                    }
                }
            }
            foreach (var a in Agents.List)
            {
                if (toCheck.Contains(a.Name)) continue;
                if (isGlobal && a.GlobalSkillsDir == null) continue;
                var dir = isGlobal ? a.GlobalSkillsDir! : NodePath.Join(cwd, a.SkillsDir);
                if (scopes.Any(s => s.Path == dir && s.Global == isGlobal)) continue;
                if (Fs.Exists(dir)) scopes.Add(new Scope(isGlobal, dir, a.Name));
            }
        }

        InstalledSkill? Find(string scope, string name) => skills.FirstOrDefault(s => s.Scope == scope && s.Name == name);

        foreach (var scope in scopes)
        {
            List<DirEntry> entries;
            try
            {
                entries = Fs.ReadDir(scope.Path);
            }
            catch
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (!IsDirOrLinkToDir(entry)) continue;
                var skillDir = NodePath.Join(scope.Path, entry.Name);
                var md = NodePath.Join(skillDir, "SKILL.md");
                if (!Fs.Exists(md)) continue;
                var skill = SkillDiscovery.ParseSkillMd(md, false);
                if (skill == null) continue;
                var scopeKey = scope.Global ? "global" : "project";
                if (scope.AgentType is { } agentType)
                {
                    var existing = Find(scopeKey, skill.Name);
                    if (existing != null)
                    {
                        if (!existing.Agents.Contains(agentType)) existing.Agents.Add(agentType);
                    }
                    else
                    {
                        skills.Add(new InstalledSkill(skill.Name, skill.Description, skillDir, skillDir, scopeKey, [agentType]));
                    }
                    continue;
                }
                var sanitized = SanitizeName(skill.Name);
                var installedAgents = new List<string>();
                foreach (var at in toCheck)
                {
                    var a = Agents.Get(at);
                    if (scope.Global && a.GlobalSkillsDir == null) continue;
                    var agentBase = GetAgentBaseDir(at, scope.Global, cwd, null);
                    var alt = PathChars().Replace(Whitespace().Replace(skill.Name.ToLowerInvariant(), "-"), "");
                    var possible = new List<string>();
                    foreach (var n in new[] { entry.Name, sanitized, alt })
                        if (!possible.Contains(n)) possible.Add(n);
                    var found = possible.Any(n =>
                    {
                        var dir = NodePath.Join(agentBase, n);
                        return NodePath.IsPathSafe(agentBase, dir) && Fs.Exists(dir);
                    });
                    if (!found)
                    {
                        foreach (var ae in Fs.TryReadDir(agentBase))
                        {
                            var cand = NodePath.Join(agentBase, ae.Name);
                            if (!IsDirOrLinkToDir(ae) || !NodePath.IsPathSafe(agentBase, cand)) continue;
                            var candMd = NodePath.Join(cand, "SKILL.md");
                            if (!Fs.Exists(candMd)) continue;
                            if (SkillDiscovery.ParseSkillMd(candMd, false) is { } cs && cs.Name == skill.Name)
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                    if (found) installedAgents.Add(at);
                }
                var ex = Find(scopeKey, skill.Name);
                if (ex != null)
                {
                    foreach (var at in installedAgents)
                        if (!ex.Agents.Contains(at)) ex.Agents.Add(at);
                }
                else
                {
                    skills.Add(new InstalledSkill(skill.Name, skill.Description, skillDir, skillDir, scopeKey, installedAgents));
                }
            }
        }
        return skills;
    }
}
