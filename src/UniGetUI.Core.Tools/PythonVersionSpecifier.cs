using System.Text.RegularExpressions;

namespace UniGetUI.Core.Tools
{
    public readonly partial struct PythonVersionSpecifier
    {
        private readonly Clause[]? _clauses;

        public bool IsValid { get; }

        private PythonVersionSpecifier(Clause[] clauses)
        {
            _clauses = clauses;
            IsValid = true;
        }

        private readonly record struct Clause(
            string Operator,
            string RawVersion,
            bool Wildcard,
            PythonVersion Version,
            int[]? Release
        );

        [GeneratedRegex(
            @"^(?<op>===|==|!=|~=|<=|>=|<|>)\s*(?<version>\S+)$",
            RegexOptions.CultureInvariant
        )]
        private static partial Regex ClausePattern();

        [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)*$", RegexOptions.CultureInvariant)]
        private static partial Regex PlainReleasePattern();

        public static bool TryParse(string? text, out PythonVersionSpecifier specifier)
        {
            specifier = default;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] parts = text.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (parts.Length is 0)
                return false;

            Clause[] clauses = new Clause[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryParseClause(parts[i], out clauses[i]))
                    return false;
            }

            specifier = new PythonVersionSpecifier(clauses);
            return true;
        }

        private static bool TryParseClause(string text, out Clause clause)
        {
            clause = default;

            Match match = ClausePattern().Match(text);
            if (!match.Success)
                return false;

            string op = match.Groups["op"].Value;
            string rawVersion = match.Groups["version"].Value;
            bool wildcard = rawVersion.EndsWith(".*", StringComparison.Ordinal);
            string bareVersion = wildcard ? rawVersion[..^2] : rawVersion;

            if (wildcard && op is not ("==" or "!="))
                return false;

            int[]? release = ParseRelease(bareVersion);

            if (wildcard && release is null)
                return false;

            if (op is "~=" && (release is null || release.Length < 2))
                return false;

            if (op is "===")
            {
                clause = new Clause(op, rawVersion, false, default, null);
                return true;
            }

            if (!PythonVersion.TryParse(bareVersion, out PythonVersion version))
                return false;

            clause = new Clause(op, rawVersion, wildcard, version, release);
            return true;
        }

        private static int[]? ParseRelease(string value)
        {
            if (!PlainReleasePattern().IsMatch(value))
                return null;

            string[] parts = value.Split('.');
            int[] release = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out release[i]))
                    return null;
            }

            return release;
        }

        public bool IsSatisfiedBy(PythonVersion version)
        {
            if (!IsValid || _clauses is null || !version.IsValid)
                return false;

            foreach (Clause clause in _clauses)
            {
                if (!Satisfies(clause, version))
                    return false;
            }

            return true;
        }

        private static bool Satisfies(Clause clause, PythonVersion version) =>
            clause.Operator switch
            {
                "===" => string.Equals(
                    version.Original,
                    clause.RawVersion,
                    StringComparison.Ordinal
                ),
                "==" => clause.Wildcard
                    ? StartsWithRelease(version, clause.Release!)
                    : version.CompareTo(clause.Version) is 0,
                "!=" => clause.Wildcard
                    ? !StartsWithRelease(version, clause.Release!)
                    : version.CompareTo(clause.Version) is not 0,
                "<=" => version.CompareTo(clause.Version) <= 0,
                ">=" => version.CompareTo(clause.Version) >= 0,
                "<" => version.CompareTo(clause.Version) < 0,
                ">" => version.CompareTo(clause.Version) > 0,
                "~=" => version.CompareTo(clause.Version) >= 0
                    && StartsWithRelease(version, clause.Release![..^1]),
                _ => false,
            };

        private static bool StartsWithRelease(PythonVersion version, int[] prefix)
        {
            IReadOnlyList<int> release = version.ReleaseComponents;
            for (int i = 0; i < prefix.Length; i++)
            {
                int component = i < release.Count ? release[i] : 0;
                if (component != prefix[i])
                    return false;
            }

            return true;
        }
    }
}
