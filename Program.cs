using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
var whatsappToken = Environment.GetEnvironmentVariable("WHATSAPP_TOKEN") ?? "";
var phoneId = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_ID") ?? "";
var verifyToken = Environment.GetEnvironmentVariable("VERIFY_TOKEN") ?? "espacionitro123";

var http = new HttpClient();
http.DefaultRequestHeaders.Add("User-Agent", "EspacioNitro/1.0");
var memoria = new Dictionary<string, List<string>>();

app.MapGet("/", () => "ESPACIONITRO OK");
app.MapGet("/health", () => "OK");

app.MapGet("/webhook", (string? hub_mode, string? hub_verify_token, string? hub_challenge) =>
{
    if (hub_mode == "subscribe" && hub_verify_token == verifyToken) return Results.Text(hub_challenge ?? "");
    return Results.Unauthorized();
});

app.MapPost("/webhook", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    try
    {
        var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value").GetProperty("messages");
        foreach (var msg in messages.EnumerateArray())
        {
            var from = msg.GetProperty("from").GetString() ?? "";
            var text = msg.GetProperty("text").GetProperty("body").GetString() ?? "";
            var respuesta = await ProcesarMensaje(from, text);
            await EnviarWhatsApp(from, respuesta);
        }
    }
    catch (Exception ex) { Console.WriteLine(ex.Message); }
    return Results.Ok();
});

async Task<string> ProcesarMensaje(string from, string textoOriginal)
{
    var texto = textoOriginal.Trim();
    var lower = texto.ToLower();

    if (!memoria.ContainsKey(from)) memoria[from] = new List<string>();
    memoria[from].Add("Usuario: " + texto);
    if (memoria[from].Count > 12) memoria[from] = memoria[from].Skip(memoria[from].Count - 12).ToList();

    if (lower.Contains("clima")) return await GetClima();
    if (lower.Contains("nasa") || lower.Contains("/nasa")) return await GetNasa();
    if (lower.Contains("partido") || lower.Contains("chile vs") || lower.Contains("jugo chile") || lower.Contains("jugó chile") || lower.Contains("/partido")) return await GetPartido();

    if (lower.Contains("/contingencia") || lower.Contains("/actualidad") || lower.Contains("/noticias") ||
        lower.Contains("que esta pasando") || lower.Contains("qué está pasando") ||
        lower.Contains("que paso hoy") || lower.Contains("qué pasó hoy") ||
        lower.Contains("que paso en santiago") || lower.Contains("qué pasó en santiago") ||
        lower.Contains("que paso en chile") || lower.Contains("contingencia santiago"))
        return await GetContingencia();

    if (lower.Contains("/espacio") || lower.Contains("noticias del espacio")) return await GetContingencia();

    if (lower.Contains("/arquitectura") || lower.Contains("/arquitecto") || lower.Contains("arquitect") ||
        lower.Contains("cabaña") || lower.Contains("cabana") || lower.Contains("casa minimalista") ||
        lower.Contains("quiero una casa") || lower.Contains("quiero construir") || lower.Contains("disena mi casa") || lower.Contains("diseña mi casa"))
        return await GetArquitectura(texto);

    var historial = string.Join("\n", memoria[from]);
    var prompt = $"Eres ESPACIONITRO, amigo chileno que sabe de futbol, actualidad, espacio y arquitectura. Hablas claro, sin decir bacan/po/cachai a cada rato. Historial:\n{historial}\nUsuario: {texto}\nResponde corto y util en espanol chileno neutro.";
    var resp = await PreguntarGroq(prompt);
    memoria[from].Add("Bot: " + resp);
    return resp;
}

async Task<string> GetContingencia()
{
    try
    {
        var rss = await http.GetStringAsync("https://news.google.com/rss/search?q=Chile%20Santiago%20hoy&hl=es-419&gl=CL&ceid=CL:es-419");
        var xdoc = XDocument.Parse(rss);
        var items = xdoc.Descendants("item").Take(5).Select(i => i.Element("title")?.Value ?? "").ToList();
        if (items.Count == 0) return "No encontré noticias frescas de Chile ahora.";
        var lista = string.Join("\n", items.Select((x, i) => $"{i + 1}. {x}"));
        var prompt = $"Eres periodista chileno. Resume estas 5 noticias de hoy en Chile en lenguaje simple, como amigo por WhatsApp. Maximo 5 lineas, sin links, sin URLs. No inventes.\nNoticias:\n{lista}";
        var resumen = await PreguntarGroq(prompt);
        return resumen + "\n\n¿Quieres que te explique mas alguna?";
    }
    catch { return "No pude traer la contingencia ahora, prueba en 2 minutos."; }
}

async Task<string> GetArquitectura(string pedido)
{
    var prompt = $"Actua como arquitecto chileno experto, referencias: Alejandro Aravena, Smiljan Radic, Pezo von Ellrichshausen, Mathias Klotz. Usuario pide: {pedido}. Responde como arquitecto amigo, no como Google. Estructura: 1. Idea general 2 lineas, estilo que calza en Chile. 2. Materiales para sur de Chile: madera, techo, aislacion. 3. Distribucion: m2, orientacion sol y vista. 4. 1 referente real de ArchDaily Chile. 5. 1 tip que ahorre plata. Maximo 18 lineas, habla de Chile, lluvia, madera, vista lago/bosque.";
    return await PreguntarGroq(prompt);
}

async Task<string> GetPartido()
{
    try
    {
        var json = await http.GetStringAsync("https://site.api.espn.com/apis/site/v2/sports/soccer/chile.1/scoreboard?limit=5");
        var doc = JsonDocument.Parse(json);
        var ev = doc.RootElement.GetProperty("events")[0];
        var comp = ev.GetProperty("competitions")[0];
        var estado = comp.GetProperty("status").GetProperty("type").GetProperty("description").GetString() ?? "";
        var local = comp.GetProperty("competitors").EnumerateArray().First(c => c.GetProperty("homeAway").GetString() == "home");
        var visita = comp.GetProperty("competitors").EnumerateArray().First(c => c.GetProperty("homeAway").GetString() == "away");
        var golesLocal = local.GetProperty("score").GetString() ?? "0";
        var golesVisita = visita.GetProperty("score").GetString() ?? "0";
        var nombreLocal = local.GetProperty("team").GetProperty("displayName").GetString() ?? "";
        var nombreVisita = visita.GetProperty("team").GetProperty("displayName").GetString() ?? "";
        var fechaPartido = ev.GetProperty("date").GetString() ?? "";
        var estadio = comp.GetProperty("venue").GetProperty("fullName").GetString() ?? "";

        string estadoEsp = estado == "Full Time" ? "Ya terminó" : estado == "In Progress" ? "Están jugando" : estado == "Scheduled" ? "Aún no empieza" : estado;
        string resultado = "";
        if (estado == "Full Time")
        {
            if (golesLocal == golesVisita) resultado = $"Empataron {golesLocal}-{golesVisita}, no hubo goles. Nadie ganó.";
            else if (int.Parse(golesLocal) > int.Parse(golesVisita)) resultado = $"Ganó {nombreLocal} {golesLocal}-{golesVisita} a {nombreVisita}.";
            else resultado = $"Ganó {nombreVisita} {golesVisita}-{golesLocal} a {nombreLocal}.";
        }
        else resultado = $"Van {nombreLocal} {golesLocal} - {golesVisita} {nombreVisita}.";

        string fechaChilena = fechaPartido;
        if (DateTime.TryParse(fechaPartido, out var dt))
        {
            try
            {
                var chile = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
                var dtChile = TimeZoneInfo.ConvertTimeFromUtc(dt.ToUniversalTime(), chile);
                fechaChilena = dtChile.ToString("dd/MM/yyyy HH:mm") + " hora Chile";
            }
            catch { fechaChilena = dt.ToString("dd/MM/yyyy HH:mm"); }
        }
        return $"Partido - {estadoEsp}\n{nombreLocal} {golesLocal} - {golesVisita} {nombreVisita}\n{resultado}\nEstadio: {estadio}\nFecha: {fechaChilena}";
    }
    catch (Exception ex) { return "No pude traer el partido: " + ex.Message; }
}

async Task<string> GetClima()
{
    try
    {
        var json = await http.GetStringAsync("https://api.open-meteo.com/v1/forecast?latitude=-33.45&longitude=-70.66&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&timezone=America%2FSantiago");
        var doc = JsonDocument.Parse(json);
        var cur = doc.RootElement.GetProperty("current");
        var temp = cur.GetProperty("temperature_2m").GetDouble();
        var hum = cur.GetProperty("relative_humidity_2m").GetInt32();
        var viento = cur.GetProperty("wind_speed_10m").GetDouble();
        return $"Clima Santiago ahora:\nTemperatura: {temp}°C\nHumedad: {hum}%\nViento: {viento} km/h";
    }
    catch { return "No pude traer el clima ahora."; }
}

async Task<string> GetNasa()
{
    try
    {
        var json = await http.GetStringAsync("https://api.nasa.gov/planetary/apod?api_key=DEMO_KEY");
        var doc = JsonDocument.Parse(json);
        var titulo = doc.RootElement.GetProperty("title").GetString() ?? "";
        var expl = doc.RootElement.GetProperty("explanation").GetString() ?? "";
        var url = doc.RootElement.GetProperty("url").GetString() ?? "";
        var resumen = await PreguntarGroq($"Resume esto en 4 lineas simples en espanol: {titulo} - {expl}");
        return $"NASA - {titulo}\n{resumen}\n{url}";
    }
    catch { return "No pude traer la foto NASA ahora."; }
}

async Task<string> PreguntarGroq(string prompt)
{
    try
    {
        var payload = new { model = "llama-3.3-70b-versatile", messages = new[] { new { role = "user", content = prompt } }, temperature = 0.7, max_tokens = 800 };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        req.Headers.Add("Authorization", $"Bearer {groqKey}");
        req.Content = content;
        var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "No entendi, repiteme.";
    }
    catch (Exception ex) { return "Error Groq: " + ex.Message; }
}

async Task EnviarWhatsApp(string to, string texto)
{
    try
    {
        var payload = new { messaging_product = "whatsapp", to = to, type = "text", text = new { body = texto } };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/v21.0/{phoneId}/messages");
        req.Headers.Add("Authorization", $"Bearer {whatsappToken}");
        req.Content = content;
        var res = await http.SendAsync(req);
        Console.WriteLine(await res.Content.ReadAsStringAsync());
    }
    catch (Exception ex) { Console.WriteLine(ex.Message); }
}

app.Run();