using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
var groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";

app.MapGet("/", () => "EspacioNitro Telegram OK - NET10");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
_ = app.RunAsync($"http://+:{port}");

Console.WriteLine("Bot Telegram iniciado...");

// CACHE para no saturar las APIs - 15 minutos
string cacheDatos = "";
DateTime cacheHora = DateTime.MinValue;

using var http = new HttpClient();
var offset = 0L;

while (true)
{
    try
    {
        var updatesJson = await http.GetStringAsync(
            $"https://api.telegram.org/bot{telegramToken}/getUpdates?offset={offset}&timeout=30");

        using var doc = JsonDocument.Parse(updatesJson);
        var updates = doc.RootElement.GetProperty("result");

        foreach (var update in updates.EnumerateArray())
        {
            offset = update.GetProperty("update_id").GetInt64() + 1;

            if (!update.TryGetProperty("message", out var msg)) continue;
            if (!msg.TryGetProperty("text", out var textEl)) continue;

            var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64();
            var texto = textEl.GetString() ?? "";
            Console.WriteLine($"MENSAJE: {texto} de {chatId}");

            var respuesta = await ResponderConGroq(texto);
            if (string.IsNullOrWhiteSpace(respuesta)) respuesta = "Estoy con problemas para traer los datos en vivo ahora, prueba de nuevo en un momento.";

            var payload = new { chat_id = chatId, text = respuesta };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var send = await http.PostAsync($"https://api.telegram.org/bot{telegramToken}/sendMessage", content);
            Console.WriteLine($"RESPONDIDO A {chatId} - Telegram: {send.StatusCode}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
        await Task.Delay(3000);
    }
}

async Task<string> ObtenerDatosReales()
{
    // Si el caché tiene menos de 15 min, lo reusamos
    if (!string.IsNullOrWhiteSpace(cacheDatos) && (DateTime.UtcNow - cacheHora).TotalMinutes < 15)
    {
        Console.WriteLine("DATOS: usando caché");
        return cacheDatos;
    }

    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EspacioNitroBot");
        var sb = new StringBuilder();

        // 1. CLIMA REAL SANTIAGO - Open-Meteo
        try
        {
            var climaJson = await client.GetStringAsync("https://api.open-meteo.com/v1/forecast?latitude=-33.45&longitude=-70.66&current=temperature_2m,precipitation,wind_speed_10m&timezone=America%2FSantiago");
            using var d = JsonDocument.Parse(climaJson);
            var cur = d.RootElement.GetProperty("current");
            var temp = cur.GetProperty("temperature_2m").GetDouble();
            var lluvia = cur.GetProperty("precipitation").GetDouble();
            var viento = cur.GetProperty("wind_speed_10m").GetDouble();
            var hora = cur.GetProperty("time").GetString();
            sb.AppendLine($"CLIMA REAL SANTIAGO a las {hora}: {temp}°C, lluvia {lluvia} mm, viento {viento} km/h.");
        }
        catch (Exception ex) { Console.WriteLine($"CLIMA FALLO: {ex.Message}"); }

        // 2. NOTICIAS REALES - Google News RSS Chile (no falla con 404)
        try
        {
            var rss = await client.GetStringAsync("https://news.google.com/rss/search?q=Santiago%20Chile&hl=es-419&gl=CL&ceid=CL:es-419");
            var titulos = Regex.Matches(rss, @"<title>(.*?)</title>").Take(9).Select(m => Regex.Replace(m.Groups[1].Value, "<.*?>", "").Trim()).Where(t => t.Length > 10 && !t.Contains("Google News")).ToList();
            sb.AppendLine("NOTICIAS REALES DE HOY (Google News Chile):");
            foreach (var t in titulos) sb.AppendLine($"- {t}");
        }
        catch (Exception ex) { Console.WriteLine($"NOTICIAS FALLO: {ex.Message}"); }

        var resultado = sb.ToString();
        if (!string.IsNullOrWhiteSpace(resultado))
        {
            cacheDatos = resultado;
            cacheHora = DateTime.UtcNow;
            return resultado;
        }

        if (!string.IsNullOrWhiteSpace(cacheDatos)) return cacheDatos;
        return "Sin datos reales disponibles en este momento.";
    }
    catch
    {
        if (!string.IsNullOrWhiteSpace(cacheDatos)) return cacheDatos;
        return "Sin datos reales disponibles en este momento.";
    }
}

async Task<string> ResponderConGroq(string texto)
{
    try
    {
        var lower = texto.ToLower();
        bool preguntaDatos = lower.Contains("santiago") || lower.Contains("paso hoy") || lower.Contains("noticia") || lower.Contains("clima") || lower.Contains("tiempo") || lower.Contains("metro") || lower.Contains("trafico") || lower.Contains("tráfico") || lower.Contains("contingencia") || lower.Contains("lluvia");

        string systemPrompt;
        if (preguntaDatos)
        {
            var datosReales = await ObtenerDatosReales();
            Console.WriteLine($"DATOS: {datosReales}");
            if (datosReales.Contains("Sin datos reales"))
            {
                return "No pude traer los datos en vivo justo ahora (las fuentes están con límite), prueba de nuevo en 2 minutos. El bot sigue funcionando para conversar normal.";
            }
            systemPrompt = $"Eres EspacioNitro, bot de Telegram en Chile. Responde corto, claro y en español chileno. REGLA: Solo usa los DATOS REALES de abajo. No inventes Metro ni tráfico. Si un dato no está, di 'no hay info confirmada sobre eso'. DATOS REALES:\n{datosReales}";
        }
        else
        {
            systemPrompt = "Eres EspacioNitro, bot de Telegram en Chile. Responde corto, claro y en español chileno. Eres amable y conversacional.";
        }

        using var groq = new HttpClient();
        groq.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);

        var payload = new
        {
            model = "openai/gpt-oss-20b",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = texto }
            },
            temperature = 0.3,
            max_tokens = 500
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await groq.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
        var json = await res.Content.ReadAsStringAsync();
        Console.WriteLine($"GROQ: {res.StatusCode}");

        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("choices", out var choices))
        {
            Console.WriteLine($"GROQ RESPUESTA RARA: {json}");
            return "Estoy con problemas con Groq ahora, prueba de nuevo en un momento.";
        }

        var resp = choices[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(resp)) return "No pude armar la respuesta, prueba de nuevo con '¿Qué pasó hoy en Santiago?'";
        return resp;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR GROQ: {ex.Message}");
        return "Estoy con problemas para conectar, prueba de nuevo en un momento.";
    }
}