using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddHttpClient();
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
builder.Services.AddMemoryCache();

var app = builder.Build();
app.UseResponseCompression();
app.UseCors();

// =========================================================================
// 1. CARREGAMENTO DAS ESTAÇÕES (STATIONS.JSON OFICIAL)
// =========================================================================
var allStations = new List<StationDto>();

void CarregarEstacoesOficiais()
{
    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
    var path = Path.Combine(baseDir, "stations.json");
    if (!File.Exists(path)) path = "stations.json";

    if (File.Exists(path))
    {
        try
        {
            var raw = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("stations", out var stationsElem))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                allStations = JsonSerializer.Deserialize<List<StationDto>>(stationsElem.GetRawText(), options) ?? new();
                Console.WriteLine($"[ESTAÇÕES] Carregadas {allStations.Count} estações com sucesso de stations.json.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO] Falha ao carregar stations.json: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine("[AVISO] Ficheiro stations.json não encontrado!");
    }
}

CarregarEstacoesOficiais();

var fertagusStationKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    { "roma_areeiro", "94-66035" },
    { "entrecampos", "94-66050" },
    { "sete_rios", "94-66076" },
    { "campolide", "94-60004" },
    { "pragal", "94-17087" },
    { "corroios", "94-17137" },
    { "foros_de_amora", "94-17152" },
    { "fogueteiro", "94-17186" },
    { "coina", "94-17236" },
    { "penalva", "94-17095" },
    { "pinhal_novo", "94-68007" },
    { "venda_do_alcaide", "94-68049" },
    { "palmela", "94-68080" },
    { "setubal", "94-68122" }
};

// =========================================================================
// ENDPOINTS
// =========================================================================

// Estações pré-calculadas uma única vez (antes eram recalculadas a cada pedido)
static double ParseCoord(string? v) =>
    double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;

var ftCodes = fertagusStationKeys.Values.ToHashSet();
var ftStationsOut = allStations
    .Where(s => ftCodes.Contains(s.Code) || (s.Railways != null && s.Railways.Contains("FT")))
    .Select(s => new { code = s.Code, name = s.Designation, lat = ParseCoord(s.Latitude), lon = ParseCoord(s.Longitude) })
    .ToList();
var cpStationsOut = allStations
    .Where(s => s.Code != null && !string.IsNullOrEmpty(s.Latitude))
    .Select(s => new { code = s.Code, name = s.Designation, lat = ParseCoord(s.Latitude), lon = ParseCoord(s.Longitude) })
    .ToList();

// Endpoint de Estações filtrado por operador
app.MapGet("/api/stations", (string? @operator, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=600";
    var op = (@operator ?? "fertagus").ToLower();

    if (op == "fertagus") return Results.Ok(ftStationsOut);
    return Results.Ok(cpStationsOut);
});

// Endpoint para servir o traçado da via Fertagus
string? fertagusTrackJson = null;
app.MapGet("/api/tracks/fertagus", async (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=3600";
    if (fertagusTrackJson == null)
    {
        var trackPath = Path.Combine(app.Environment.ContentRootPath, "fertagus_track.json");
        if (!File.Exists(trackPath))
            return Results.NotFound(new { message = "Ficheiro fertagus_track.json não encontrado." });
        fertagusTrackJson = await File.ReadAllTextAsync(trackPath);
    }
    return Results.Content(fertagusTrackJson, "application/json");
});

// Horários Fertagus (unifica os dois sentidos) para o radar calcular posições
object? fertagusScheduleCache = null;
app.MapGet("/api/trains/fertagus/schedule", async (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=3600";
    if (fertagusScheduleCache != null) return Results.Ok(fertagusScheduleCache);

    var root = app.Environment.ContentRootPath;
    var files = new[]
    {
        (Path.Combine(root, "fertagus_sentido_lisboa_partida.json"), "norte"),
        (Path.Combine(root, "fertagus_sentido_margem_partida.json"), "sul")
    };

    var trips = new List<object>();
    foreach (var (path, dir) in files)
    {
        if (!File.Exists(path)) continue;
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        foreach (var trip in doc.RootElement.GetProperty("trips").EnumerateArray())
        {
            var stops = new List<object>();
            int last = -1, offset = 0;
            // A ordem das propriedades no JSON é a ordem de marcha do comboio
            foreach (var prop in trip.EnumerateObject())
            {
                if (!fertagusStationKeys.ContainsKey(prop.Name)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var parts = prop.Value.GetString()!.Split(':');
                var secs = int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60;
                if (last >= 0 && secs + offset < last) offset += 86400; // passou a meia-noite
                secs += offset;
                last = secs;
                stops.Add(new { k = prop.Name, t = secs });
            }
            if (stops.Count < 2) continue;

            trip.TryGetProperty("ocupacao", out var occ);
            trips.Add(new
            {
                id = trip.GetProperty("id").GetInt32(),
                dir,
                carruagens = trip.TryGetProperty("carruagens", out var c) ? c.GetInt32() : (int?)null,
                ocupacao = occ.ValueKind == JsonValueKind.Number ? occ.GetDouble() : (double?)null,
                stops
            });
        }
    }

    var byCode = allStations.ToDictionary(s => s.Code, s => s, StringComparer.OrdinalIgnoreCase);
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var stationsOut = new Dictionary<string, object>();
    foreach (var (key, code) in fertagusStationKeys)
    {
        if (!byCode.TryGetValue(code, out var st)) continue;
        stationsOut[key] = new
        {
            name = st.Designation,
            lat = double.Parse(st.Latitude, System.Globalization.NumberStyles.Any, inv),
            lon = double.Parse(st.Longitude, System.Globalization.NumberStyles.Any, inv)
        };
    }

    fertagusScheduleCache = new { stations = stationsOut, trips };
    return Results.Ok(fertagusScheduleCache);
});

// =========================================================================
// CP TEMPO REAL — proxy + normalização do feed configurado em appsettings.json
// =========================================================================
var cpLock = new SemaphoreSlim(1, 1);
List<CpTrainDto>? cpLastGood = null;

app.MapGet("/api/trains/cp", async (IHttpClientFactory httpFactory, IMemoryCache cache, IConfiguration cfg) =>
{
    var url = cfg["CpRealtime:Url"];
    if (string.IsNullOrWhiteSpace(url))
        return Results.Problem("CpRealtime:Url não está configurado em appsettings.json.", statusCode: 503);

    if (cache.TryGetValue("cp_trains", out List<CpTrainDto>? cached) && cached != null)
        return Results.Ok(cached);

    // Só um pedido de cada vez ao feed: os restantes esperam e reutilizam o resultado
    await cpLock.WaitAsync();
    try
    {
        if (cache.TryGetValue("cp_trains", out cached) && cached != null)
            return Results.Ok(cached);

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ComboiosApp/1.0");

        var raw = await client.GetStringAsync(url);
        var seconds = !string.Equals(cfg["CpRealtime:DelayUnit"], "minutes", StringComparison.OrdinalIgnoreCase);
        var trains = CpFeed.Parse(raw, seconds);
        var ttl = cfg.GetValue<int>("CpRealtime:CacheSeconds", 10);
        cache.Set("cp_trains", trains, TimeSpan.FromSeconds(ttl));
        cpLastGood = trains;

        Console.WriteLine($"[CP] {trains.Count} comboios com posição.");
        return Results.Ok(trains);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CP] Erro: {ex.Message}");
        // Se o feed falhar pontualmente, mantém o mapa vivo com a última leitura boa
        if (cpLastGood != null) return Results.Ok(cpLastGood);
        return Results.Problem("Falha ao obter o feed CP: " + ex.Message, statusCode: 502);
    }
    finally
    {
        cpLock.Release();
    }
});

// Diagnóstico: devolve o JSON original do feed, para confirmares os nomes dos campos
app.MapGet("/api/trains/cp/raw", async (IHttpClientFactory httpFactory, IConfiguration cfg) =>
{
    var url = cfg["CpRealtime:Url"];
    if (string.IsNullOrWhiteSpace(url))
        return Results.Problem("CpRealtime:Url não está configurado.", statusCode: 503);
    try
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ComboiosApp/1.0");
        return Results.Content(await client.GetStringAsync(url), "application/json");
    }
    catch (Exception ex)
    {
        return Results.Problem("Falha ao obter o feed CP: " + ex.Message, statusCode: 502);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();

// =========================================================================
// MODELOS / DTOs
// =========================================================================

public class StationDto
{
    public string Code { get; set; } = "";
    public string Designation { get; set; } = "";
    public string Latitude { get; set; } = "";
    public string Longitude { get; set; } = "";
    public string? Region { get; set; }
    public List<string>? Railways { get; set; }
}

public record CpTrainDto(
    string Id, double Lat, double Lon, double? Heading, int DelayMin,
    string? Service, string? Origin, string? Destination);

// Leitor tolerante: encontra a lista de comboios e os campos mais comuns,
// sem depender de um esquema exato (o feed é não oficial e pode mudar).
public static class CpFeed
{
    static readonly string[] LatKeys = { "lat", "latitude" };
    static readonly string[] LonKeys = { "lon", "lng", "long", "longitude" };
    static readonly string[] PosContainers = { "position", "location", "coords", "coordinates", "gps", "status" };
    static readonly string[] IdKeys = { "trainNumber", "train_number", "number", "numero", "trainId", "id" };
    static readonly string[] DelayKeys = { "delay", "delaySeconds", "delay_seconds", "atraso" };
    static readonly string[] HeadingKeys = { "heading", "bearing", "course" };
    static readonly string[] ServiceKeys = { "service", "serviceType", "trainType", "type" };
    static readonly string[] OriginKeys = { "origin", "trainOrigin", "from" };
    static readonly string[] DestKeys = { "destination", "trainDestination", "to" };

    public static List<CpTrainDto> Parse(string raw, bool delayInSeconds)
    {
        var list = new List<CpTrainDto>();
        using var doc = JsonDocument.Parse(raw);
        var arr = FindArray(doc.RootElement, 0);
        if (arr == null) return list;

        foreach (var el in arr.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var lat = Num(Find(el, LatKeys, true));
            var lon = Num(Find(el, LonKeys, true));
            if (lat == null || lon == null) continue;
            if (lat < 36.8 || lat > 42.3 || lon < -9.7 || lon > -6.0) continue; // fora de Portugal continental

            var delayRaw = Num(Find(el, DelayKeys, true));
            var delayMin = 0;
            if (delayRaw != null)
                delayMin = (int)Math.Round(delayInSeconds ? delayRaw.Value / 60.0 : delayRaw.Value);

            list.Add(new CpTrainDto(
                Str(Find(el, IdKeys, false)) ?? list.Count.ToString(),
                lat.Value, lon.Value,
                Num(Find(el, HeadingKeys, true)),
                delayMin,
                Str(Find(el, ServiceKeys, false)),
                Str(Find(el, OriginKeys, false)),
                Str(Find(el, DestKeys, false))));
        }
        return list;
    }

    static JsonElement? FindArray(JsonElement e, int depth)
    {
        if (e.ValueKind == JsonValueKind.Array) return e;
        if (e.ValueKind != JsonValueKind.Object || depth > 3) return null;
        foreach (var name in new[] { "trains", "data", "items", "results", "active" })
        {
            if (e.TryGetProperty(name, out var p))
            {
                var r = FindArray(p, depth + 1);
                if (r != null) return r;
            }
        }
        foreach (var prop in e.EnumerateObject())
        {
            var r = FindArray(prop.Value, depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    static JsonElement? Direct(JsonElement obj, string[] keys)
    {
        foreach (var p in obj.EnumerateObject())
            foreach (var k in keys)
                if (string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase))
                    return p.Value;
        return null;
    }

    static JsonElement? Deep(JsonElement obj, string[] keys, int depth)
    {
        var d = Direct(obj, keys);
        if (d != null) return d;
        if (depth >= 3) return null;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            var r = Deep(p.Value, keys, depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    static JsonElement? Find(JsonElement obj, string[] keys, bool preferPosition)
    {
        var d = Direct(obj, keys);
        if (d != null) return d;
        if (preferPosition)
        {
            foreach (var c in PosContainers)
            {
                var child = Direct(obj, new[] { c });
                if (child != null && child.Value.ValueKind == JsonValueKind.Object)
                {
                    var v = Direct(child.Value, keys);
                    if (v != null) return v;
                }
            }
        }
        return Deep(obj, keys, 0);
    }

    static double? Num(JsonElement? e)
    {
        if (e == null) return null;
        var v = e.Value;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    static string? Str(JsonElement? e)
    {
        if (e == null) return null;
        var v = e.Value;
        switch (v.ValueKind)
        {
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.Number: return v.GetRawText();
            case JsonValueKind.Object:
                foreach (var k in new[] { "designation", "name", "code" })
                {
                    var x = Direct(v, new[] { k });
                    if (x != null && x.Value.ValueKind == JsonValueKind.String) return x.Value.GetString();
                }
                return null;
            default:
                return null;
        }
    }
}