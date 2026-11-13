namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>
/// Resolves Apple's numeric StoreFront identifiers (from <c>ISAccountService</c>) and ISO 3166-1
/// alpha-2 country/territory codes (ported from MIT-licensed <c>mas-cli/mas</c> <c>Region.swift</c>)
/// to the two-letter storefront country code accepted by <c>itunes.apple.com</c>.
/// </summary>
internal static class AppStoreRegion
{
    public static string? FromStoreFrontHeader(string? storeFront)
    {
        if (string.IsNullOrWhiteSpace(storeFront))
            return null;

        string trimmed = storeFront.Trim();
        if (trimmed.Length == 2 && trimmed.All(char.IsAsciiLetter))
            return NormalizeTerritory(trimmed.ToUpperInvariant());

        int end = 0;
        while (end < trimmed.Length && char.IsAsciiDigit(trimmed[end]))
            end++;

        if (end == 0)
            return null;

        return StoreFrontIds.TryGetValue(trimmed[..end], out string? country)
            ? NormalizeTerritory(country)
            : null;
    }

    public static string NormalizeTerritory(string? isoRegion)
    {
        if (isoRegion is not { Length: 2 } || !isoRegion.All(char.IsAsciiLetter))
            return "US";

        string code = isoRegion.ToUpperInvariant();
        if (code == "IV")
            return "US";

        return code switch
        {
            "AD" => "ES", // Andorra -> Spain
            "AQ" => "NO", // Antarctica -> Norway
            "AS" => "US", // American Samoa -> United States
            "AW" => "NL", // Aruba -> Netherlands
            "AX" => "FI", // Aland Islands -> Finland
            "BD" => "IN", // Bangladesh -> India
            "BI" => "KE", // Burundi -> Kenya
            "BL" => "FR", // St. Barthelemy -> France
            "BQ" => "NL", // Caribbean Netherlands -> Netherlands
            "BV" => "NO", // Bouvet Island -> Norway
            "CC" => "AU", // Cocos (Keeling) Islands -> Australia
            "CF" => "FR", // Central African Republic -> France
            "CK" => "NZ", // Cook Islands -> New Zealand
            "CU" => "US", // Cuba -> United States
            "CW" => "NL", // Curacao -> Netherlands
            "CX" => "AU", // Christmas Island -> Australia
            "DJ" => "FR", // Djibouti -> France
            "EH" => "MA", // Western Sahara -> Morocco
            "ER" => "KE", // Eritrea -> Kenya
            "ET" => "KE", // Ethiopia -> Kenya
            "FK" => "GB", // Falkland Islands -> United Kingdom
            "FO" => "DK", // Faroe Islands -> Denmark
            "GF" => "FR", // French Guiana -> France
            "GG" => "GB", // Guernsey -> United Kingdom
            "GI" => "GB", // Gibraltar -> United Kingdom
            "GL" => "DK", // Greenland -> Denmark
            "GN" => "FR", // Guinea -> France
            "GP" => "FR", // Guadeloupe -> France
            "GQ" => "FR", // Equatorial Guinea -> France
            "GS" => "GB", // South Georgia & South Sandwich Islands -> United Kingdom
            "GU" => "US", // Guam -> United States
            "HM" => "AU", // Heard & McDonald Islands -> Australia
            "HT" => "US", // Haiti -> United States
            "IC" => "ES", // Canary Islands -> Spain
            "IM" => "GB", // Isle of Man -> United Kingdom
            "IO" => "GB", // British Indian Ocean Territory -> United Kingdom
            "IR" => "TR", // Iran -> Turkiye
            "JE" => "GB", // Jersey -> United Kingdom
            "KI" => "AU", // Kiribati -> Australia
            "KM" => "FR", // Comoros -> France
            "KP" => "CN", // North Korea -> China
            "LI" => "CH", // Liechtenstein -> Switzerland
            "LS" => "ZA", // Lesotho -> South Africa
            "MC" => "FR", // Monaco -> France
            "MF" => "FR", // St. Martin -> France
            "MH" => "US", // Marshall Islands -> United States
            "MP" => "US", // Northern Mariana Islands -> United States
            "MQ" => "FR", // Martinique -> France
            "NC" => "FR", // New Caledonia -> France
            "NF" => "AU", // Norfolk Island -> Australia
            "NU" => "NZ", // Niue -> New Zealand
            "PF" => "FR", // French Polynesia -> France
            "PM" => "FR", // St. Pierre & Miquelon -> France
            "PN" => "NZ", // Pitcairn Islands -> New Zealand
            "PR" => "US", // Puerto Rico -> United States
            "PS" => "IL", // Palestine -> Israel
            "RE" => "FR", // Reunion -> France
            "SD" => "EG", // Sudan -> Egypt
            "SH" => "GB", // St. Helena -> United Kingdom
            "SJ" => "NO", // Svalbard & Jan Mayen -> Norway
            "SM" => "IT", // San Marino -> Italy
            "SO" => "KE", // Somalia -> Kenya
            "SS" => "KE", // South Sudan -> Kenya
            "SX" => "NL", // Sint Maarten -> Netherlands
            "SY" => "TR", // Syria -> Turkiye
            "TF" => "FR", // French Southern Territories -> France
            "TG" => "FR", // Togo -> France
            "TK" => "NZ", // Tokelau -> New Zealand
            "TL" => "ID", // Timor-Leste -> Indonesia
            "TV" => "AU", // Tuvalu -> Australia
            "UM" => "US", // U.S. Outlying Islands -> United States
            "VA" => "IT", // Vatican City -> Italy
            "VI" => "US", // U.S. Virgin Islands -> United States
            "WF" => "FR", // Wallis & Futuna -> France
            "WS" => "AU", // Samoa -> Australia
            "YT" => "FR", // Mayotte -> France
            _ => code,
        };
    }

    // Apple App Store numeric storefront IDs returned by StoreFoundation's ISAccountService.
    private static readonly Dictionary<string, string> StoreFrontIds = new(StringComparer.Ordinal)
    {
        ["143441"] = "US",
        ["143442"] = "FR",
        ["143443"] = "DE",
        ["143444"] = "GB",
        ["143445"] = "AT",
        ["143446"] = "BE",
        ["143447"] = "FI",
        ["143448"] = "GR",
        ["143449"] = "IE",
        ["143450"] = "IT",
        ["143451"] = "LU",
        ["143452"] = "NL",
        ["143453"] = "PT",
        ["143454"] = "ES",
        ["143455"] = "CA",
        ["143456"] = "SE",
        ["143457"] = "NO",
        ["143458"] = "DK",
        ["143459"] = "CH",
        ["143460"] = "AU",
        ["143461"] = "NZ",
        ["143462"] = "JP",
        ["143463"] = "HK",
        ["143464"] = "SG",
        ["143465"] = "CN",
        ["143466"] = "KR",
        ["143467"] = "IN",
        ["143468"] = "MX",
        ["143469"] = "RU",
        ["143470"] = "TW",
        ["143471"] = "VN",
        ["143472"] = "ZA",
        ["143473"] = "MY",
        ["143474"] = "PH",
        ["143475"] = "TH",
        ["143476"] = "ID",
        ["143477"] = "PK",
        ["143478"] = "PL",
        ["143479"] = "SI",
        ["143480"] = "TR",
        ["143481"] = "AE",
        ["143482"] = "HU",
        ["143483"] = "CL",
        ["143484"] = "NP",
        ["143485"] = "PA",
        ["143486"] = "LK",
        ["143487"] = "RO",
        ["143489"] = "CZ",
        ["143491"] = "IL",
        ["143492"] = "UA",
        ["143493"] = "KW",
        ["143494"] = "HR",
        ["143495"] = "CR",
        ["143496"] = "SK",
        ["143497"] = "LB",
        ["143498"] = "QA",
        ["143499"] = "SI",
        ["143501"] = "CO",
        ["143502"] = "SV",
        ["143503"] = "BR",
        ["143504"] = "GT",
        ["143505"] = "AR",
        ["143506"] = "DO",
        ["143507"] = "PE",
        ["143508"] = "DO",
        ["143509"] = "EC",
        ["143510"] = "HN",
        ["143511"] = "JM",
        ["143512"] = "NI",
        ["143513"] = "PY",
        ["143514"] = "UY",
        ["143515"] = "MO",
        ["143516"] = "EG",
        ["143517"] = "KZ",
        ["143518"] = "EE",
        ["143519"] = "LV",
        ["143520"] = "LT",
        ["143521"] = "MT",
        ["143523"] = "MD",
        ["143524"] = "AM",
        ["143525"] = "BW",
        ["143526"] = "BG",
        ["143528"] = "JO",
        ["143529"] = "KE",
        ["143530"] = "MK",
        ["143531"] = "MG",
        ["143532"] = "ML",
        ["143533"] = "MU",
        ["143534"] = "NE",
        ["143535"] = "SN",
        ["143536"] = "TN",
        ["143537"] = "UG",
        ["143557"] = "CY",
        ["143559"] = "BH",
        ["143560"] = "IS",
        ["143561"] = "NG",
        ["143562"] = "OM",
        ["143563"] = "DZ",
        ["143564"] = "AO",
        ["143565"] = "AI",
        ["143566"] = "AZ",
        ["143568"] = "BY",
        ["143571"] = "BO",
        ["143573"] = "GH",
        ["143575"] = "GY",
        ["143576"] = "IS",
        ["143577"] = "UZ",
        ["143579"] = "SA",
        ["143580"] = "MA",
        ["143600"] = "RS",
        ["143610"] = "GE",
    };
}
