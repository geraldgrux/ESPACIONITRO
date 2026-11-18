using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Collections.Concurrent;

var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
var groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";

// --- MINI SERVIDOR KEEP-ALIVE PARA UPTIMEROBOT ---
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

// --- MEMORIA POR CHAT ---
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

        // --- TIEMPO REAL ---
        if (textoBajo.Contains("chile") && (textoBajo.Contains("colombia") || textoBajo.Contains("partido") || textoBajo.Contains("jugo") || textoBajo.Contains("jugó") || textoBajo.Contains("resultado")))
        {
            var r = await BuscarPartido("Chile_vs_Colombia");
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

        // --- CHAT NORMAL CON MEMORIA ---
        var lista = historial.GetOrAdd(chatId, _ => new List<object>());
        lista.Add(new { role = "user", content = mensaje });
        if (lista.Count > 20) lista.RemoveRange(0, lista.Count - 20);
        var respuesta = await PreguntarGroq(lista);
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

async Task<string> BuscarPartido(string busqueda)
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 ESPACIONITRO");
        
        // ESPN no pide key y no bloquea. Buscamos hoy y ayer
        var hoy = DateTime.UtcNow.ToString("yyyyMMdd");
        var ayer = DateTime.UtcNow.AddDays(-1).ToString("yyyyMMdd");
        
        foreach (var fecha in new[] { hoy, ayer })
        {
            var url = $"https://site.api.espn.com/apis/site/v2/sports/soccer/all/scoreboard?dates={fecha}";
            var jsonStr = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(jsonStr);
            
            if (!doc.RootElement.TryGetProperty("events", out var eventos)) continue;
            
            foreach (var ev in eventos.EnumerateArray())
            {
                var nombre = ev.GetProperty("name").GetString() ?? "";
                var partes = busqueda.Replace("_vs_", " ").Replace("_", " ").ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var nombreBajo = nombre.ToLower();
                bool coincide = partes.All(p => nombreBajo.Contains(p));
                
                if (!coincide) continue;

                var comp = ev.GetProperty("competitions")[0];
                var competidores = comp.GetProperty("competitors");
                // ESPN a veces pone visita primero, ordenamos por homeAway
                JsonElement local = default, visita = default;
                foreach (var c in competidores.EnumerateArray())
                {
                    var ha = c.GetProperty("homeAway").GetString();
                    if (ha == "home") local = c;
                    if (ha == "away") visita = c;
                }
                if (local.ValueKind == JsonValueKind.Undefined) local = competidores[0];
                if (visita.ValueKind == JsonValueKind.Undefined) visita = competidores[1];

                var nombreLocal = local.GetProperty("team").GetProperty("displayName").GetString();
                var nombreVisita = visita.GetProperty("team").GetProperty("displayName").GetString();
                var golLocal = local.TryGetProperty("score", out var gl) ? gl.GetString() ?? "?" : "?";
                var golVisita = visita.TryGetProperty("score", out var gv) ? gv.GetString() ?? "?" : "?";
                var estado = comp.GetProperty("status").GetProperty("type").GetProperty("description").GetString();
                var liga = ev.GetProperty("league").GetProperty("name").GetString();

                return $"Resultado en vivo:\n{nombreLocal} {golLocal} - {golVisita} {nombreVisita}\nLiga: {liga}\nEstado: {estado}\nFecha: {fecha}";
            }
        }
        return "No encontré ese partido en hoy ni ayer en ESPN. Prueba con /partido Chile_vs_Colombia";
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

async Task<string> PreguntarGroq(List<object> historialChat)
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {groqKey}");
        var messages = new List<object>
        {
            new { role = "system", content = "Eres ESPACIONITRO. Habla español neutro, claro, natural y con sentido. Prohibido usar modismos chilenos forzados como bacán, po, cachai, al tiro, weon o similares. No hables como caricatura. Habla como una persona normal, educada y amable. Sigue la conversación, recuerda lo que el usuario te dijo antes. Si te preguntan por un partido, clima o noticia en vivo, di que use los comandos /partido, /clima o /nasa, porque tu no tienes internet en vivo, esos comandos si lo buscan. Nunca uses ** ni markdown, solo texto plano." }
        };
        messages.AddRange(historialChat);
        var body = new { model = "openai/gpt-oss-20b", messages = messages, temperature = 0.6, max_tokens = 800 };
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