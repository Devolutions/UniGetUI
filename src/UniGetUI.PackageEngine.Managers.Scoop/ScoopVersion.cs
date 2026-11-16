using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace UniGetUI.PackageEngine.Managers.ScoopManager;

internal static partial class ScoopVersion
{
    [GeneratedRegex("[a-zA-Z]+")]
    private static partial Regex LettersRegex();

    [GeneratedRegex("alpha|beta|rc|pre", RegexOptions.IgnoreCase)]
    private static partial Regex PreReleaseRegex();

    private readonly record struct Part(string Text, BigInteger? Number);

    public static int? Compare(string reference, string difference) => Compare(reference, difference, '-');

    private static int? Compare(string reference, string difference, char delimiter)
    {
        reference = reference.Replace('+', '-');
        difference = difference.Replace('+', '-');

        if (string.Equals(difference, reference, StringComparison.InvariantCultureIgnoreCase))
            return 0;

        Part[] referenceParts = Split(reference, delimiter);
        Part[] differenceParts = Split(difference, delimiter);

        if (
            referenceParts.Length > 0
            && differenceParts.Length > 0
            && IsNightly(referenceParts[0])
            && IsNightly(differenceParts[0])
        )
            return null;

        for (int i = 0; i < Math.Max(referenceParts.Length, differenceParts.Length); i++)
        {
            if (i >= referenceParts.Length)
                return PreReleaseRegex().IsMatch(differenceParts[i].Text) ? -1 : 1;
            if (i >= differenceParts.Length)
                return PreReleaseRegex().IsMatch(referenceParts[i].Text) ? 1 : -1;

            Part referencePart = referenceParts[i];
            Part differencePart = differenceParts[i];

            char? nested =
                referencePart.Text.Contains('.') || differencePart.Text.Contains('.') ? '.'
                : referencePart.Text.Contains('_') || differencePart.Text.Contains('_') ? '_'
                : null;
            if (nested is { } nestedDelimiter)
            {
                int? result = Compare(referencePart.Text, differencePart.Text, nestedDelimiter);
                if (result is not 0)
                    return result;
                continue;
            }

            int comparison =
                referencePart.Number is { } referenceNumber && differencePart.Number is { } differenceNumber
                    ? differenceNumber.CompareTo(referenceNumber)
                    : string.Compare(
                        differencePart.Text,
                        referencePart.Text,
                        CultureInfo.InvariantCulture,
                        CompareOptions.IgnoreCase
                    );
            if (comparison != 0)
                return Math.Sign(comparison);
        }

        return null;
    }

    private static bool IsNightly(Part part) =>
        string.Equals(part.Text, "nightly", StringComparison.InvariantCultureIgnoreCase);

    private static Part[] Split(string version, char delimiter)
    {
        string separated = LettersRegex().Replace(version, match => delimiter + match.Value + delimiter);
        return separated
            .Split(delimiter, StringSplitOptions.RemoveEmptyEntries)
            .Select(ToPart)
            .ToArray();
    }

    private static Part ToPart(string text)
    {
        if (!text.All(char.IsAsciiDigit))
            return new Part(text, null);

        BigInteger number = BigInteger.Parse(text, CultureInfo.InvariantCulture);
        return new Part(number.ToString(CultureInfo.InvariantCulture), number);
    }
}
