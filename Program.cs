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

            var payload = new { chat_id = chatId, text = respuesta };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await http.PostAsync($"https://api.telegram.org/bot{telegramToken}/sendMessage", content);
            Console.WriteLine($"RESPONDIDO A {chatId}");
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
    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var sb = new StringBuilder();

        // 1. CLIMA REAL SANTIAGO - Open-Meteo
        try
        {
            var climaJson = await client.GetStringAsync("https://api.open-meteo.com/v1/forecast?latitude=-33.45&longitude=-70.66&current=temperature_2m,relative_humidity_2m,precipitation,weather_code,wind_speed_10m&timezone=America%2FSantiago");
            using var d = JsonDocument.Parse(climaJson);
            var cur = d.RootElement.GetProperty("current");
            var temp = cur.GetProperty("temperature_2m").GetDouble();
            var lluvia = cur.GetProperty("precipitation").GetDouble();
            var viento = cur.GetProperty("wind_speed_10m").GetDouble();
            var hora = cur.GetProperty("time").GetString();
            sb.AppendLine($"CLIMA REAL SANTIAGO a las {hora}: {temp}°C, lluvia {lluvia} mm, viento {viento} km/h.");
        }
        catch (Exception ex) { Console.WriteLine($"CLIMA FALLO: {ex.Message}"); }

        // 2. NOTICIAS REALES - RSS BioBio
        try
        {
            var rss = await client.GetStringAsync("https://www.biobiochile.cl/feed/");
            var titulos = Regex.Matches(rss, @"<title>(.*?)</title>").Take(8).Select(m => Regex.Replace(m.Groups[1].Value, "<.*?>", "").Trim()).Where(t => t.Length > 10 && t != "BioBioChile").ToList();
            sb.AppendLine("NOTICIAS REALES DE HOY (BioBio):");
            foreach (var t in titulos) sb.AppendLine($"- {t}");
        }
        catch (Exception ex) { Console.WriteLine($"NOTICIAS FALLO: {ex.Message}"); }

        return sb.ToString();
    }
    catch
    {
        return "Sin datos reales disponibles en este momento.";
    }
}

async Task<string> ResponderConGroq(string texto)
{
    try
    {
        var datosReales = await ObtenerDatosReales();
        Console.WriteLine($"DATOS: {datosReales}");

        using var groq = new HttpClient();
        groq.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);

        var payload = new
        {
            model = "openai/gpt-oss-20b",
            messages = new[]
            {
                new { role = "system", content = $"Eres EspacioNitro, bot de Telegram en Chile. Responde corto, claro y en español chileno. REGLA OBLIGATORIA: Solo usa los DATOS REALES de abajo. No inventes contingencia, ni cortes de Metro, ni tráfico, ni clima. Si un dato no está en los datos reales, di 'no hay info confirmada'. DATOS REALES:\n{datosReales}" },
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

        return choices[0].GetProperty("message").GetProperty("content").GetString() ?? "No pude responder ahora, prueba de nuevo.";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR GROQ: {ex.Message}");
        return "Estoy con problemas para conectar, prueba de nuevo en un momento.";
    }
}