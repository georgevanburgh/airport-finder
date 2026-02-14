using AirportFinder;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<AirportService>();

var app = builder.Build();

app.UseStaticFiles();

app.MapGet("/api/search", async (string? postcode, string? date, string? time, AirportService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(postcode))
        return Results.BadRequest(new { error = "Please enter a postcode." });

    var coords = await service.GeocodePostcodeAsync(postcode, ct);
    if (coords is null)
        return Results.BadRequest(new { error = $"Could not find postcode '{postcode}'. Please check and try again." });

    var (lat, lon) = coords.Value;
    var results = await service.GetJourneyTimesAsync(lat, lon, date, time, ct);
    return Results.Ok(results);
});

app.MapFallbackToFile("index.html");

app.Run();
