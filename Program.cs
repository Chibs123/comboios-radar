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

// Procura de estações por código (para resolver as paragens do feed da CP)
var stationByCode = allStations.Where(s => !string.IsNullOrEmpty(s.Code))
    .GroupBy(s => s.Code).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

(double, double, string)? LookupStation(string code)
{
    if (!stationByCode.TryGetValue(code, out var st) && !stationByCode.TryGetValue("94-" + code, out st))
        return null;
    return (ParseCoord(st.Latitude), ParseCoord(st.Longitude), st.Designation);
}

app.MapGet("/api/stations", (string? @operator, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "public, max-age=600";
    var op = (@operator ?? "fertagus").ToLower();

    if (op == "fertagus") return Results.Ok(ftStationsOut);
    return Results.Ok(cpStationsOut);
});

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
            foreach (var prop in trip.EnumerateObject())
            {
                if (!fertagusStationKeys.ContainsKey(prop.Name)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var parts = prop.Value.GetString()!.Split(':');
                var secs = int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60;
                if (last >= 0 && secs + offset < last) offset += 86400;
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

var cpLock = new SemaphoreSlim(1, 1);
List<CpTrainDto>? cpLastGood = null;
Dictionary<string, List<CpStopDto>> cpStops = new();

app.MapGet("/api/trains/cp", async (IHttpClientFactory httpFactory, IMemoryCache cache, IConfiguration cfg) =>
{
    var url = cfg["CpRealtime:Url"];
    if (string.IsNullOrWhiteSpace(url))
        return Results.Problem("CpRealtime:Url não está configurado em appsettings.json.", statusCode: 503);

    if (cache.TryGetValue("cp_trains", out List<CpTrainDto>? cached) && cached != null)
        return Results.Ok(cached);

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
        var stopsNovas = new Dictionary<string, List<CpStopDto>>();
        var trains = CpFeed.Parse(raw, seconds, LookupStation, stopsNovas);
        cpStops = stopsNovas;
        var ttl = cfg.GetValue<int>("CpRealtime:CacheSeconds", 10);
        cache.Set("cp_trains", trains, TimeSpan.FromSeconds(ttl));
        cpLastGood = trains;

        return Results.Ok(trains);
    }
    catch (Exception ex)
    {
        if (cpLastGood != null) return Results.Ok(cpLastGood);
        return Results.Problem("Falha ao obter o feed CP: " + ex.Message, statusCode: 502);
    }
    finally
    {
        cpLock.Release();
    }
});

// Paragens (estações) do comboio, se o feed as trouxer
var rail = new RailGraph();   // tem de ser declarado antes de ser usado no endpoint

app.MapGet("/api/trains/cp/{id}/stops", (string id) =>
{
    var stops = cpStops.TryGetValue(id, out var st) ? st : new List<CpStopDto>();
    var route = rail.Route(stops);   // null enquanto o grafo não estiver pronto -> o cliente usa linhas retas
    return Results.Ok(new { stops, path = route?.Path, stopAt = route?.At, railStatus = rail.Status });
});

// Grafo ferroviário (OpenStreetMap): carrega em segundo plano, não atrasa o arranque
var railPath = Path.Combine(app.Environment.ContentRootPath, app.Configuration["RailGraph:Path"] ?? "rail_pt.json");
_ = Task.Run(async () =>
{
    var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
    http.Timeout = TimeSpan.FromMinutes(6);
    await rail.LoadAsync(railPath, http);
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();

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

public record CpStopDto(string Name, double Lat, double Lon, string? Time);

public static class CpFeed
{
    static readonly string[] StopArrayKeys = { "trainStops", "stops", "paragens", "route", "itinerary", "stations" };
    static readonly string[] StopNameKeys = { "designation", "name", "station", "stationName", "nome" };
    static readonly string[] StopCodeKeys = { "code", "stationCode", "station_code", "nodeCode", "id" };
    static readonly string[] StopTimeKeys = { "time", "scheduledTime", "arrival", "arrivalTime", "departure", "hora" };

    static readonly string[] LatKeys = { "lat", "latitude" };
    static readonly string[] LonKeys = { "lon", "lng", "long", "longitude" };
    static readonly string[] PosContainers = { "position", "location", "coords", "coordinates", "gps", "status" };
    static readonly string[] IdKeys = { "trainNumber", "train_number", "number", "numero", "trainId", "id" };
    static readonly string[] DelayKeys = { "delay", "delaySeconds", "delay_seconds", "atraso" };
    static readonly string[] HeadingKeys = { "heading", "bearing", "course" };
    static readonly string[] ServiceKeys = { "service", "serviceType", "trainType", "type" };
    static readonly string[] OriginKeys = { "origin", "trainOrigin", "from" };
    static readonly string[] DestKeys = { "destination", "trainDestination", "to" };

    public static List<CpTrainDto> Parse(string raw, bool delayInSeconds,
        Func<string, (double, double, string)?> lookup, Dictionary<string, List<CpStopDto>> stopsOut)
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
            if (lat < 36.8 || lat > 42.3 || lon < -9.7 || lon > -6.0) continue;

            var delayRaw = Num(Find(el, DelayKeys, true));
            var delayMin = 0;
            if (delayRaw != null)
                delayMin = Math.Max(0, (int)Math.Round(delayInSeconds ? delayRaw.Value / 60.0 : delayRaw.Value));

            var id = Str(Find(el, IdKeys, false)) ?? list.Count.ToString();
            var stops = ExtractStops(el, lookup);
            if (stops.Count > 1) stopsOut[id] = stops;

            list.Add(new CpTrainDto(
                id,
                lat.Value, lon.Value,
                Num(Find(el, HeadingKeys, true)) ?? HeadingFromStops(lat.Value, lon.Value, stops),
                delayMin,
                Str(Find(el, ServiceKeys, false)),
                Str(Find(el, OriginKeys, false)),
                Str(Find(el, DestKeys, false))));
        }
        return list;
    }

    // O feed não traz rumo: usa a direção do troço de paragens mais próximo (segue o sentido real da linha)
    static double? HeadingFromStops(double lat, double lon, List<CpStopDto> stops)
    {
        if (stops.Count < 2) return null;
        var k = Math.Cos(lat * Math.PI / 180);
        var best = 0;
        var bestD = double.MaxValue;
        for (var i = 0; i < stops.Count - 1; i++)
        {
            double ax = stops[i].Lon * k, ay = stops[i].Lat, bx = stops[i + 1].Lon * k, by = stops[i + 1].Lat;
            double dx = bx - ax, dy = by - ay, px = lon * k - ax, py = lat - ay;
            var len2 = dx * dx + dy * dy;
            var t = len2 == 0 ? 0 : Math.Clamp((px * dx + py * dy) / len2, 0, 1);
            var ex = px - t * dx;
            var ey = py - t * dy;
            var d = Math.Sqrt(ex * ex + ey * ey);
            if (d < bestD) { bestD = d; best = i; }
        }
        var a = stops[best];
        var b = stops[best + 1];
        var brg = Math.Atan2((b.Lon - a.Lon) * k, b.Lat - a.Lat) * 180 / Math.PI;
        return Math.Round((brg + 360) % 360, 1);
    }

    static List<CpStopDto> ExtractStops(JsonElement train, Func<string, (double, double, string)?> lookup)
    {
        var result = new List<CpStopDto>();
        var arr = FindNamedArray(train, 0);
        if (arr == null) return result;

        foreach (var s in arr.Value.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var lat = Num(Find(s, LatKeys, true));
            var lon = Num(Find(s, LonKeys, true));
            var name = Str(Find(s, StopNameKeys, false));

            if (lat == null || lon == null)
            {
                var code = Str(Find(s, StopCodeKeys, false));
                var hit = code == null ? null : lookup(code);
                if (hit == null) continue;
                lat = hit.Value.Item1;
                lon = hit.Value.Item2;
                name ??= hit.Value.Item3;
            }
            result.Add(new CpStopDto(name ?? "", lat.Value, lon.Value, Str(Find(s, StopTimeKeys, false))));
        }
        return result;
    }

    static JsonElement? FindNamedArray(JsonElement e, int depth)
    {
        if (e.ValueKind != JsonValueKind.Object || depth > 3) return null;
        foreach (var p in e.EnumerateObject())
            foreach (var k in StopArrayKeys)
                if (string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                    return p.Value;
        foreach (var p in e.EnumerateObject())
        {
            var r = FindNamedArray(p.Value, depth + 1);
            if (r != null) return r;
        }
        return null;
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

public record RailRoute(List<double[]> Path, List<int> At);

// Linhas férreas de Portugal (railway=rail do OpenStreetMap) como grafo; liga paragens seguindo a via
public sealed class RailGraph
{
    double[] _lat = Array.Empty<double>(), _lon = Array.Empty<double>();
    (int To, float W)[][] _adj = Array.Empty<(int, float)[]>();
    readonly Dictionary<(int, int), List<int>> _grid = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, RailRoute> _cache = new();
    public volatile bool Ready;
    public string Status = "a iniciar";

    public async Task LoadAsync(string path, HttpClient http)
    {
        try
        {
            var geo = Path.Combine(Path.GetDirectoryName(path) ?? "", "export.geojson");
            if (!File.Exists(path) && File.Exists(geo)) path = geo;   // exportação manual do Overpass Turbo

            if (!File.Exists(path))
            {
                Status = "a descarregar as linhas do OpenStreetMap (1-2 min)";
                const string q = "[out:json][timeout:300];area[\"ISO3166-1\"=\"PT\"][admin_level=2]->.pt;way[\"railway\"=\"rail\"][!\"service\"](area.pt);out geom;";
                // O Overpass recusa (406) pedidos sem User-Agent identificável
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ComboiosApp/1.0 (live train tracker)");
                http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
                string[] servidores =
                {
                    "https://overpass-api.de/api/interpreter",
                    "https://overpass.kumi.systems/api/interpreter",
                    "https://overpass.private.coffee/api/interpreter"
                };
                Exception? ultimo = null;
                foreach (var url in servidores)
                {
                    try
                    {
                        using var res = await http.PostAsync(url,
                            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", q) }));
                        res.EnsureSuccessStatusCode();
                        var tmp = path + ".tmp";
                        await using (var fs = File.Create(tmp)) { await res.Content.CopyToAsync(fs); }
                        File.Move(tmp, path, true);
                        ultimo = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        ultimo = ex;
                        Console.WriteLine($"[RAIL] {url}: {ex.Message}");
                    }
                }
                if (ultimo != null) throw ultimo;
            }
            Status = "a construir o grafo";
            Build(await File.ReadAllBytesAsync(path));
            if (_lat.Length == 0)
            {
                if (!path.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase)) File.Delete(path);   // resposta vazia do Overpass
                throw new Exception("o ficheiro de linhas veio vazio (apagado)");
            }
            Ready = true;
            Status = $"pronto ({_lat.Length} nós)";
            Console.WriteLine($"[RAIL] Grafo {Status}");
        }
        catch (Exception ex)
        {
            Status = "erro: " + ex.Message;
            Console.WriteLine($"[RAIL] {Status}");
        }
    }

    void Build(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var idx = new Dictionary<long, int>();
        var lat = new List<double>();
        var lon = new List<double>();
        var adj = new List<List<(int, float)>>();

        int GetNode(long key, double la, double lo)
        {
            if (!idx.TryGetValue(key, out var cur))
            {
                cur = lat.Count;
                idx[key] = cur;
                lat.Add(la);
                lon.Add(lo);
                adj.Add(new List<(int, float)>());
            }
            return cur;
        }

        void AddLine(JsonElement line)
        {
            var prev = -1;
            foreach (var c in line.EnumerateArray())
            {
                double lo = c[0].GetDouble(), la = c[1].GetDouble();
                // nós partilhados entre vias têm coordenadas idênticas
                var key = (long)Math.Round(la * 1e7) * 1_000_000_000L + (long)Math.Round((lo + 20) * 1e7);
                var cur = GetNode(key, la, lo);
                if (prev >= 0 && prev != cur)
                {
                    var w = (float)Dist(lat[prev], lon[prev], lat[cur], lon[cur]);
                    adj[prev].Add((cur, w));
                    adj[cur].Add((prev, w));
                }
                prev = cur;
            }
        }

        if (doc.RootElement.TryGetProperty("features", out var features))
        {
            // GeoJSON (exportação do Overpass Turbo)
            foreach (var f in features.EnumerateArray())
            {
                if (!f.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object) continue;
                var type = g.GetProperty("type").GetString();
                var coords = g.GetProperty("coordinates");
                if (type == "LineString") AddLine(coords);
                else if (type == "Polygon" || type == "MultiLineString")
                    foreach (var part in coords.EnumerateArray()) AddLine(part);
            }
        }
        else
        foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (!el.TryGetProperty("nodes", out var nodes) || !el.TryGetProperty("geometry", out var geom)) continue;
            var n = nodes.GetArrayLength();
            if (geom.GetArrayLength() != n) continue;
            var prev = -1;
            for (var i = 0; i < n; i++)
            {
                var g = geom[i];
                if (g.ValueKind != JsonValueKind.Object) { prev = -1; continue; }
                var id = nodes[i].GetInt64();
                if (!idx.TryGetValue(id, out var cur))
                {
                    cur = lat.Count;
                    idx[id] = cur;
                    lat.Add(g.GetProperty("lat").GetDouble());
                    lon.Add(g.GetProperty("lon").GetDouble());
                    adj.Add(new List<(int, float)>());
                }
                if (prev >= 0 && prev != cur)
                {
                    var w = (float)Dist(lat[prev], lon[prev], lat[cur], lon[cur]);
                    adj[prev].Add((cur, w));
                    adj[cur].Add((prev, w));
                }
                prev = cur;
            }
        }

        _lat = lat.ToArray();
        _lon = lon.ToArray();
        _adj = adj.Select(a => a.ToArray()).ToArray();
        _grid.Clear();
        for (var i = 0; i < _lat.Length; i++)
        {
            var key = Cell(_lat[i], _lon[i]);
            if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<int>();
            l.Add(i);
        }
    }

    public RailRoute? Route(IReadOnlyList<CpStopDto> stops)
    {
        if (!Ready || stops.Count < 2) return null;
        var key = string.Join("|", stops.Select(s => s.Name));
        return _cache.GetOrAdd(key, _ => Compute(stops));
    }

    RailRoute Compute(IReadOnlyList<CpStopDto> stops)
    {
        var cands = stops.Select(st => Candidates(st.Lat, st.Lon)).ToArray();
        var path = new List<double[]>();
        var at = new List<int>();

        for (var i = 0; i < stops.Count; i++)
        {
            List<int>? seg = null;
            if (i > 0 && cands[i - 1].Count > 0 && cands[i].Count > 0)
            {
                seg = Shortest(cands[i - 1], cands[i]);
                var reta = Dist(stops[i - 1].Lat, stops[i - 1].Lon, stops[i].Lat, stops[i].Lon);
                if (seg != null && Length(seg) > 2.2 * reta + 3000) seg = null;   // desvio absurdo: via desligada no mapa
            }

            if (seg != null) foreach (var n in seg) AddPt(path, Pt(_lat[n], _lon[n]));
            else if (i == 0 && cands[0].Count > 0) AddPt(path, Pt(_lat[cands[0][0].N], _lon[cands[0][0].N]));
            else AddPt(path, Pt(stops[i].Lat, stops[i].Lon));   // sem via: segmento reto
            at.Add(path.Count - 1);
        }
        return new RailRoute(path, at);
    }

    static void AddPt(List<double[]> path, double[] p)
    {
        if (path.Count == 0 || path[^1][0] != p[0] || path[^1][1] != p[1]) path.Add(p);
    }

    static double[] Pt(double lat, double lon) => new[] { Math.Round(lon, 5), Math.Round(lat, 5) };
    static (int, int) Cell(double lat, double lon) => ((int)Math.Floor(lat / 0.01), (int)Math.Floor(lon / 0.01));

    static double Dist(double la1, double lo1, double la2, double lo2)
    {
        var k = Math.Cos((la1 + la2) * Math.PI / 360);
        var dx = (lo1 - lo2) * 111320 * k;
        var dy = (la1 - la2) * 110570;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    double Length(List<int> p)
    {
        var t = 0.0;
        for (var i = 1; i < p.Count; i++) t += Dist(_lat[p[i - 1]], _lon[p[i - 1]], _lat[p[i]], _lon[p[i]]);
        return t;
    }

    // Vários nós perto da estação (ex.: várias vias): evita ficar preso numa via desligada do resto
    List<(double D, int N)> Candidates(double lat, double lon, double maxM = 150, int max = 25)
    {
        var (cy, cx) = Cell(lat, lon);
        var res = new List<(double D, int N)>();
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                if (_grid.TryGetValue((cy + dy, cx + dx), out var l))
                    foreach (var i in l)
                    {
                        var d = Dist(lat, lon, _lat[i], _lon[i]);
                        if (d <= maxM) res.Add((d, i));
                    }
        if (res.Count == 0 && maxM < 1500) return Candidates(lat, lon, 1500, 5);
        res.Sort((a, b) => a.D.CompareTo(b.D));
        if (res.Count > max) res.RemoveRange(max, res.Count - max);
        return res;
    }

    // Dijkstra de várias origens para vários destinos
    List<int>? Shortest(List<(double D, int N)> srcs, List<(double D, int N)> tgts)
    {
        var g = new Dictionary<int, double>();
        var came = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        var pq = new PriorityQueue<int, double>();
        foreach (var (d, n) in srcs)
            if (!g.TryGetValue(n, out var o) || d < o) { g[n] = d; pq.Enqueue(n, d); }

        var tgt = new Dictionary<int, double>();
        foreach (var (d, n) in tgts)
            if (!tgt.TryGetValue(n, out var o) || d < o) tgt[n] = d;

        var best = double.MaxValue;
        var bestN = -1;
        while (pq.TryDequeue(out var u, out var cu))
        {
            if (cu >= best) break;
            if (!closed.Add(u)) continue;
            if (tgt.TryGetValue(u, out var td) && cu + td < best) { best = cu + td; bestN = u; }
            foreach (var (v, w) in _adj[u])
            {
                var ng = cu + w;
                if (g.TryGetValue(v, out var old) && old <= ng) continue;
                g[v] = ng;
                came[v] = u;
                pq.Enqueue(v, ng);
            }
        }
        if (bestN < 0) return null;

        var path = new List<int> { bestN };
        var x = bestN;
        while (came.TryGetValue(x, out var p)) { x = p; path.Add(x); }
        path.Reverse();
        return path;
    }
}