namespace Dissimilis.MrzScanner.Internal;

/// <summary>
/// Codes valid in MRZ issuing state and nationality fields: ISO 3166-1 alpha-3
/// plus the ICAO 9303 special codes (D for Germany, GBD..GBS, UN organizations,
/// stateless and refugee codes). Used as a recognition prior, since no check
/// digit protects these fields.
/// </summary>
internal static class CountryCodes
{
    /// <summary>All codes padded to three characters with fillers (Germany is "D&lt;&lt;").</summary>
    public static readonly string[] All =
    {
        "AFG", "ALB", "DZA", "ASM", "AND", "AGO", "AIA", "ATA", "ATG", "ARG",
        "ARM", "ABW", "AUS", "AUT", "AZE", "BHS", "BHR", "BGD", "BRB", "BLR",
        "BEL", "BLZ", "BEN", "BMU", "BTN", "BOL", "BES", "BIH", "BWA", "BVT",
        "BRA", "IOT", "BRN", "BGR", "BFA", "BDI", "CPV", "KHM", "CMR", "CAN",
        "CYM", "CAF", "TCD", "CHL", "CHN", "CXR", "CCK", "COL", "COM", "COG",
        "COD", "COK", "CRI", "CIV", "HRV", "CUB", "CUW", "CYP", "CZE", "DNK",
        "DJI", "DMA", "DOM", "ECU", "EGY", "SLV", "GNQ", "ERI", "EST", "SWZ",
        "ETH", "FLK", "FRO", "FJI", "FIN", "FRA", "GUF", "PYF", "ATF", "GAB",
        "GMB", "GEO", "D<<", "DEU", "GHA", "GIB", "GRC", "GRL", "GRD", "GLP",
        "GUM", "GTM", "GGY", "GIN", "GNB", "GUY", "HTI", "HMD", "VAT", "HND",
        "HKG", "HUN", "ISL", "IND", "IDN", "IRN", "IRQ", "IRL", "IMN", "ISR",
        "ITA", "JAM", "JPN", "JEY", "JOR", "KAZ", "KEN", "KIR", "PRK", "KOR",
        "KWT", "KGZ", "LAO", "LVA", "LBN", "LSO", "LBR", "LBY", "LIE", "LTU",
        "LUX", "MAC", "MDG", "MWI", "MYS", "MDV", "MLI", "MLT", "MHL", "MTQ",
        "MRT", "MUS", "MYT", "MEX", "FSM", "MDA", "MCO", "MNG", "MNE", "MSR",
        "MAR", "MOZ", "MMR", "NAM", "NRU", "NPL", "NLD", "NCL", "NZL", "NIC",
        "NER", "NGA", "NIU", "NFK", "MKD", "MNP", "NOR", "OMN", "PAK", "PLW",
        "PSE", "PAN", "PNG", "PRY", "PER", "PHL", "PCN", "POL", "PRT", "PRI",
        "QAT", "REU", "ROU", "RUS", "RWA", "BLM", "SHN", "KNA", "LCA", "MAF",
        "SPM", "VCT", "WSM", "SMR", "STP", "SAU", "SEN", "SRB", "SYC", "SLE",
        "SGP", "SXM", "SVK", "SVN", "SLB", "SOM", "ZAF", "SGS", "SSD", "ESP",
        "LKA", "SDN", "SUR", "SJM", "SWE", "CHE", "SYR", "TWN", "TJK", "TZA",
        "THA", "TLS", "TGO", "TKL", "TON", "TTO", "TUN", "TUR", "TKM", "TCA",
        "TUV", "UGA", "UKR", "ARE", "GBR", "USA", "UMI", "URY", "UZB", "VUT",
        "VEN", "VNM", "VGB", "VIR", "WLF", "ESH", "YEM", "ZMB", "ZWE",
        // ICAO 9303 special codes.
        "GBD", "GBN", "GBO", "GBP", "GBS", "RKS", "EUE", "UNO", "UNA", "UNK",
        "XBA", "XIM", "XCC", "XCO", "XEC", "XPO", "XOM", "XXA", "XXB", "XXC",
        "XXX", "ANT", "NTZ", "UTO",
    };

    private static readonly HashSet<string> Lookup = new(All, StringComparer.Ordinal);

    public static bool IsValid(string code) => Lookup.Contains(code);
}
