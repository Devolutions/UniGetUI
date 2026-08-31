using System.Globalization;

namespace UniGetUI.Core.Tools
{
    /// <summary>
    /// Semantic Versioning 2.0 ordering, with NuGet's fourth numeric segment allowed.
    /// Use it only for ecosystems where a dash introduces a pre-release that is OLDER than the
    /// release it precedes (NuGet, npm, crates.io). Ecosystems whose dash, underscore or hash
    /// introduces a build or port revision that is NEWER than the base version - Debian, Scoop,
    /// Homebrew, vcpkg - must keep using <see cref="CoreTools.VersionStringToStruct"/> instead.
    /// </summary>
    public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        private static readonly int[] EmptyNumbers = [0, 0, 0, 0];
        private static readonly string[] NoLabels = [];

        private readonly int[] _numbers;
        private readonly string[] _labels;

        public string Original { get; }
        public bool IsValid { get; }
        public bool IsPreRelease => _labels is { Length: > 0 };

        private SemanticVersion(string original, int[] numbers, string[] labels, bool isValid)
        {
            Original = original;
            _numbers = numbers;
            _labels = labels;
            IsValid = isValid;
        }

        public static SemanticVersion Invalid(string original) =>
            new(original, EmptyNumbers, NoLabels, false);

        public static bool TryParse(string? value, out SemanticVersion version)
        {
            version = Invalid(value ?? string.Empty);

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string candidate = value.Trim();
            if (candidate.StartsWith('v') || candidate.StartsWith('V'))
                candidate = candidate[1..];

            int metadataIndex = candidate.IndexOf('+');
            if (metadataIndex >= 0)
                candidate = candidate[..metadataIndex];

            string[] labels = NoLabels;
            int labelIndex = candidate.IndexOf('-');
            if (labelIndex >= 0)
            {
                string labelPart = candidate[(labelIndex + 1)..];
                candidate = candidate[..labelIndex];
                labels = labelPart.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (labels.Length is 0)
                    labels = NoLabels;
            }

            string[] parts = candidate.Split('.');
            if (parts.Length is 0 or > 4)
                return false;

            int[] numbers = [0, 0, 0, 0];
            for (int i = 0; i < parts.Length; i++)
            {
                if (
                    !int.TryParse(
                        parts[i],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int number
                    )
                )
                    return false;

                numbers[i] = number;
            }

            version = new SemanticVersion(value, numbers, labels, true);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            if (!IsValid || !other.IsValid)
                return IsValid.CompareTo(other.IsValid);

            for (int i = 0; i < 4; i++)
            {
                int comparison = _numbers[i].CompareTo(other._numbers[i]);
                if (comparison is not 0)
                    return comparison;
            }

            if (_labels.Length is 0 || other._labels.Length is 0)
                return other._labels.Length.CompareTo(_labels.Length);

            int shared = Math.Min(_labels.Length, other._labels.Length);
            for (int i = 0; i < shared; i++)
            {
                int comparison = CompareLabel(_labels[i], other._labels[i]);
                if (comparison is not 0)
                    return comparison;
            }

            return _labels.Length.CompareTo(other._labels.Length);
        }

        private static int CompareLabel(string left, string right)
        {
            bool leftNumeric = int.TryParse(
                left,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int leftValue
            );
            bool rightNumeric = int.TryParse(
                right,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int rightValue
            );

            if (leftNumeric && rightNumeric)
                return leftValue.CompareTo(rightValue);
            if (leftNumeric)
                return -1;
            if (rightNumeric)
                return 1;

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public bool Equals(SemanticVersion other) => CompareTo(other) is 0;

        public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

        public override int GetHashCode() =>
            IsValid
                ? HashCode.Combine(_numbers[0], _numbers[1], _numbers[2], _numbers[3], _labels.Length)
                : 0;

        public override string ToString() => Original ?? string.Empty;

        public static bool operator >(SemanticVersion left, SemanticVersion right) =>
            left.CompareTo(right) > 0;

        public static bool operator <(SemanticVersion left, SemanticVersion right) =>
            left.CompareTo(right) < 0;

        public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
            left.CompareTo(right) >= 0;

        public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
            left.CompareTo(right) <= 0;

        public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

        public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
    }
}
