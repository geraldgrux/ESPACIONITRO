using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
var groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";

app.MapGet("/", () => "EspacioNitro Telegram OK - NET10");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
_ = app.RunAsync($"http://+:{port}");

Console.WriteLine("Bot Telegram iniciado...");

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
        sb.AppendLine($"FECHA HOY: {DateTime.Now:dd/MM/yyyy HH:mm} America/Santiago");

        // 1. CLIMA - Plan A Open-Meteo, Plan B wttr.in
        bool climaOk = false;
        try
        {
            var climaJson = await client.GetStringAsync("https://api.open-meteo.com/v1/forecast?latitude=-33.45&longitude=-70.66&current=temperature_2m,precipitation,wind_speed_10m&timezone=America%2FSantiago");
            using var d = JsonDocument.Parse(climaJson);
            var cur = d.RootElement.GetProperty("current");
            var temp = cur.GetProperty("temperature_2m").GetDouble();
            var lluvia = cur.GetProperty("precipitation").GetDouble();
            var viento = cur.GetProperty("wind_speed_10m").GetDouble();
            sb.AppendLine($"CLIMA REAL SANTIAGO: {temp}°C, lluvia {lluvia} mm, viento {viento} km/h.");
            climaOk = true;
        }
        catch (Exception ex) { Console.WriteLine($"CLIMA A FALLO: {ex.Message}"); }

        if (!climaOk)
        {
            try
            {
                var w = await client.GetStringAsync("https://wttr.in/Santiago?format=j1");
                using var d = JsonDocument.Parse(w);
                var cur = d.RootElement.GetProperty("current_condition")[0];
                var temp = cur.GetProperty("temp_C").GetString();
                var desc = cur.GetProperty("weatherDesc")[0].GetProperty("value").GetString();
                sb.AppendLine($"CLIMA REAL SANTIAGO (respaldo): {temp}°C, {desc}.");
            }
            catch (Exception ex) { Console.WriteLine($"CLIMA B FALLO: {ex.Message}"); }
        }

        // 2. NOTICIAS - Solo últimas 24 horas
        try
        {
            var rss = await client.GetStringAsync("https://news.google.com/rss/search?q=Santiago%20Chile&hl=es-419&gl=CL&ceid=CL:es-419");
            var items = Regex.Matches(rss, @"<item>(.*?)</item>", RegexOptions.Singleline);
            sb.AppendLine("NOTICIAS REALES SOLO ULTIMAS 24 HORAS:");
            int count = 0;
            foreach (Match item in items)
            {
                var titleM = Regex.Match(item.Value, @"<title>(.*?)</title>");
                var dateM = Regex.Match(item.Value, @"<pubDate>(.*?)</pubDate>");
                if (!titleM.Success || !dateM.Success) continue;
                var titulo = Regex.Replace(titleM.Groups[1].Value, "<.*?>", "").Trim();
                if (titulo.Contains("Google News")) continue;
                // Parsear fecha
                if (DateTime.TryParse(dateM.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
                {
                    var horas = (DateTime.UtcNow - fecha.ToUniversalTime()).TotalHours;
                    Console.WriteLine($"NOTICIA: {titulo} - hace {horas:F1}h");
                    if (horas > 24) continue; // descartar vieja
                    sb.AppendLine($"- [{fecha:dd/MM HH:mm}] {titulo}");
                    count++;
                    if (count >= 6) break;
                }
            }
            if (count == 0) sb.AppendLine("- No hay noticias de las últimas 24h en esta fuente.");
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
                return "No pude traer los datos en vivo justo ahora (las fuentes están con límite), prueba de nuevo en 2 minutos.";
            }
            systemPrompt = $"Eres EspacioNitro, bot de Telegram en Chile. Responde corto, claro y en español chileno. REGLA OBLIGATORIA: Solo usa las NOTICIAS que dicen 'ULTIMAS 24 HORAS' y el CLIMA REAL de abajo. No uses noticias viejas. Si no hay noticias de hoy, dilo y solo da el clima. No inventes Metro ni tráfico. DATOS REALES:\n{datosReales}";
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
            temperature = 0.2,
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