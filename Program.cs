using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
var groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
var listener = new HttpListener();
listener.Prefixes.Add($"http://*:{port}/");
listener.Start();
Console.WriteLine($"Servidor keep-alive en puerto {port}");
_ = Task.Run(async () =>
{
    while (true)
    {
        var ctx = await listener.GetContextAsync();
        var bytes = Encoding.UTF8.GetBytes("ESPACIONITRO esta vivo");
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }
});

var historial = new ConcurrentDictionary<long, List<object>>();
var bot = new TelegramBotClient(telegramToken);
var me = await bot.GetMe();
Console.WriteLine($"Bot conectado: @{me.Username}");
var cts = new CancellationTokenSource();
bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions { AllowedUpdates = { } }, cts.Token);
Console.WriteLine("Bot esperando mensajes...");
await Task.Delay(-1);

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken token)
{
    try
    {
        if (update.Type != UpdateType.Message) return;
        if (update.Message?.Text is not { } mensaje) return;
        var chatId = update.Message.Chat.Id;
        var textoBajo = mensaje.ToLower();

        if (textoBajo.Contains("chile") && (textoBajo.Contains("colombia") || textoBajo.Contains("partido") || textoBajo.Contains("jugo") || textoBajo.Contains("jugó") || textoBajo.Contains("resultado") || textoBajo.Contains("como quedo") || textoBajo.Contains("como quedó") || textoBajo.Contains("quien gano") || textoBajo.Contains("quién ganó")))
        {
            var r = await BuscarPartido("chile colombia");
            await botClient.SendMessage(chatId, r, cancellationToken: token);
            return;
        }
        if (textoBajo.StartsWith("/partido"))
        {
            var equipo = mensaje.Replace("/partido", "").Trim();
            if (string.IsNullOrEmpty(equipo)) equipo = "Chile";
            var r = await BuscarPartido(equipo);
            await botClient.SendMessage(chatId, r, cancellationToken: token);
            return;
        }
        if (textoBajo.StartsWith("/clima") || textoBajo.Contains("clima en") || textoBajo.Contains("como esta el clima"))
        {
            var r = await BuscarClima();
            await botClient.SendMessage(chatId, r, cancellationToken: token);
            return;
        }
        if (textoBajo.StartsWith("/nasa") || textoBajo.Contains("foto nasa") || textoBajo.Contains("foto del dia"))
        {
            var r = await BuscarNasa();
            await botClient.SendMessage(chatId, r, cancellationToken: token);
            return;
        }
        if (textoBajo.StartsWith("/contingencia") || textoBajo.Contains("contingencia") || textoBajo.Contains("que esta pasando en chile") || textoBajo.Contains("que pasa en chile") || textoBajo.Contains("que paso en chile"))
        {
            var r = await BuscarNoticias("Chile contingencia hoy", "Contingencia Chile");
            await botClient.SendMessage(chatId, r, cancellationToken: token);
            return;
        }
        if (textoBajo.StartsWith("/actualidad") || textoBajo.StartsWith("/noticias") || textoBajo.Contains("noticias de hoy") || textoBajo.Contains("actualidad"))
        {
            var r = await BuscarNoticias("actualidad mundo hoy", "Actualidad");
            await botClient.SendMessage(chatId, r, cancellationToken: token);
            return;
        }
        if (textoBajo.StartsWith("/espacio") || textoBajo.Contains("noticias del espacio") || textoBajo.Contains("noticias espacio"))
        {
            var r = await BuscarNoticias("NASA espacio astronomia", "Noticias del Espacio");
            await botClient.SendMessage(chatId, r, cancellationToken: token);
            return;
        }
        if (textoBajo.StartsWith("/arquitectura") || textoBajo.StartsWith("/archi") || textoBajo.Contains("arquitectura"))
        {
            var tema = mensaje.Replace("/arquitectura", "").Replace("/archi", "").Trim();
            if (string.IsNullOrEmpty(tema)) tema = "tendencias arquitectura 2026";
            var r = await BuscarArquitectura(tema);
            await botClient.SendMessage(chatId, r, cancellationToken: token);
            return;
        }

        var lista = historial.GetOrAdd(chatId, _ => new List<object>());
        lista.Add(new { role = "user", content = mensaje });
        if (lista.Count > 20) lista.RemoveRange(0, lista.Count - 20);
        var respuesta = await PreguntarGroq(lista, false);
        lista.Add(new { role = "assistant", content = respuesta });
        if (lista.Count > 20) lista.RemoveRange(0, lista.Count - 20);
        await botClient.SendMessage(chatId, respuesta, cancellationToken: token);
    }
    catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
}

Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken token)
{
    Console.WriteLine($"Error Telegram: {exception.Message}");
    return Task.CompletedTask;
}

string GetStr(JsonElement el, string key)
{
    if (el.ValueKind != JsonValueKind.Object) return "";
    if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
    return "";
}

// Traduce el estado de ESPN a español que se entienda
string TraducirEstado(string estadoIngles, string displayClock, string shortDetail)
{
    var e = (estadoIngles + " " + shortDetail).ToLower();
    if (e.Contains("full time") || e.Contains("final")) return "Ya terminó";
    if (e.Contains("half")) return "Entretiempo - Están en el descanso";
    if (e.Contains("in progress") || e.Contains("live") || e.Contains("'")) return $"Están jugando ahora mismo - Minuto {displayClock}";
    if (e.Contains("scheduled") || e.Contains("pregame") || e.Contains("not started")) return "Aún no empieza";
    if (e.Contains("postponed")) return "Se suspendió";
    if (e.Contains("cancelled")) return "Se canceló";
    if (!string.IsNullOrEmpty(displayClock) && displayClock.Contains("'")) return $"Están jugando - {displayClock}";
    return "Ya terminó";
}

async Task<string> BuscarPartido(string busqueda)
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 ESPACIONITRO");
        var fechas = new[]
        {
            DateTime.UtcNow.ToString("yyyyMMdd"),
            DateTime.UtcNow.AddDays(-1).ToString("yyyyMMdd"),
            DateTime.UtcNow.AddDays(-2).ToString("yyyyMMdd")
        };
        var partes = busqueda.ToLower().Replace("_vs_", " ").Replace("_", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var fecha in fechas)
        {
            string jsonStr;
            try
            {
                var url = $"https://site.api.espn.com/apis/site/v2/sports/soccer/all/scoreboard?dates={fecha}";
                jsonStr = await http.GetStringAsync(url);
            }
            catch { continue; }
            using var doc = JsonDocument.Parse(jsonStr);
            if (!doc.RootElement.TryGetProperty("events", out var eventos)) continue;
            if (eventos.ValueKind != JsonValueKind.Array) continue;
            foreach (var ev in eventos.EnumerateArray())
            {
                try
                {
                    var nombre = GetStr(ev, "name");
                    if (string.IsNullOrEmpty(nombre)) continue;
                    var nombreBajo = nombre.ToLower();
                    if (!partes.All(p => nombreBajo.Contains(p))) continue;
                    if (!ev.TryGetProperty("competitions", out var comps)) continue;
                    if (comps.ValueKind != JsonValueKind.Array || comps.GetArrayLength() == 0) continue;
                    var comp = comps[0];
                    if (!comp.TryGetProperty("competitors", out var competidores)) continue;

                    string nombreLocal = "?", nombreVisita = "?", golLocal = "?", golVisita = "?";
                    foreach (var c in competidores.EnumerateArray())
                    {
                        var ha = GetStr(c, "homeAway");
                        string teamName = "";
                        if (c.TryGetProperty("team", out var team)) teamName = GetStr(team, "displayName");
                        string score = "?";
                        if (c.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.String) score = sc.GetString() ?? "?";
                        if (ha == "home") { nombreLocal = teamName; golLocal = score; }
                        if (ha == "away") { nombreVisita = teamName; golVisita = score; }
                    }
                    if (nombreLocal == "?")
                    {
                        var arr = competidores.EnumerateArray().ToList();
                        if (arr.Count >= 2)
                        {
                            if (arr[0].TryGetProperty("team", out var t0)) nombreLocal = GetStr(t0, "displayName");
                            if (arr[1].TryGetProperty("team", out var t1)) nombreVisita = GetStr(t1, "displayName");
                            if (arr[0].TryGetProperty("score", out var s0) && s0.ValueKind == JsonValueKind.String) golLocal = s0.GetString() ?? "?";
                            if (arr[1].TryGetProperty("score", out var s1) && s1.ValueKind == JsonValueKind.String) golVisita = s1.GetString() ?? "?";
                        }
                    }

                    // Datos del partido para explicar
                    string estadoOriginal = "";
                    string reloj = "";
                    string detalleCorto = "";
                    string estadio = "";
                    string fechaPartido = GetStr(ev, "date");
                    if (comp.TryGetProperty("status", out var st))
                    {
                        if (st.TryGetProperty("type", out var stType))
                        {
                            estadoOriginal = GetStr(stType, "description");
                            detalleCorto = GetStr(stType, "shortDetail");
                        }
                        reloj = GetStr(st, "displayClock");
                    }
                    if (comp.TryGetProperty("venue", out var venue)) estadio = GetStr(venue, "fullName");
                    if (string.IsNullOrEmpty(estadio) && comp.TryGetProperty("venue", out var v2)) estadio = GetStr(v2, "shortName");

                    string liga = "";
                    if (ev.TryGetProperty("league", out var lg)) liga = GetStr(lg, "name");
                    if (string.IsNullOrEmpty(liga)) liga = "Amistoso Internacional";

                    string estadoEsp = TraducirEstado(estadoOriginal, reloj, detalleCorto);

                    // Explicación en español normal
                    string explicacion = "";
                    int gL = int.TryParse(golLocal, out var glInt) ? glInt : -1;
                    int gV = int.TryParse(golVisita, out var gvInt) ? gvInt : -1;

                    if (estadoEsp == "Ya terminó")
                    {
                        if (gL == gV)
                        {
                            if (gL == 0) explicacion = $"Empataron 0-0, no hubo goles. Fue un partido sin goles, nadie ganó.";
                            else explicacion = $"Empataron {gL}-{gV}, nadie ganó, quedaron igual.";
                        }
                        else if (gL > gV) explicacion = $"Ganó {nombreLocal} por {gL}-{gV} contra {nombreVisita}.";
                        else explicacion = $"Ganó {nombreVisita} por {gV}-{gL} contra {nombreLocal}.";
                    }
                    else if (estadoEsp.StartsWith("Están jugando"))
                    {
                        if (gL == gV) explicacion = $"Van empatando {gL}-{gV} en este momento.";
                        else if (gL > gV) explicacion = $"Va ganando {nombreLocal} {gL}-{gV} en este momento.";
                        else explicacion = $"Va ganando {nombreVisita} {gV}-{gL} en este momento.";
                    }
                    else if (estadoEsp == "Aún no empieza")
                    {
                        explicacion = $"Todavía no juegan, el partido está programado.";
                    }
                    else if (estadoEsp.Contains("Entretiempo"))
                    {
                        explicacion = $"Están en el descanso, el marcador va {gL}-{gV}.";
                    }
                    else explicacion = $"Marcador actual: {gL}-{gV}.";

                    // Fecha en hora Chile
                    string fechaChilena = fecha;
                    try
                    {
                        if (DateTime.TryParse(fechaPartido, out var dt)) 
{
    var chile = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
    var dtChile = TimeZoneInfo.ConvertTimeFromUtc(dt.ToUniversalTime(), chile);
    fechaChilena = dtChile.ToString("dd/MM/yyyy HH:mm") + " hora Chile";
}
                    }
                    catch { }

                    var sb = new StringBuilder();
                    sb.AppendLine($"{nombreLocal} {golLocal} - {golVisita} {nombreVisita}");
                    sb.AppendLine($"{estadoEsp}");
                    sb.AppendLine("");
                    sb.AppendLine(explicacion);
                    sb.AppendLine("");
                    sb.AppendLine($"Torneo: {liga}");
                    if (!string.IsNullOrEmpty(estadio)) sb.AppendLine($"Estadio: {estadio}");
                    sb.AppendLine($"Fecha: {fechaChilena}");

                    return sb.ToString();
                }
                catch { continue; }
            }
        }
        return "No encontré ese partido en los últimos 3 días. Prueba con /partido Chile";
    }
    catch (Exception ex) { return $"No pude buscar el partido ahora: {ex.Message}"; }
}

async Task<string> BuscarClima()
{
    try
    {
        using var http = new HttpClient();
        var url = "https://api.open-meteo.com/v1/forecast?latitude=-33.45&longitude=-70.66&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&timezone=America%2FSantiago";
        var jsonStr = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(jsonStr);
        var cur = doc.RootElement.GetProperty("current");
        var temp = cur.GetProperty("temperature_2m").GetDouble();
        var hum = cur.GetProperty("relative_humidity_2m").GetInt32();
        var viento = cur.GetProperty("wind_speed_10m").GetDouble();
        return $"Clima en Santiago ahora mismo:\nTemperatura: {temp}°C\nHumedad: {hum}%\nViento: {viento} km/h";
    }
    catch (Exception ex) { return $"No pude ver el clima: {ex.Message}"; }
}

async Task<string> BuscarNasa()
{
    try
    {
        using var http = new HttpClient();
        var url = "https://api.nasa.gov/planetary/apod?api_key=DEMO_KEY";
        var jsonStr = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(jsonStr);
        var titulo = doc.RootElement.GetProperty("title").GetString();
        var desc = doc.RootElement.GetProperty("explanation").GetString();
        var img = doc.RootElement.GetProperty("url").GetString();
        var descCorta = desc!.Length > 400 ? desc.Substring(0, 400) + "..." : desc;
        return $"Foto del día NASA: {titulo}\n{descCorta}\nVer foto: {img}";
    }
    catch (Exception ex) { return $"No pude traer la NASA: {ex.Message}"; }
}

async Task<string> BuscarNoticias(string query, string titulo)
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 ESPACIONITRO");
        var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl=es-419&gl=CL&ceid=CL:es-419";
        var xmlStr = await http.GetStringAsync(url);
        var doc = XDocument.Parse(xmlStr);
        var items = doc.Descendants("item").Take(5).ToList();
        if (items.Count == 0) return "No encontré noticias ahora, intenta de nuevo en un rato.";
        var sb = new StringBuilder();
        sb.AppendLine($"{titulo} - Hoy:");
        sb.AppendLine("");
        int i = 1;
        foreach (var it in items)
        {
            var t = it.Element("title")?.Value ?? "";
            var link = it.Element("link")?.Value ?? "";
            sb.AppendLine($"{i}. {t}");
            if (!string.IsNullOrEmpty(link)) sb.AppendLine($"   {link}");
            i++;
        }
        return sb.ToString();
    }
    catch (Exception ex) { return $"No pude traer noticias: {ex.Message}"; }
}

async Task<string> BuscarArquitectura(string tema)
{
    try
    {
        var explicacion = await PreguntarGroq(new List<object> { new { role = "user", content = tema } }, true);
        string proyectos = "";
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 ESPACIONITRO");
            var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString("arquitectura " + tema + " ArchDaily")}&hl=es-419&gl=CL&ceid=CL:es-419";
            var xmlStr = await http.GetStringAsync(url);
            var doc = XDocument.Parse(xmlStr);
            var items = doc.Descendants("item").Take(3).ToList();
            if (items.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("");
                sb.AppendLine("Proyectos / Articulos reales:");
                foreach (var it in items)
                {
                    sb.AppendLine($"- {it.Element("title")?.Value}");
                    sb.AppendLine($"  {it.Element("link")?.Value}");
                }
                proyectos = sb.ToString();
            }
        }
        catch { }
        return $"MODO ARQUITECTO:\n\n{explicacion}{proyectos}";
    }
    catch (Exception ex) { return $"Error en modo arquitecto: {ex.Message}"; }
}

async Task<string> PreguntarGroq(List<object> historialChat, bool modoArquitecto)
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {groqKey}");
        string systemPrompt;
        if (modoArquitecto)
        {
            systemPrompt = "Eres ESPACIONITRO en MODO ARQUITECTO. Eres un arquitecto experto, con conocimiento de historia de la arquitectura, estilos, materiales, estructuras, luz, paisaje, normativa chilena, y referentes como Le Corbusier, Zaha Hadid, Smiljan Radic, Alejandro Aravena, Elemental. Explica con claridad, con ejemplos reales, medidas cuando corresponda, y da ideas de diseño concretas. Habla español neutro, normal, sin modismos chilenos forzados. Nunca uses ** ni markdown, solo texto plano.";
        }
        else
        {
            systemPrompt = "Eres ESPACIONITRO. Habla español neutro, claro, natural y con sentido. Prohibido usar modismos chilenos forzados como bacán, po, cachai, al tiro, weon o similares. No hables como caricatura. Habla como una persona normal, educada y amable. Sigue la conversación, recuerda lo que el usuario te dijo antes. Si te preguntan por un partido, clima, contingencia o noticia en vivo, di que use los comandos /partido, /clima, /contingencia, /actualidad o /nasa, porque tu no tienes internet en vivo, esos comandos si lo buscan. Nunca uses ** ni markdown, solo texto plano.";
        }
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };
        messages.AddRange(historialChat);
        var body = new { model = "openai/gpt-oss-20b", messages = messages, temperature = 0.6, max_tokens = 900 };
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
        var textoRespuesta = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return $"Groq fallo: {response.StatusCode}";
        using var doc = JsonDocument.Parse(textoRespuesta);
        var respuesta = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "Sin respuesta";
        return respuesta.Replace("**", "").Replace("__", "").Replace("### ", "").Replace("## ", "").Replace("# ", "").Replace("`", "");
    }
    catch (Exception ex) { return $"Ups, tuve un error: {ex.Message}"; }
}