namespace AviatesAirTracker.Core.Reference;

/// <summary>
/// Maps an ICAO airport code to an ISO-3166 alpha-2 country code using the
/// ICAO region prefix (1–2 leading letters). No airport database required.
/// Longest matching prefix wins (e.g. "K" = US, but "EG" = GB before "E...").
/// </summary>
public static class IcaoCountry
{
    // Two-letter prefixes take priority over one-letter.
    private static readonly Dictionary<string, string> Two = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EG"]="GB", ["EI"]="IE", ["EH"]="NL", ["EB"]="BE", ["EL"]="LU", ["ED"]="DE", ["ET"]="DE",
        ["LF"]="FR", ["LE"]="ES", ["LP"]="PT", ["LI"]="IT", ["LS"]="CH", ["LO"]="AT", ["LK"]="CZ",
        ["LZ"]="SK", ["LH"]="HU", ["LG"]="GR", ["LT"]="TR", ["LM"]="MT", ["LC"]="CY", ["LR"]="RO",
        ["LB"]="BG", ["LD"]="HR", ["LJ"]="SI", ["LQ"]="BA", ["LY"]="RS", ["LW"]="MK", ["LA"]="AL",
        ["LU"]="MD", ["EK"]="DK", ["EN"]="NO", ["ES"]="SE", ["EF"]="FI", ["EV"]="LV",
        ["EE"]="EE", ["EY"]="LT", ["EP"]="PL", ["UK"]="UA", ["UM"]="BY", ["UB"]="AZ", ["UD"]="AM",
        ["UG"]="GE", ["OM"]="AE", ["OB"]="BH", ["OK"]="KW", ["OT"]="QA", ["OO"]="OM", ["OE"]="SA",
        ["OI"]="IR", ["OR"]="IQ", ["OJ"]="JO", ["OL"]="LB", ["OS"]="SY", ["OP"]="PK", ["OY"]="YE",
        ["VA"]="IN", ["VI"]="IN", ["VE"]="IN", ["VO"]="IN", ["VG"]="BD", ["VC"]="LK", ["VN"]="NP",
        ["VR"]="MV", ["VT"]="TH", ["VV"]="VN", ["VD"]="KH", ["VL"]="LA", ["VY"]="MM", ["VM"]="MO",
        ["VH"]="HK", ["WI"]="ID", ["WA"]="ID", ["WR"]="ID", ["WM"]="MY", ["WB"]="MY", ["WS"]="SG",
        ["WP"]="TL", ["RJ"]="JP", ["RO"]="JP", ["RK"]="KR", ["RC"]="TW", ["RP"]="PH", ["ZK"]="KP",
        ["ZM"]="MN", ["FA"]="ZA", ["FB"]="BW", ["FC"]="CG", ["FD"]="SZ", ["FE"]="CF", ["FG"]="GQ",
        ["FH"]="SH", ["FI"]="MU", ["FK"]="CM", ["FL"]="ZM", ["FM"]="MG", ["FN"]="AO", ["FO"]="GA",
        ["FP"]="ST", ["FQ"]="MZ", ["FS"]="SC", ["FT"]="TD", ["FV"]="ZW", ["FW"]="MW", ["FX"]="LS",
        ["FY"]="NA", ["FZ"]="CD", ["GA"]="ML", ["GB"]="GM", ["GC"]="ES", ["GE"]="ES", ["GF"]="SL",
        ["GG"]="GW", ["GL"]="LR", ["GM"]="MA", ["GO"]="SN", ["GQ"]="MR", ["GU"]="GN", ["GV"]="CV",
        ["DA"]="DZ", ["DB"]="BJ", ["DF"]="BF", ["DG"]="GH", ["DI"]="CI", ["DN"]="NG", ["DR"]="NE",
        ["DT"]="TN", ["DX"]="TG", ["HA"]="ET", ["HB"]="BI", ["HC"]="SO", ["HD"]="DJ", ["HE"]="EG",
        ["HH"]="ER", ["HK"]="KE", ["HL"]="LY", ["HR"]="RW", ["HS"]="SD", ["HT"]="TZ", ["HU"]="UG",
        ["SA"]="AR", ["SB"]="BR", ["SC"]="CL", ["SD"]="BR", ["SE"]="EC", ["SF"]="FK", ["SG"]="PY",
        ["SK"]="CO", ["SL"]="BO", ["SM"]="SR", ["SO"]="GF", ["SP"]="PE", ["SU"]="UY", ["SV"]="VE",
        ["SW"]="BR", ["SY"]="GY", ["MB"]="TC", ["MD"]="DO", ["MG"]="GT", ["MH"]="HN", ["MK"]="JM",
        ["MM"]="MX", ["MN"]="NI", ["MP"]="PA", ["MR"]="CR", ["MS"]="SV", ["MT"]="HT", ["MU"]="CU",
        ["MW"]="KY", ["MY"]="BS", ["MZ"]="BZ", ["TA"]="AG", ["TB"]="BB", ["TD"]="DM", ["TF"]="GP",
        ["TG"]="GD", ["TI"]="VI", ["TJ"]="PR", ["TK"]="KN", ["TL"]="LC", ["TN"]="AW", ["TQ"]="AI",
        ["TR"]="MS", ["TT"]="TT", ["TU"]="VG", ["TV"]="VC", ["TX"]="BM", ["NF"]="FJ", ["NG"]="KI",
        ["NI"]="NU", ["NL"]="WF", ["NS"]="WS", ["NT"]="PF", ["NV"]="VU", ["NW"]="NC", ["NZ"]="NZ",
        ["AG"]="SB", ["AN"]="NR", ["AY"]="PG", ["BG"]="GL", ["BI"]="IS", ["BK"]="XK",
        ["UA"]="KZ", ["UC"]="KG", ["UT"]="UZ", ["CY"]="CA", ["CZ"]="CA",
    };

    // Only the genuine single-letter ICAO regions. All other first letters are
    // always 2-letter prefixes (handled by Two) so they are intentionally absent
    // and fall through to null. NO duplicate keys — a dictionary initializer
    // throws ArgumentException at runtime on duplicates.
    private static readonly Dictionary<char, string> One = new()
    {
        ['K']="US", ['C']="CA", ['Y']="AU", ['Z']="CN", ['U']="RU", ['P']="US",
    };

    /// <summary>Returns ISO alpha-2 (lower-case) or null if unknown.</summary>
    public static string? IsoFromIcao(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao) || icao.Length < 2) return null;
        var p2 = icao.Substring(0, 2);
        if (Two.TryGetValue(p2, out var iso) && iso.Length == 2) return iso.ToLowerInvariant();
        if (One.TryGetValue(char.ToUpperInvariant(icao[0]), out var one) && one.Length == 2)
            return one.ToLowerInvariant();
        return null;
    }
}
