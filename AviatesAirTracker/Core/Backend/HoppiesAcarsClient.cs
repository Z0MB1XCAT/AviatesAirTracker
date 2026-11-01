using RestSharp;
using Serilog;

namespace AviatesAirTracker.Core.Backend;

/// <summary>
/// Sends one-way telex messages over the real Hoppies ACARS network (hoppie.nl),
/// authenticated with the VA's own company logon code. Delivery is best-effort — a
/// failed send is logged and swallowed, never thrown into the caller, matching the
/// non-critical-call pattern used elsewhere in AviatesBackendClient.
/// </summary>
public class HoppiesAcarsClient
{
    // VA company Hoppies logon code. Authorizes outgoing sends — not the pilot's own
    // personal code (that's AppSettings.HoppiesPilotCode, stored but unused for
    // sending in this build). Hardcoded per explicit decision: this is a VA
    // operations credential, not a high-value secret.
    private const string CompanyLogonCode = "ykWrqTsbepVQAaj";
    private const string SenderStationId = "VAOPS";
    private const string HoppiesEndpoint = "https://www.hoppie.nl/acars/system/connect.html";

    private readonly RestClient _client = new();

    /// <summary>Sends a telex message to the given callsign. Returns true if Hoppies
    /// acknowledged the request (response body starts with "ok").</summary>
    public async Task<bool> SendTelexAsync(string toCallsign, string message)
    {
        if (string.IsNullOrWhiteSpace(toCallsign) || string.IsNullOrWhiteSpace(message))
            return false;

        try
        {
            var request = new RestRequest(HoppiesEndpoint, Method.Get)
                .AddQueryParameter("logon", CompanyLogonCode)
                .AddQueryParameter("from", SenderStationId)
                .AddQueryParameter("to", toCallsign)
                .AddQueryParameter("type", "telex")
                .AddQueryParameter("packet", message);

            var response = await _client.ExecuteAsync(request);
            var body = (response.Content ?? "").Trim();
            var success = response.IsSuccessful && body.StartsWith("ok", StringComparison.OrdinalIgnoreCase);

            if (success)
                Log.Information("[HoppiesAcars] Sent telex to {Callsign}: {Preview}", toCallsign, Preview(message));
            else
                Log.Warning("[HoppiesAcars] Telex to {Callsign} not accepted: {Body}", toCallsign, body);

            return success;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[HoppiesAcars] Failed to send telex to {Callsign} (non-critical)", toCallsign);
            return false;
        }
    }

    /// <summary>Polls Hoppies under the pilot's own logon code (never <see cref="CompanyLogonCode"/>).
    /// Hoppies only starts delivering telexes addressed to a callsign once that callsign's own
    /// logon code has made at least one authenticated request — normally the pilot's in-sim ACARS
    /// client does this by polling its inbox. Without it, a company telex sent to the pilot's
    /// callsign (via <see cref="SendTelexAsync"/>) silently never reaches them until they poll
    /// manually first. Calling this once per flight, right after gate spawn, closes that gap.
    /// Returns true if Hoppies acknowledged the poll (response body starts with "ok").</summary>
    public async Task<bool> PollAsync(string pilotLogonCode, string fromCallsign)
    {
        if (string.IsNullOrWhiteSpace(pilotLogonCode) || string.IsNullOrWhiteSpace(fromCallsign))
            return false;

        try
        {
            var request = new RestRequest(HoppiesEndpoint, Method.Get)
                .AddQueryParameter("logon", pilotLogonCode)
                .AddQueryParameter("from", fromCallsign)
                .AddQueryParameter("to", fromCallsign)
                .AddQueryParameter("type", "poll");

            var response = await _client.ExecuteAsync(request);
            var body = (response.Content ?? "").Trim();
            var success = response.IsSuccessful && body.StartsWith("ok", StringComparison.OrdinalIgnoreCase);

            if (success)
                Log.Information("[HoppiesAcars] Pilot init poll succeeded for {Callsign}", fromCallsign);
            else
                Log.Warning("[HoppiesAcars] Pilot init poll for {Callsign} not accepted: {Body}", fromCallsign, body);

            return success;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[HoppiesAcars] Pilot init poll failed for {Callsign} (non-critical)", fromCallsign);
            return false;
        }
    }

    /// <summary>One raw request under the PILOT's own logon code, for the EFB's datalink terminal
    /// (telex, inforeq, cpdlc, peek). Never use <see cref="CompanyLogonCode"/> here: reading a
    /// shared company inbox per client would mix every pilot's traffic (see the AI dispatcher,
    /// which polls VAOPS server-side for exactly that reason). Returns Hoppie's response body,
    /// e.g. "ok {...}" or "error {illegal logon code}"; never throws.</summary>
    public async Task<(bool Ok, string Body)> ConnectAsync(string pilotLogonCode, string from, string to, string type, string? packet)
    {
        if (string.IsNullOrWhiteSpace(pilotLogonCode) || string.IsNullOrWhiteSpace(from))
            return (false, "error {no logon code or callsign}");
        try
        {
            var request = new RestRequest(HoppiesEndpoint, Method.Get)
                .AddQueryParameter("logon", pilotLogonCode)
                .AddQueryParameter("from", from)
                .AddQueryParameter("to", string.IsNullOrWhiteSpace(to) ? "SERVER" : to)
                .AddQueryParameter("type", type);
            if (packet != null) request.AddQueryParameter("packet", packet);

            var response = await _client.ExecuteAsync(request);
            var body = (response.Content ?? "").Trim();
            var ok = response.IsSuccessful && body.StartsWith("ok", StringComparison.OrdinalIgnoreCase);
            if (!ok) Log.Warning("[HoppiesAcars] {Type} from {From} to {To} not accepted: {Body}", type, from, to, Preview(body));
            return (ok, body);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[HoppiesAcars] {Type} request failed (non-critical)", type);
            return (false, "error {network}");
        }
    }

    private static string Preview(string message)
    {
        var oneLine = message.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length > 60 ? oneLine[..60] + "..." : oneLine;
    }
}
