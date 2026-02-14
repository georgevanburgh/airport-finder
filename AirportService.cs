using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AirportFinder;

public record Airport(string Name, string Destination);

public record Leg(string Mode, int Duration, string From, string To, string Instruction, double[][] Path);

public record JourneyResult(
    string AirportName,
    int? DurationMinutes,
    string Summary,
    List<Leg>? Legs = null,
    string? Error = null
);

public class AirportService(HttpClient http, ILogger<AirportService> logger)
{
    private static readonly Airport[] Airports =
    [
        new("Heathrow", "51.471618,-0.454037"),
        new("Gatwick", "920GLGW0"),
        new("Stansted", "920GSTN1"),
        new("Luton", "910GLUTOAPY"),
        new("London City", "51.503419,0.048749"),
        new("Southend", "51.56867,0.70505"),
    ];

    public async Task<(double Lat, double Lon)?> GeocodePostcodeAsync(string postcode, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(postcode.Trim());
        var response = await http.GetAsync($"https://api.postcodes.io/postcodes/{encoded}", ct);

        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var result = doc.RootElement.GetProperty("result");
        var lat = result.GetProperty("latitude").GetDouble();
        var lon = result.GetProperty("longitude").GetDouble();
        return (lat, lon);
    }

    public async Task<List<JourneyResult>> GetJourneyTimesAsync(double fromLat, double fromLon, string? date = null, string? time = null, CancellationToken ct = default)
    {
        var tasks = Airports.Select(airport => GetSingleJourneyAsync(fromLat, fromLon, airport, date, time, ct));
        var results = await Task.WhenAll(tasks);
        return results
            .OrderBy(r => r.DurationMinutes == null)
            .ThenBy(r => r.DurationMinutes)
            .ToList();
    }

    private async Task<JourneyResult> GetSingleJourneyAsync(double fromLat, double fromLon, Airport airport, string? date, string? time, CancellationToken ct)
    {
        try
        {
            var from = $"{fromLat},{fromLon}";
            var url = $"https://api.tfl.gov.uk/Journey/JourneyResults/{from}/to/{airport.Destination}";
            var query = new List<string>();
            if (date is not null) query.Add($"date={date}");
            if (time is not null)
            {
                query.Add($"time={time}");
                query.Add("timeIs=Departing");
            }
            if (query.Count > 0) url += "?" + string.Join("&", query);
            var response = await http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
                return new JourneyResult(airport.Name, null, "", Error: $"TfL API returned {(int)response.StatusCode}");

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var journeys = doc.RootElement.GetProperty("journeys");

            int? bestDuration = null;
            string bestSummary = "";
            List<Leg>? bestLegs = null;

            foreach (var journey in journeys.EnumerateArray())
            {
                var duration = journey.GetProperty("duration").GetInt32();
                if (bestDuration is null || duration < bestDuration)
                {
                    bestDuration = duration;
                    var legs = journey.GetProperty("legs").EnumerateArray()
                        .Select(ParseLeg)
                        .ToList();
                    bestSummary = string.Join(" → ", legs.Select(l => l.Mode).Distinct());
                    bestLegs = legs;
                }
            }

            return new JourneyResult(airport.Name, bestDuration, bestSummary, bestLegs);
        }
        catch (Exception ex)
        {
            return new JourneyResult(airport.Name, null, "", Error: $"Error: {ex.Message}");
        }
    }

    private Leg ParseLeg(JsonElement leg)
    {
        var mode = leg.GetProperty("mode").GetProperty("name").GetString() ?? "";
        var duration = leg.GetProperty("duration").GetInt32();
        var from = leg.GetProperty("departurePoint").GetProperty("commonName").GetString() ?? "";
        var to = leg.GetProperty("arrivalPoint").GetProperty("commonName").GetString() ?? "";
        var instruction = leg.TryGetProperty("instruction", out var instr)
            ? instr.GetProperty("summary").GetString() ?? ""
            : "";

        var pathStr = leg.TryGetProperty("path", out var pathObj)
            && pathObj.TryGetProperty("lineString", out var ls)
            ? ls.GetString() : null;
        double[][] path = [];
        if (pathStr is not null)
        {
            try
            {
                using var pathDoc = JsonDocument.Parse(pathStr);
                path = pathDoc.RootElement.EnumerateArray()
                    .Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() })
                    .ToArray();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse path lineString: {PathStr}", pathStr);
            }
        }

        return new Leg(Capitalize(mode), duration, from, to, instruction, path);
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..].Replace("-", " ");
}
