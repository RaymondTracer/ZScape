namespace ZScape.Utilities;

/// <summary>
/// Represents a country with its ISO 3166-1 alpha-2 code and name.
/// </summary>
public record CountryItem(string Code, string Name)
{
    public override string ToString() => $"{Name} ({Code})";
}

/// <summary>
/// Static data containing ISO 3166-1 alpha-2 country codes.
/// </summary>
public static class CountryData
{
    public static readonly CountryItem[] Countries =
    [
        // Special codes - shown at top for easy filtering
        new("??", "[Unknown/Unresolved]"),
        new("A1", "[Anonymous Proxy]"),
        new("A2", "[Satellite Provider]"),
        new("AP", "[Asia/Pacific Region]"),
        new("EU", "[Europe Region]"),
        // Standard ISO 3166-1 alpha-2 codes
        new("AF", "Afghanistan"),
        new("AL", "Albania"),
        new("DZ", "Algeria"),
        new("AD", "Andorra"),
        new("AO", "Angola"),
        new("AR", "Argentina"),
        new("AM", "Armenia"),
        new("AU", "Australia"),
        new("AT", "Austria"),
        new("AZ", "Azerbaijan"),
        new("BS", "Bahamas"),
        new("BH", "Bahrain"),
        new("BD", "Bangladesh"),
        new("BY", "Belarus"),
        new("BE", "Belgium"),
        new("BZ", "Belize"),
        new("BO", "Bolivia"),
        new("BA", "Bosnia and Herzegovina"),
        new("BR", "Brazil"),
        new("BN", "Brunei"),
        new("BG", "Bulgaria"),
        new("KH", "Cambodia"),
        new("CM", "Cameroon"),
        new("CA", "Canada"),
        new("CL", "Chile"),
        new("CN", "China"),
        new("CO", "Colombia"),
        new("CR", "Costa Rica"),
        new("HR", "Croatia"),
        new("CU", "Cuba"),
        new("CY", "Cyprus"),
        new("CZ", "Czechia"),
        new("DK", "Denmark"),
        new("DO", "Dominican Republic"),
        new("EC", "Ecuador"),
        new("EG", "Egypt"),
        new("SV", "El Salvador"),
        new("EE", "Estonia"),
        new("ET", "Ethiopia"),
        new("FI", "Finland"),
        new("FR", "France"),
        new("GE", "Georgia"),
        new("DE", "Germany"),
        new("GH", "Ghana"),
        new("GR", "Greece"),
        new("GT", "Guatemala"),
        new("HN", "Honduras"),
        new("HK", "Hong Kong"),
        new("HU", "Hungary"),
        new("IS", "Iceland"),
        new("IN", "India"),
        new("ID", "Indonesia"),
        new("IR", "Iran"),
        new("IQ", "Iraq"),
        new("IE", "Ireland"),
        new("IL", "Israel"),
        new("IT", "Italy"),
        new("JM", "Jamaica"),
        new("JP", "Japan"),
        new("JO", "Jordan"),
        new("KZ", "Kazakhstan"),
        new("KE", "Kenya"),
        new("KR", "Korea, South"),
        new("KW", "Kuwait"),
        new("KG", "Kyrgyzstan"),
        new("LA", "Laos"),
        new("LV", "Latvia"),
        new("LB", "Lebanon"),
        new("LY", "Libya"),
        new("LI", "Liechtenstein"),
        new("LT", "Lithuania"),
        new("LU", "Luxembourg"),
        new("MO", "Macau"),
        new("MY", "Malaysia"),
        new("MT", "Malta"),
        new("MX", "Mexico"),
        new("MD", "Moldova"),
        new("MC", "Monaco"),
        new("MN", "Mongolia"),
        new("ME", "Montenegro"),
        new("MA", "Morocco"),
        new("MM", "Myanmar"),
        new("NP", "Nepal"),
        new("NL", "Netherlands"),
        new("NZ", "New Zealand"),
        new("NI", "Nicaragua"),
        new("NG", "Nigeria"),
        new("MK", "North Macedonia"),
        new("NO", "Norway"),
        new("OM", "Oman"),
        new("PK", "Pakistan"),
        new("PA", "Panama"),
        new("PY", "Paraguay"),
        new("PE", "Peru"),
        new("PH", "Philippines"),
        new("PL", "Poland"),
        new("PT", "Portugal"),
        new("PR", "Puerto Rico"),
        new("QA", "Qatar"),
        new("RO", "Romania"),
        new("RU", "Russia"),
        new("SA", "Saudi Arabia"),
        new("RS", "Serbia"),
        new("SG", "Singapore"),
        new("SK", "Slovakia"),
        new("SI", "Slovenia"),
        new("ZA", "South Africa"),
        new("ES", "Spain"),
        new("LK", "Sri Lanka"),
        new("SE", "Sweden"),
        new("CH", "Switzerland"),
        new("SY", "Syria"),
        new("TW", "Taiwan"),
        new("TH", "Thailand"),
        new("TN", "Tunisia"),
        new("TR", "Turkey"),
        new("UA", "Ukraine"),
        new("AE", "United Arab Emirates"),
        new("GB", "United Kingdom"),
        new("US", "United States"),
        new("UY", "Uruguay"),
        new("UZ", "Uzbekistan"),
        new("VE", "Venezuela"),
        new("VN", "Vietnam"),
        new("YE", "Yemen"),
        new("ZW", "Zimbabwe")
    ];

    /// <summary>
    /// Maps ISO 3166-1 alpha-3 codes to alpha-2 codes.
    /// Also includes special codes that should pass through unchanged.
    /// </summary>
    public static readonly Dictionary<string, string> Alpha3ToAlpha2 = new(StringComparer.OrdinalIgnoreCase)
    {
        // Special codes - normalize unknown variants to "??"
        ["XIP"] = "??", ["XUN"] = "??", ["O1"] = "??", ["A1"] = "A1", ["A2"] = "A2", ["AP"] = "AP", ["EU"] = "EU", ["??"] = "??",
        // Standard alpha-3 to alpha-2 mappings
        ["AFG"] = "AF", ["ALB"] = "AL", ["DZA"] = "DZ", ["AND"] = "AD", ["AGO"] = "AO",
        ["ARG"] = "AR", ["ARM"] = "AM", ["AUS"] = "AU", ["AUT"] = "AT", ["AZE"] = "AZ",
        ["BHS"] = "BS", ["BHR"] = "BH", ["BGD"] = "BD", ["BLR"] = "BY", ["BEL"] = "BE",
        ["BLZ"] = "BZ", ["BOL"] = "BO", ["BIH"] = "BA", ["BRA"] = "BR", ["BRN"] = "BN",
        ["BGR"] = "BG", ["KHM"] = "KH", ["CMR"] = "CM", ["CAN"] = "CA", ["CHL"] = "CL",
        ["CHN"] = "CN", ["COL"] = "CO", ["CRI"] = "CR", ["HRV"] = "HR", ["CUB"] = "CU",
        ["CYP"] = "CY", ["CZE"] = "CZ", ["DNK"] = "DK", ["DOM"] = "DO", ["ECU"] = "EC",
        ["EGY"] = "EG", ["SLV"] = "SV", ["EST"] = "EE", ["ETH"] = "ET", ["FIN"] = "FI",
        ["FRA"] = "FR", ["GEO"] = "GE", ["DEU"] = "DE", ["GHA"] = "GH", ["GRC"] = "GR",
        ["GTM"] = "GT", ["HND"] = "HN", ["HKG"] = "HK", ["HUN"] = "HU", ["ISL"] = "IS",
        ["IND"] = "IN", ["IDN"] = "ID", ["IRN"] = "IR", ["IRQ"] = "IQ", ["IRL"] = "IE",
        ["ISR"] = "IL", ["ITA"] = "IT", ["JAM"] = "JM", ["JPN"] = "JP", ["JOR"] = "JO",
        ["KAZ"] = "KZ", ["KEN"] = "KE", ["KOR"] = "KR", ["KWT"] = "KW", ["KGZ"] = "KG",
        ["LAO"] = "LA", ["LVA"] = "LV", ["LBN"] = "LB", ["LBY"] = "LY", ["LIE"] = "LI",
        ["LTU"] = "LT", ["LUX"] = "LU", ["MAC"] = "MO", ["MYS"] = "MY", ["MLT"] = "MT",
        ["MEX"] = "MX", ["MDA"] = "MD", ["MCO"] = "MC", ["MNG"] = "MN", ["MNE"] = "ME",
        ["MAR"] = "MA", ["MMR"] = "MM", ["NPL"] = "NP", ["NLD"] = "NL", ["NZL"] = "NZ",
        ["NIC"] = "NI", ["NGA"] = "NG", ["MKD"] = "MK", ["NOR"] = "NO", ["OMN"] = "OM",
        ["PAK"] = "PK", ["PAN"] = "PA", ["PRY"] = "PY", ["PER"] = "PE", ["PHL"] = "PH",
        ["POL"] = "PL", ["PRT"] = "PT", ["PRI"] = "PR", ["QAT"] = "QA", ["ROU"] = "RO",
        ["RUS"] = "RU", ["SAU"] = "SA", ["SRB"] = "RS", ["SGP"] = "SG", ["SVK"] = "SK",
        ["SVN"] = "SI", ["ZAF"] = "ZA", ["ESP"] = "ES", ["LKA"] = "LK", ["SWE"] = "SE",
        ["CHE"] = "CH", ["SYR"] = "SY", ["TWN"] = "TW", ["THA"] = "TH", ["TUN"] = "TN",
        ["TUR"] = "TR", ["UKR"] = "UA", ["ARE"] = "AE", ["GBR"] = "GB", ["USA"] = "US",
        ["URY"] = "UY", ["UZB"] = "UZ", ["VEN"] = "VE", ["VNM"] = "VN", ["YEM"] = "YE",
        ["ZWE"] = "ZW"
    };

    /// <summary>
    /// Normalizes a country code to alpha-2 format.
    /// Unknown variants (XIP, XUN, O1, empty) are normalized to "??".
    /// </summary>
    public static string NormalizeToAlpha2(string code)
    {
        if (string.IsNullOrEmpty(code)) return "??";
        
        var upper = code.ToUpperInvariant().Trim();
        if (string.IsNullOrEmpty(upper)) return "??";
        
        // Check for special/unknown codes first
        if (upper == "XIP" || upper == "XUN" || upper == "O1")
            return "??";
        
        // Already alpha-2
        if (upper.Length == 2) return upper;
        
        // Try to convert from alpha-3
        if (upper.Length == 3 && Alpha3ToAlpha2.TryGetValue(upper, out var alpha2))
            return alpha2;
        
        return upper;
    }
}
