using System.Text;
using System.Text.RegularExpressions;

namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB DATALINK — the Hoppie ACARS wire formats, kept pure so they can be unit-tested.
//
// Hoppie's connect.html answers "ok {…} {…}" (or "error {reason}"). Each brace group is one
// message: "{id FROM type {packet}}" from peek, "{FROM type {packet}}" from poll, and
// "{server info {text}}" for an inforeq. CPDLC packets are "/data2/MIN/MRN/RA/TEXT":
// MIN = this message's number, MRN = the number it answers (blank if none), RA = the
// response the sender wants (WU wilco/unable, AN affirm/negative, R roger, Y any, N/NE none).
// In TEXT, "@" brackets variable fields (CLIMB TO @FL360@) and "@_@" separates lines.
// ============================================================

public sealed record HoppieMessage(string? Id, string From, string Type, string Packet);

public sealed record CpdlcPacket(int Min, int? Mrn, string Ra, string Text)
{
    /// <summary>The text as a pilot reads it: line separators turned into new lines and the
    /// "@" field markers dropped.</summary>
    public string Display => Text.Replace("@_@", "\n").Replace("@", "").Trim();

    /// <summary>The responses a pilot may give, in DCDU order (empty = none required).</summary>
    public IReadOnlyList<string> Responses => EfbAcars.ResponsesFor(Ra);

    public string Format() => EfbAcars.FormatCpdlc(Min, Mrn, Ra, Text);
}

public static class EfbAcars
{
    /// <summary>Splits a connect.html body into its messages. An "error {…}" body, or anything
    /// not starting with "ok", yields no messages.</summary>
    public static List<HoppieMessage> Parse(string? body)
    {
        var list = new List<HoppieMessage>();
        if (string.IsNullOrWhiteSpace(body)) return list;
        body = body.Trim();
        if (!body.StartsWith("ok", StringComparison.OrdinalIgnoreCase)) return list;

        int i = 2;
        while (i < body.Length)
        {
            int open = body.IndexOf('{', i);
            if (open < 0) break;
            int close = Matching(body, open);
            if (close < 0) break;
            var group = body.Substring(open + 1, close - open - 1);
            i = close + 1;

            int p = group.IndexOf('{');
            string head = (p < 0 ? group : group[..p]).Trim();
            string packet = "";
            if (p >= 0)
            {
                int pc = Matching(group, p);
                packet = pc < 0 ? group[(p + 1)..] : group.Substring(p + 1, pc - p - 1);
            }
            var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3)
                list.Add(new HoppieMessage(words[0], words[1], words[2].ToLowerInvariant(), packet.Trim()));
            else if (words.Length == 2)
                list.Add(new HoppieMessage(null, words[0], words[1].ToLowerInvariant(), packet.Trim()));
        }
        return list;
    }

    /// <summary>The reason inside "error {…}", or null for an ok body.</summary>
    public static string? ErrorOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "No response from the Hoppie server.";
        body = body.Trim();
        if (body.StartsWith("ok", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(body, @"\{(.*)\}", RegexOptions.Singleline);
        var reason = m.Success ? m.Groups[1].Value.Trim() : body;
        return reason.Equals("illegal logon code", StringComparison.OrdinalIgnoreCase)
            ? "Hoppie rejected the logon code. Check it in the desktop app (Settings › Hoppies ACARS)."
            : "Hoppie: " + reason;
    }

    private static int Matching(string s, int open)
    {
        int depth = 0;
        for (int k = open; k < s.Length; k++)
        {
            if (s[k] == '{') depth++;
            else if (s[k] == '}' && --depth == 0) return k;
        }
        return -1;
    }

    public static bool TryParseCpdlc(string? packet, out CpdlcPacket cpdlc)
    {
        cpdlc = null!;
        if (string.IsNullOrWhiteSpace(packet)) return false;
        var m = Regex.Match(packet.Trim(), @"^/data2/(\d+)/(\d*)/([A-Za-z]*)/(.*)$", RegexOptions.Singleline);
        if (!m.Success) return false;
        int? mrn = int.TryParse(m.Groups[2].Value, out var r) ? r : null;
        cpdlc = new CpdlcPacket(int.Parse(m.Groups[1].Value), mrn, m.Groups[3].Value.ToUpperInvariant(), m.Groups[4].Value.Trim());
        return true;
    }

    public static string FormatCpdlc(int min, int? mrn, string ra, string text) =>
        $"/data2/{min}/{(mrn?.ToString() ?? "")}/{ra}/{text}";

    public static IReadOnlyList<string> ResponsesFor(string ra) => (ra ?? "").ToUpperInvariant() switch
    {
        "WU" => ["WILCO", "UNABLE", "STANDBY"],
        "AN" => ["AFFIRM", "NEGATIVE", "STANDBY"],
        "R"  => ["ROGER", "UNABLE", "STANDBY"],
        "Y"  => ["ROGER"],
        _    => [],
    };

    /// <summary>A response closes the uplink unless it is STANDBY.</summary>
    public static bool IsClosing(string response) =>
        !response.Equals("STANDBY", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pre-departure clearance request VATSIM controllers' PDC tools expect.</summary>
    public static string PdcRequest(string callsign, string aircraftType, string destination, string departure,
                                    string? stand, string? atis, string? remarks = null)
    {
        var sb = new StringBuilder($"REQUEST PREDEP CLEARANCE {Up(callsign)} {Up(aircraftType)} TO {Up(destination)} AT {Up(departure)}");
        if (!string.IsNullOrWhiteSpace(stand)) sb.Append($" STAND {Up(stand)}");
        if (!string.IsNullOrWhiteSpace(atis)) sb.Append($" ATIS {Up(atis)}");
        if (!string.IsNullOrWhiteSpace(remarks)) sb.Append($" {Up(remarks)}");
        return sb.ToString();
    }

    /// <summary>ACARS is upper case, one space between words, no braces (they'd break the wire format).</summary>
    public static string Clean(string? text) =>
        Regex.Replace((text ?? "").ToUpperInvariant().Replace('{', '(').Replace('}', ')'), @"[ \t]+", " ").Trim();

    private static string Up(string? s) => (s ?? "").Trim().ToUpperInvariant();

    /// <summary>Hoppie station names: 3–8 letters/digits (airports, FIRs, callsigns, SERVER).</summary>
    public static bool IsStation(string? s) => !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s.Trim(), @"^[A-Za-z0-9_]{3,10}$");
}
