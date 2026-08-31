using System.Globalization;
using System.Text.RegularExpressions;

namespace UniGetUI.Core.Tools
{
    /// <summary>
    /// PEP 440 version ordering, as implemented by Python's packaging library.
    /// Python spells a pre-release without a dash ("1.0.0rc1") and it is OLDER than the release
    /// it precedes, while a bare trailing dash-number ("1.0.0-1") is an implicit post-release and
    /// so is NEWER. Neither <see cref="SemanticVersion"/> nor
    /// <see cref="CoreTools.VersionStringToStruct"/> can order those correctly.
    /// </summary>
    public readonly partial struct PythonVersion
        : IComparable<PythonVersion>,
            IEquatable<PythonVersion>
    {
        private const int PreCategoryDevOnly = -1;
        private const int PreCategoryPresent = 0;
        private const int PreCategoryAbsent = 1;

        private static readonly int[] NoRelease = [];

        private readonly int _epoch;
        private readonly int[] _release;
        private readonly int _preCategory;
        private readonly int _preRank;
        private readonly int _preNumber;
        private readonly int? _post;
        private readonly int? _dev;
        private readonly string[]? _local;

        public string Original { get; }
        public bool IsValid { get; }

        public bool IsPreRelease =>
            IsValid && (_preCategory is PreCategoryPresent || _dev is not null);

        private PythonVersion(
            string original,
            int epoch,
            int[] release,
            int preCategory,
            int preRank,
            int preNumber,
            int? post,
            int? dev,
            string[]? local
        )
        {
            Original = original;
            IsValid = true;
            _epoch = epoch;
            _release = release;
            _preCategory = preCategory;
            _preRank = preRank;
            _preNumber = preNumber;
            _post = post;
            _dev = dev;
            _local = local;
        }

        [GeneratedRegex(
            @"^v?(?:(?:(?<epoch>[0-9]+)!)?(?<release>[0-9]+(?:\.[0-9]+)*)"
                + @"(?:[-_\.]?(?<pre_l>alpha|a|beta|b|preview|pre|c|rc)[-_\.]?(?<pre_n>[0-9]+)?)?"
                + @"(?:(?:-(?<post_n1>[0-9]+))|(?:[-_\.]?(?<post_l>post|rev|r)[-_\.]?(?<post_n2>[0-9]+)?))?"
                + @"(?<dev>[-_\.]?dev[-_\.]?(?<dev_n>[0-9]+)?)?)"
                + @"(?:\+(?<local>[a-z0-9]+(?:[-_\.][a-z0-9]+)*))?$",
            RegexOptions.CultureInvariant
        )]
        private static partial Regex Pep440Pattern();

        public static bool TryParse(string? value, out PythonVersion version)
        {
            version = default;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string candidate = value.Trim().ToLowerInvariant();
            Match match = Pep440Pattern().Match(candidate);
            if (!match.Success)
                return false;

            int epoch = ParseNumber(match.Groups["epoch"], 0);

            string[] releaseParts = match.Groups["release"].Value.Split('.');
            int[] release = new int[releaseParts.Length];
            for (int i = 0; i < releaseParts.Length; i++)
            {
                if (
                    !int.TryParse(
                        releaseParts[i],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out release[i]
                    )
                )
                    return false;
            }

            int preCategory = PreCategoryAbsent;
            int preRank = 0;
            int preNumber = 0;
            if (match.Groups["pre_l"].Success)
            {
                preCategory = PreCategoryPresent;
                preRank = match.Groups["pre_l"].Value switch
                {
                    "a" or "alpha" => 0,
                    "b" or "beta" => 1,
                    _ => 2,
                };
                preNumber = ParseNumber(match.Groups["pre_n"], 0);
            }

            int? post = null;
            if (match.Groups["post_n1"].Success)
                post = ParseNumber(match.Groups["post_n1"], 0);
            else if (match.Groups["post_l"].Success)
                post = ParseNumber(match.Groups["post_n2"], 0);

            int? dev = match.Groups["dev"].Success
                ? ParseNumber(match.Groups["dev_n"], 0)
                : null;

            string[]? local = match.Groups["local"].Success
                ? match.Groups["local"].Value.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                : null;

            if (preCategory is PreCategoryAbsent && post is null && dev is not null)
                preCategory = PreCategoryDevOnly;

            version = new PythonVersion(
                value,
                epoch,
                StripTrailingZeros(release),
                preCategory,
                preRank,
                preNumber,
                post,
                dev,
                local
            );
            return true;
        }

        private static int ParseNumber(Group group, int fallback) =>
            group.Success
            && int.TryParse(
                group.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed
            )
                ? parsed
                : fallback;

        private static int[] StripTrailingZeros(int[] release)
        {
            int length = release.Length;
            while (length > 0 && release[length - 1] is 0)
                length--;

            if (length == release.Length)
                return release;
            if (length is 0)
                return NoRelease;

            int[] trimmed = new int[length];
            Array.Copy(release, trimmed, length);
            return trimmed;
        }

        public int CompareTo(PythonVersion other)
        {
            if (!IsValid || !other.IsValid)
                return IsValid.CompareTo(other.IsValid);

            int comparison = _epoch.CompareTo(other._epoch);
            if (comparison is not 0)
                return comparison;

            comparison = CompareRelease(_release, other._release);
            if (comparison is not 0)
                return comparison;

            comparison = _preCategory.CompareTo(other._preCategory);
            if (comparison is not 0)
                return comparison;

            if (_preCategory is PreCategoryPresent)
            {
                comparison = _preRank.CompareTo(other._preRank);
                if (comparison is not 0)
                    return comparison;

                comparison = _preNumber.CompareTo(other._preNumber);
                if (comparison is not 0)
                    return comparison;
            }

            comparison = CompareLowestFirst(_post, other._post);
            if (comparison is not 0)
                return comparison;

            comparison = CompareHighestWhenAbsent(_dev, other._dev);
            if (comparison is not 0)
                return comparison;

            return CompareLocal(_local, other._local);
        }

        private static int CompareRelease(int[] left, int[] right)
        {
            int length = Math.Max(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                int a = i < left.Length ? left[i] : 0;
                int b = i < right.Length ? right[i] : 0;
                int comparison = a.CompareTo(b);
                if (comparison is not 0)
                    return comparison;
            }

            return 0;
        }

        private static int CompareLowestFirst(int? left, int? right) =>
            (left, right) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                _ => left.Value.CompareTo(right.Value),
            };

        private static int CompareHighestWhenAbsent(int? left, int? right) =>
            (left, right) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                _ => left.Value.CompareTo(right.Value),
            };

        private static int CompareLocal(string[]? left, string[]? right)
        {
            if (left is null && right is null)
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            int length = Math.Max(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                if (i >= left.Length)
                    return -1;
                if (i >= right.Length)
                    return 1;

                bool leftNumeric = int.TryParse(
                    left[i],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int leftValue
                );
                bool rightNumeric = int.TryParse(
                    right[i],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int rightValue
                );

                if (leftNumeric && rightNumeric)
                {
                    int comparison = leftValue.CompareTo(rightValue);
                    if (comparison is not 0)
                        return comparison;
                }
                else if (leftNumeric)
                {
                    return 1;
                }
                else if (rightNumeric)
                {
                    return -1;
                }
                else
                {
                    int comparison = string.CompareOrdinal(left[i], right[i]);
                    if (comparison is not 0)
                        return comparison;
                }
            }

            return 0;
        }

        public bool Equals(PythonVersion other) => CompareTo(other) is 0;

        public override bool Equals(object? obj) => obj is PythonVersion other && Equals(other);

        public override int GetHashCode() =>
            IsValid ? HashCode.Combine(_epoch, _release.Length, _preCategory, _post, _dev) : 0;

        public override string ToString() => Original ?? string.Empty;

        public static bool operator >(PythonVersion left, PythonVersion right) =>
            left.CompareTo(right) > 0;

        public static bool operator <(PythonVersion left, PythonVersion right) =>
            left.CompareTo(right) < 0;

        public static bool operator >=(PythonVersion left, PythonVersion right) =>
            left.CompareTo(right) >= 0;

        public static bool operator <=(PythonVersion left, PythonVersion right) =>
            left.CompareTo(right) <= 0;

        public static bool operator ==(PythonVersion left, PythonVersion right) =>
            left.Equals(right);

        public static bool operator !=(PythonVersion left, PythonVersion right) =>
            !left.Equals(right);
    }
}
