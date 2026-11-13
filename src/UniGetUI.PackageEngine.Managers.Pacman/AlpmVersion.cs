namespace UniGetUI.PackageEngine.Managers.PacmanManager;

public static class AlpmVersion
{
    public static int Compare(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 0;

        var (epochA, versionA, releaseA) = Parse(a);
        var (epochB, versionB, releaseB) = Parse(b);

        int result = RpmVerCmp(epochA, epochB);
        if (result != 0)
            return result;

        result = RpmVerCmp(versionA, versionB);
        if (result == 0 && releaseA is not null && releaseB is not null)
            result = RpmVerCmp(releaseA, releaseB);
        return result;
    }

    private static (string Epoch, string Version, string? Release) Parse(string evr)
    {
        int s = 0;
        while (s < evr.Length && char.IsAsciiDigit(evr[s]))
            s++;

        int releaseSeparator = evr.LastIndexOf('-');
        if (releaseSeparator < s)
            releaseSeparator = -1;

        string epoch = "0";
        int versionStart = 0;
        if (s < evr.Length && evr[s] == ':')
        {
            if (s > 0)
                epoch = evr[..s];
            versionStart = s + 1;
        }

        string version = releaseSeparator >= 0
            ? evr[versionStart..releaseSeparator]
            : evr[versionStart..];
        string? release = releaseSeparator >= 0 ? evr[(releaseSeparator + 1)..] : null;
        return (epoch, version, release);
    }

    private static int RpmVerCmp(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 0;

        int one = 0, two = 0;
        int ptr1 = 0, ptr2 = 0;

        while (one < a.Length && two < b.Length)
        {
            while (one < a.Length && !char.IsAsciiLetterOrDigit(a[one]))
                one++;
            while (two < b.Length && !char.IsAsciiLetterOrDigit(b[two]))
                two++;

            if (one >= a.Length || two >= b.Length)
                break;

            if (one - ptr1 != two - ptr2)
                return one - ptr1 < two - ptr2 ? -1 : 1;

            ptr1 = one;
            ptr2 = two;

            bool isNumber = char.IsAsciiDigit(a[ptr1]);
            if (isNumber)
            {
                while (ptr1 < a.Length && char.IsAsciiDigit(a[ptr1]))
                    ptr1++;
                while (ptr2 < b.Length && char.IsAsciiDigit(b[ptr2]))
                    ptr2++;
            }
            else
            {
                while (ptr1 < a.Length && char.IsAsciiLetter(a[ptr1]))
                    ptr1++;
                while (ptr2 < b.Length && char.IsAsciiLetter(b[ptr2]))
                    ptr2++;
            }

            if (two == ptr2)
                return isNumber ? 1 : -1;

            string segmentA = a[one..ptr1];
            string segmentB = b[two..ptr2];

            if (isNumber)
            {
                segmentA = segmentA.TrimStart('0');
                segmentB = segmentB.TrimStart('0');
                if (segmentA.Length != segmentB.Length)
                    return segmentA.Length > segmentB.Length ? 1 : -1;
            }

            int comparison = string.CompareOrdinal(segmentA, segmentB);
            if (comparison != 0)
                return comparison < 0 ? -1 : 1;

            one = ptr1;
            two = ptr2;
        }

        bool endA = one >= a.Length;
        bool endB = two >= b.Length;
        if (endA && endB)
            return 0;

        return (endA && !char.IsAsciiLetter(b[two])) || (!endA && char.IsAsciiLetter(a[one])) ? -1 : 1;
    }
}
