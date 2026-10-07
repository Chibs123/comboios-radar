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

app.MapGet("/api/stations", async (string? @operator, HttpContext ctx, IHttpClientFactory f, IMemoryCache cache) =>
{
    var op = (@operator ?? "fertagus").ToLower();

    if (op == "cm") 
    {
        var stops = await CmStops(f, cache);
        if (stops.Count == 0)   // ainda a carregar ou falhou: não deixar o navegador guardar uma lista vazia
            return Results.Problem("As paragens da Carris ainda não estão carregadas (ver /api/buses/status).", statusCode: 503);
        ctx.Response.Headers.CacheControl = "public, max-age=600";
        var cmStationsOut = stops.Select(s => new { code = s.Key, name = s.Value.Name, lat = s.Value.Lat, lon = s.Value.Lon }).ToList();
        return Results.Ok(cmStationsOut);
    }
    
    ctx.Response.Headers.CacheControl = "public, max-age=600";
    if (op == "fertagus") return Results.Ok(ftStationsOut);
    return Results.Ok(cpStationsOut);
});

string? fertagusTrackJson = null;
app.MapGet("/api/tracks/fertagus", async (HttpContext ctx) =>
{
    if (fertagusTrackJson == null)
    {
        var trackPath = Path.Combine(app.Environment.ContentRootPath, "fertagus_track.json");
        if (!File.Exists(trackPath))
            return Results.NotFound(new { message = "Ficheiro fertagus_track.json não encontrado." });
        fertagusTrackJson = await File.ReadAllTextAsync(trackPath);
    }
	ctx.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.Content(fertagusTrackJson, "application/json");
});

object? fertagusScheduleCache = null;
app.MapGet("/api/trains/fertagus/schedule", async (HttpContext ctx) =>
{
    if (fertagusScheduleCache != null)
	{
		ctx.Response.Headers.CacheControl = "public, max-age=3600";
		return Results.Ok(fertagusScheduleCache);
	}
	
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
                
                // Validação defensiva (evita rebentar com formatos malucos)
                if (parts.Length < 2 || !int.TryParse(parts[0], out var hh) || !int.TryParse(parts[1], out var mm)) continue;
                
                var secs = hh * 3600 + mm * 60;
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

    var stationsOut = new Dictionary<string, object>();
    foreach (var (key, code) in fertagusStationKeys)
    {
        if (!stationByCode.TryGetValue(code, out var st)) continue;

        var lat = ParseCoord(st.Latitude);
        var lon = ParseCoord(st.Longitude);
        if (lat == 0 || lon == 0) continue; 

        stationsOut[key] = new { name = st.Designation, lat, lon };
    }

    if (trips.Count == 0 || stationsOut.Count == 0)
	{
        return Results.Problem("Horários ou estações da Fertagus indisponíveis.", statusCode: 503);
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

    // Negative Cache - se estivermos em modo de falha contínua
    if (cache.TryGetValue("cp_fail", out string? falhaRecente))
        return cpLastGood != null ? Results.Ok(cpLastGood)
                                  : Results.Problem("Feed CP indisponível: " + falhaRecente, statusCode: 502);

    if (cache.TryGetValue("cp_trains", out List<CpTrainDto>? cached) && cached != null)
        return Results.Ok(cached);

    await cpLock.WaitAsync();
    try
    {
        // Re-verificar locks
        if (cache.TryGetValue("cp_fail", out falhaRecente))
            return cpLastGood != null ? Results.Ok(cpLastGood) : Results.Problem("Feed CP indisponível.", statusCode: 502);
            
        if (cache.TryGetValue("cp_trains", out cached) && cached != null)
            return Results.Ok(cached);

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ComboiosApp/1.0");

        var raw = await client.GetStringAsync(url);
        var seconds = !string.Equals(cfg["CpRealtime:DelayUnit"], "minutes", StringComparison.OrdinalIgnoreCase);
        var stopsNovas = new Dictionary<string, List<CpStopDto>>();
        
        // Passa a buscar o parser mais inteligente (shallow)
        var trains = CpFeed.Parse(raw, seconds, LookupStation, stopsNovas);
        cpStops = stopsNovas;
        
        var ttl = cfg.GetValue<int>("CpRealtime:CacheSeconds", 10);
        cache.Set("cp_trains", trains, TimeSpan.FromSeconds(ttl));
        cpLastGood = trains;

        return Results.Ok(trains);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CP] feed erro: {ex.Message}");
        // Aplica o Negative Cache durante 5 segundos para não espancar o servidor CP
        cache.Set("cp_fail", ex.Message, TimeSpan.FromSeconds(cfg.GetValue<int>("CpRealtime:FailCacheSeconds", 5)));
        
        if (cpLastGood != null) return Results.Ok(cpLastGood);
        return Results.Problem("Falha ao obter o feed CP: " + ex.Message, statusCode: 502);
    }
    finally
    {
        cpLock.Release();
    }
});

var rail = new RailGraph();

app.MapGet("/api/trains/cp/{id}/stops", (string id) =>
{
    var stops = cpStops.TryGetValue(id, out var st) ? st : new List<CpStopDto>();
    var route = rail.Route(stops);
    return Results.Ok(new { stops, path = route?.Path, stopAt = route?.At, railStatus = rail.Status });
});

var railPath = Path.Combine(app.Environment.ContentRootPath, app.Configuration["RailGraph:Path"] ?? "rail_pt.json");
_ = Task.Run(async () =>
{
    var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
    http.Timeout = TimeSpan.FromMinutes(6);
    await rail.LoadAsync(railPath, http);
});

// =========================================================================
// CARRIS METROPOLITANA
// =========================================================================
const string CmBase = "https://api.carrismetropolitana.pt/v2";
var cmLock = new SemaphoreSlim(1, 1);
List<CmVehicleDto>? cmLastGood = null;
string? cmLastError = null;

async Task<string> CmGet(IHttpClientFactory f, string path)
{
    var client = f.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(90);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ComboiosApp/1.0");
    return await client.GetStringAsync(CmBase + path);
}

async Task<Dictionary<string, CmLine>> CmLines(IHttpClientFactory f, IMemoryCache cache)
{
    if (cache.TryGetValue("cm_lines", out Dictionary<string, CmLine>? c) && c != null) return c;
    var d = new Dictionary<string, CmLine>();
    try
    {
        d = CmFeed.ParseLines(await CmGet(f, "/lines"));
        cache.Set("cm_lines", d, TimeSpan.FromHours(12));
    }
    catch (Exception ex)
    {
        cmLastError = "linhas: " + ex.Message;
        Console.WriteLine($"[CM] linhas: {ex.Message}");
        cache.Set("cm_lines", d, TimeSpan.FromMinutes(2));
    }
    return d;
}

async Task<Dictionary<string, CmStopInfo>> CmStops(IHttpClientFactory f, IMemoryCache cache)
{
    if (cache.TryGetValue("cm_stops", out Dictionary<string, CmStopInfo>? c) && c != null) return c;
    var d = new Dictionary<string, CmStopInfo>();
    try
    {
        d = CmFeed.ParseStops(await CmGet(f, "/stops"));
        cache.Set("cm_stops", d, TimeSpan.FromHours(12));
    }
    catch (Exception ex)
    {
        cmLastError = "paragens: " + ex.Message;
        Console.WriteLine($"[CM] paragens: {ex.Message}");
        cache.Set("cm_stops", d, TimeSpan.FromMinutes(2));
    }
    return d;
}

app.MapGet("/api/buses/vehicles", async (IHttpClientFactory f, IMemoryCache cache, IConfiguration cfg) =>
{
    if (cache.TryGetValue("cm_fail", out string? falhaCm))
        return cmLastGood != null ? Results.Ok(cmLastGood)
                                  : Results.Problem("Feed Carris indisponível: " + falhaCm, statusCode: 502);

    if (cache.TryGetValue("cm_vehicles", out List<CmVehicleDto>? cached) && cached != null)
        return Results.Ok(cached);

    await cmLock.WaitAsync();
    try
    {
        if (cache.TryGetValue("cm_fail", out falhaCm))
            return cmLastGood != null ? Results.Ok(cmLastGood)
                                      : Results.Problem("Feed Carris indisponível.", statusCode: 502);

        if (cache.TryGetValue("cm_vehicles", out cached) && cached != null)
            return Results.Ok(cached);

        var raw = await CmGet(f, "/vehicles");
        var lines = await CmLines(f, cache);
        var stops = await CmStops(f, cache);
        var list = CmFeed.ParseVehicles(raw, lines, stops);
        cache.Set("cm_vehicles", list, TimeSpan.FromSeconds(cfg.GetValue<int>("CarrisRealtime:CacheSeconds", 3)));
        cmLastGood = list;
        return Results.Ok(list);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CM] veículos: {ex.Message}");
        cache.Set("cm_fail", ex.Message, TimeSpan.FromSeconds(cfg.GetValue<int>("CarrisRealtime:FailCacheSeconds", 5)));
        if (cmLastGood != null) return Results.Ok(cmLastGood);
        return Results.Problem("Falha ao obter o feed da Carris Metropolitana: " + ex.Message, statusCode: 502);
    }
    finally
    {
        cmLock.Release();
    }
});

app.MapGet("/api/buses/pattern/{id}", async (string id, IHttpClientFactory f, IMemoryCache cache) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_\\[\\]-]{1,80}$")) return Results.BadRequest();
    var key = "cm_pat_" + id;
    if (cache.TryGetValue(key, out CmPatternDto? cached) && cached != null) return Results.Ok(cached);
    try
    {
        var stopDict = await CmStops(f, cache);
        var curto = System.Text.RegularExpressions.Regex.Replace(id, @"^(\[[^\]]*\])+", "");
        var candidatos = curto != id && curto.Length > 0 ? new[] { id, curto } : new[] { id };
        CmPatternRaw? pat = null;
        Exception? ultimo = null;
        foreach (var cand in candidatos)
        {
            try
            {
                pat = CmFeed.ParsePattern(await CmGet(f, "/patterns/" + Uri.EscapeDataString(cand)), stopDict, id, curto);
                if (pat.Stops.Count > 0) break;
                Console.WriteLine($"[CM] padrão {cand}: resposta sem paragens reconhecidas");
            }
            catch (Exception ex)
            {
                ultimo = ex;
                Console.WriteLine($"[CM] padrão {cand}: {ex.Message}");
            }
        }
        if (pat == null || pat.Stops.Count == 0)
            return Results.Problem($"Sem paragens para o padrão {id}" + (ultimo != null ? ": " + ultimo.Message : ""), statusCode: 502);
        List<double[]>? path = null;
        if (!string.IsNullOrEmpty(pat.ShapeId))
        {
            try { path = CmFeed.ParseShape(await CmGet(f, "/shapes/" + Uri.EscapeDataString(pat.ShapeId))); }
            catch (Exception ex) { Console.WriteLine($"[CM] forma {pat.ShapeId}: {ex.Message}"); }
        }
        var dto = new CmPatternDto(pat.Headsign, pat.Stops, path);
        cache.Set(key, dto, TimeSpan.FromHours(6));
        return Results.Ok(dto);
    }
    catch (Exception ex)
    {
        return Results.Problem("Falha ao obter o percurso: " + ex.Message, statusCode: 502);
    }
});

app.MapGet("/api/buses/status", async (IHttpClientFactory f, IMemoryCache cache) =>
{
    var lines = await CmLines(f, cache);
    var stops = await CmStops(f, cache);
    return Results.Ok(new { lines = lines.Count, stops = stops.Count, lastError = cmLastError });
});

_ = Task.Run(async () =>
{
    try
    {
        var f = app.Services.GetRequiredService<IHttpClientFactory>();
        var c = app.Services.GetRequiredService<IMemoryCache>();
        await CmLines(f, c);
        await CmStops(f, c);
    }
    catch { }
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

    public static List<CpTrainDto> Parse(string raw, bool delayInSeconds, Func<string, (double, double, string)?> lookup, Dictionary<string, List<CpStopDto>> stopsOut)
	{
		var list = new List<CpTrainDto>();
		using var doc = JsonDocument.Parse(raw);
		var arr = FindArray(doc.RootElement, 0);
		if (arr == null) return list;

		foreach (var el in arr.Value.EnumerateArray())
		{
			if (el.ValueKind != JsonValueKind.Object) continue;

			var lat = Num(Get(el, "data", "status", "latitude")) ?? Num(Get(el, "fixed", "latitude"));
			var lon = Num(Get(el, "data", "status", "longitude")) ?? Num(Get(el, "fixed", "longitude"));
			if (lat == null || lon == null) continue;
			if (lat < 36.8 || lat > 42.3 || lon < -9.7 || lon > -6.0) continue;

			// data.status.delay = segundos; fixed.delay = minutos
			var delaySec = Num(Get(el, "data", "status", "delay"));
			var delayMin = delaySec != null
				? (int)Math.Round(delaySec.Value / 60.0)
				: (int)Math.Round(Num(Get(el, "fixed", "delay")) ?? 0);
			delayMin = Math.Max(0, delayMin);

			var id = Str(Get(el, "train_id"))
				  ?? Str(Get(el, "data", "status", "trainNumber"))
				  ?? list.Count.ToString();

			var stops = ExtractStops(el, lookup);
			if (stops.Count > 1) stopsOut[id] = stops;

			list.Add(new CpTrainDto(
				id,
				lat.Value, lon.Value,
				HeadingFromStops(lat.Value, lon.Value, stops),
				delayMin,
				Str(Get(el, "db", "trainService", "designation")) ?? Str(Get(el, "fixed", "trainService", "designation")),
				Str(Get(el, "db", "trainOrigin", "designation")) ?? Str(Get(el, "fixed", "trainOrigin", "designation")),
				Str(Get(el, "db", "trainDestination", "designation")) ?? Str(Get(el, "fixed", "trainDestination", "designation"))));
		}
		return list;
	}

	// Navega por um caminho fixo; devolve null se faltar ou se for null no JSON
	static JsonElement? Get(JsonElement e, params string[] keys)
	{
		var cur = e;
		foreach (var k in keys)
		{
			if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(k, out var next)) return null;
			cur = next;
		}
		return cur.ValueKind == JsonValueKind.Null ? null : cur;
	}

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
		var arr = Get(train, "fixed", "trainStops");
		if (arr == null || arr.Value.ValueKind != JsonValueKind.Array) return result;

		foreach (var s in arr.Value.EnumerateArray())
		{
			if (s.ValueKind != JsonValueKind.Object) continue;

			var lat = Num(Get(s, "latitude"));
			var lon = Num(Get(s, "longitude"));
			var name = Str(Get(s, "station", "designation"));

			if (lat == null || lon == null)
			{
				var code = Str(Get(s, "station", "code"));
				var hit = code == null ? null : lookup(code);
				if (hit == null) continue;
				lat = hit.Value.Item1;
				lon = hit.Value.Item2;
				name ??= hit.Value.Item3;
			}

			// hora estimada (com atraso); a 1.ª paragem só tem partida, a última só chegada
			var time = Str(Get(s, "ETA")) ?? Str(Get(s, "ETD")) ?? Str(Get(s, "arrival")) ?? Str(Get(s, "departure"));
			result.Add(new CpStopDto(name ?? "", lat.Value, lon.Value, time));
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

    public static JsonElement? FindArray(JsonElement e, int depth)
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

    public static JsonElement? Direct(JsonElement obj, string[] keys)
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

    public static JsonElement? Find(JsonElement obj, string[] keys, bool preferPosition)
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
	
	public static JsonElement? FindShallow(JsonElement obj, string[] keys, bool preferPosition)
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
        return null;
    }
	
    public static double? Num(JsonElement? e)
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

    public static string? Str(JsonElement? e)
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
            if (!File.Exists(path) && File.Exists(geo)) path = geo; 

            if (!File.Exists(path))
            {
                Status = "a descarregar as linhas do OpenStreetMap (1-2 min)";
                const string q = "[out:json][timeout:300];area[\"ISO3166-1\"=\"PT\"][admin_level=2]->.pt;way[\"railway\"=\"rail\"][!\"service\"](area.pt);out geom;";
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
                if (!path.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase)) File.Delete(path); 
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
                if (seg != null && Length(seg) > 2.2 * reta + 3000) seg = null; 
            }

            if (seg != null) foreach (var n in seg) AddPt(path, Pt(_lat[n], _lon[n]));
            else if (i == 0 && cands[0].Count > 0) AddPt(path, Pt(_lat[cands[0][0].N], _lon[cands[0][0].N]));
            else AddPt(path, Pt(stops[i].Lat, stops[i].Lon));
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

public record CmLine(string Short, string Long, string? Color);
public record CmStopInfo(string Name, double Lat, double Lon);
public record CmVehicleDto(
    string Id, double Lat, double Lon, double? Heading, string? Line, string? LineName, string? Color,
    string? Pattern, string? Status, string? Stop, string? StopName, string? Occupancy, bool Stopped);
public record CmStopDto(string? Id, string Name, double Lat, double Lon);
public record CmPatternRaw(string? ShapeId, string? Headsign, List<CmStopDto> Stops);
public record CmPatternDto(string? Headsign, List<CmStopDto> Stops, List<double[]>? Path);

public static class CmFeed
{
    static string? S(JsonElement el, params string[] keys) => CpFeed.Str(CpFeed.Direct(el, keys));
    static double? N(JsonElement el, params string[] keys) => CpFeed.Num(CpFeed.Direct(el, keys));

    static string? Cor(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return null;
        c = c.Trim();
        if (c.StartsWith('#')) return c;
        return c.Length == 6 && c.All(Uri.IsHexDigit) ? "#" + c : null;
    }

    static string? OcupacaoValida(string? o) => string.IsNullOrEmpty(o) || o == "NO_DATA_AVAILABLE" ? null : o;

    public static Dictionary<string, CmLine> ParseLines(string raw)
    {
        var d = new Dictionary<string, CmLine>();
        using var doc = JsonDocument.Parse(raw);
        var arr = CpFeed.FindArray(doc.RootElement, 0);
        if (arr == null) return d;
        foreach (var el in arr.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = S(el, "id", "line_id");
            if (id == null) continue;
            d[id] = new CmLine(S(el, "short_name", "shortName") ?? id, S(el, "long_name", "longName", "name") ?? "", Cor(S(el, "color")));
        }
        return d;
    }

    public static Dictionary<string, CmStopInfo> ParseStops(string raw)
    {
        var d = new Dictionary<string, CmStopInfo>();
        using var doc = JsonDocument.Parse(raw);
        var arr = CpFeed.FindArray(doc.RootElement, 0);
        if (arr == null) return d;
        foreach (var el in arr.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = S(el, "id", "stop_id");
            var name = S(el, "long_name") ?? S(el, "name", "stop_name") ?? S(el, "short_name") ?? id;
            var lat = N(el, "lat", "stop_lat", "latitude");
            var lon = N(el, "lon", "stop_lon", "longitude");
            if (id != null && name != null && lat != null && lon != null) d[id] = new CmStopInfo(name, lat.Value, lon.Value);
        }
        return d;
    }

    public static List<CmVehicleDto> ParseVehicles(string raw, Dictionary<string, CmLine> lines, Dictionary<string, CmStopInfo> stops)
    {
        var list = new List<CmVehicleDto>();
        using var doc = JsonDocument.Parse(raw);
        var arr = CpFeed.FindArray(doc.RootElement, 0);
        if (arr == null) return list;
        var agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var el in arr.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var lat = N(el, "lat", "latitude");
            var lon = N(el, "lon", "lng", "longitude");
            var id = S(el, "id");
            if (lat == null || lon == null || id == null) continue;
            if (lat < 36.8 || lat > 39.6 || lon < -10.0 || lon > -7.3) continue;

            var ts = N(el, "timestamp");
            if (ts != null)
            {
                var t = ts.Value > 1e12 ? ts.Value / 1000 : ts.Value;
                if (agora - t > 20 * 60) continue; 
            }

            var speed = N(el, "speed");
            var bearing = N(el, "bearing", "heading");
            var status = S(el, "current_status");
            double? heading = bearing != null && ((speed ?? 1) > 0 || bearing != 0) ? bearing : null;

            var lineId = S(el, "line_id");
            CmLine? line = null;
            if (lineId != null) lines.TryGetValue(lineId, out line);
            var stopId = S(el, "stop_id");
            if (stopId == "UNAVAILABLE_STOP_ID" || stopId == "") stopId = null;
            string? stopName = null;
            if (stopId != null && stops.TryGetValue(stopId, out var sn)) stopName = sn.Name;

            list.Add(new CmVehicleDto(
                id, lat.Value, lon.Value, heading,
                line?.Short ?? lineId, line?.Long, line?.Color,
                S(el, "pattern_id"), status, stopId, stopName,
                OcupacaoValida(S(el, "occupancy_status", "occupancy")),
                status == "STOPPED_AT" || (speed != null && speed == 0)));
        }
        return list;
    }

    public static CmPatternRaw ParsePattern(string raw, Dictionary<string, CmStopInfo> stopDict, params string[] ids)
    {
        var vazio = new CmPatternRaw(null, null, new List<CmStopDto>());
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) root = data;

        var ver = root;
        if (root.ValueKind == JsonValueKind.Array)
        {
            var hoje = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Lisboa).ToString("yyyyMMdd"); 
            JsonElement? pick = null;
            var score = -1;
            foreach (var e in root.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var sc = 0;
                var eid = CpFeed.Str(CpFeed.Direct(e, new[] { "id", "pattern_id" }));
                if (eid != null && ids.Contains(eid)) sc += 2;
                var v = CpFeed.Direct(e, new[] { "valid_on" });
                if (v != null && v.Value.ValueKind == JsonValueKind.Array &&
                    v.Value.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == hoje)) sc += 1;
                if (sc > score) { score = sc; pick = e; }
            }
            if (pick == null) return vazio;
            ver = pick.Value;
        }
        if (ver.ValueKind != JsonValueKind.Object) return vazio;

        var shapeId = CpFeed.Str(CpFeed.Find(ver, new[] { "shape_id" }, false));
        var headsign = CpFeed.Str(CpFeed.Find(ver, new[] { "headsign" }, false));

        JsonElement? path = null;
        foreach (var p in ver.EnumerateObject())
            if ((p.Name == "path" || p.Name == "stops") && p.Value.ValueKind == JsonValueKind.Array) { path = p.Value; break; }
        if (path == null) return new CmPatternRaw(shapeId, headsign, new List<CmStopDto>());

        var stops = new List<(double Seq, CmStopDto Stop)>();
        foreach (var el in path.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var sid = CpFeed.Str(CpFeed.FindShallow(el, new[] { "stop_id" }, false)) ?? CpFeed.Str(CpFeed.FindShallow(el, new[] { "id" }, false));
            var lat = CpFeed.Num(CpFeed.FindShallow(el, new[] { "lat", "latitude" }, false));
            var lon = CpFeed.Num(CpFeed.FindShallow(el, new[] { "lon", "lng", "longitude" }, false));
            var name = CpFeed.Str(CpFeed.FindShallow(el, new[] { "name", "stop_name", "short_name" }, false));
            if ((lat == null || lon == null) && sid != null && stopDict.TryGetValue(sid, out var info))
            {
                lat = info.Lat;
                lon = info.Lon;
                name ??= info.Name;
            }
            if (lat == null || lon == null) continue;
            var seq = CpFeed.Num(CpFeed.FindShallow(el, new[] { "stop_sequence" }, false)) ?? stops.Count;
            stops.Add((seq, new CmStopDto(sid, name ?? "", lat.Value, lon.Value)));
        }
        return new CmPatternRaw(shapeId, headsign, stops.OrderBy(x => x.Seq).Select(x => x.Stop).ToList());
    }

    // Forma do percurso (GeoJSON LineString) -> [[lon,lat],...]
    public static List<double[]>? ParseShape(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var c = FindCoords(doc.RootElement, 0);
        return c?.Select(p => new[] { Math.Round(p[0], 5), Math.Round(p[1], 5) }).ToList();
    }

    static List<double[]>? FindCoords(JsonElement e, int depth)
    {
        if (depth > 5 || e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
        {
            var v = p.Value;
            if (p.Name.Equals("coordinates", StringComparison.OrdinalIgnoreCase) && v.ValueKind == JsonValueKind.Array &&
                v.GetArrayLength() > 1 && v[0].ValueKind == JsonValueKind.Array && v[0].GetArrayLength() >= 2 &&
                v[0][0].ValueKind == JsonValueKind.Number)
                return v.EnumerateArray().Select(c => new[] { c[0].GetDouble(), c[1].GetDouble() }).ToList();
        }
        foreach (var p in e.EnumerateObject())
        {
            var r = FindCoords(p.Value, depth + 1);
            if (r != null) return r;
        }
        return null;
    }
	
	static readonly TimeZoneInfo Lisboa = ObterFusoLisboa();

    static TimeZoneInfo ObterFusoLisboa()
    {
        foreach (var id in new[] { "Europe/Lisbon", "GMT Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        Console.WriteLine("[CM] Fuso de Lisboa não encontrado, a usar UTC.");
        return TimeZoneInfo.Utc;
}
}
