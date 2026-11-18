using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
var groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";

app.MapGet("/", () => "EspacioNitro Telegram OK - NET10 - Arquitectura");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
_ = app.RunAsync($"http://+:{port}");

Console.WriteLine("Bot Telegram iniciado - Modo Arquitectura...");

string cacheDatos = "";
DateTime cacheHora = DateTime.MinValue;
string cacheArq = "";
DateTime cacheArqHora = DateTime.MinValue;

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

async Task<string> ObtenerDatosSantiago()
{
    if (!string.IsNullOrWhiteSpace(cacheDatos) && (DateTime.UtcNow - cacheHora).TotalMinutes < 15)
        return cacheDatos;

    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EspacioNitroBot");
        var sb = new StringBuilder();
        sb.AppendLine($"FECHA HOY: {DateTime.Now:dd/MM/yyyy HH:mm} America/Santiago");

        bool climaOk = false;
        try
        {
            var climaJson = await client.GetStringAsync("https://api.open-meteo.com/v1/forecast?latitude=-33.45&longitude=-70.66&current=temperature_2m,precipitation,wind_speed_10m&timezone=America%2FSantiago");
            using var d = JsonDocument.Parse(climaJson);
            var cur = d.RootElement.GetProperty("current");
            sb.AppendLine($"CLIMA REAL SANTIAGO: {cur.GetProperty("temperature_2m").GetDouble()}°C, lluvia {cur.GetProperty("precipitation").GetDouble()} mm, viento {cur.GetProperty("wind_speed_10m").GetDouble()} km/h.");
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
                sb.AppendLine($"CLIMA REAL SANTIAGO (respaldo): {cur.GetProperty("temp_C").GetString()}°C, {cur.GetProperty("weatherDesc")[0].GetProperty("value").GetString()}.");
            }
            catch (Exception ex) { Console.WriteLine($"CLIMA B FALLO: {ex.Message}"); }
        }

        try
        {
            var rss = await client.GetStringAsync("https://news.google.com/rss/search?q=Santiago%20Chile&hl=es-419&gl=CL&ceid=CL:es-419");
            var items = Regex.Matches(rss, @"<item>(.*?)</item>", RegexOptions.Singleline);
            sb.AppendLine("NOTICIAS SANTIAGO ULTIMAS 24H:");
            int count = 0;
            foreach (Match item in items)
            {
                var titleM = Regex.Match(item.Value, @"<title>(.*?)</title>");
                var dateM = Regex.Match(item.Value, @"<pubDate>(.*?)</pubDate>");
                if (!titleM.Success || !dateM.Success) continue;
                var titulo = Regex.Replace(titleM.Groups[1].Value, "<.*?>", "").Trim();
                if (titulo.Contains("Google News")) continue;
                if (DateTime.TryParse(dateM.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
                {
                    if ((DateTime.UtcNow - fecha.ToUniversalTime()).TotalHours > 24) continue;
                    sb.AppendLine($"- [{fecha:dd/MM HH:mm}] {titulo}");
                    count++;
                    if (count >= 6) break;
                }
            }
            if (count == 0) sb.AppendLine("- No hay noticias de las últimas 24h.");
        }
        catch (Exception ex) { Console.WriteLine($"NOTICIAS FALLO: {ex.Message}"); }

        var r = sb.ToString();
        if (!string.IsNullOrWhiteSpace(r)) { cacheDatos = r; cacheHora = DateTime.UtcNow; return r; }
        return cacheDatos != "" ? cacheDatos : "Sin datos reales.";
    }
    catch { return cacheDatos != "" ? cacheDatos : "Sin datos reales."; }
}

async Task<string> ObtenerNoticiasArquitectura()
{
    if (!string.IsNullOrWhiteSpace(cacheArq) && (DateTime.UtcNow - cacheArqHora).TotalMinutes < 60)
        return cacheArq;

    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EspacioNitroBot");
        var sb = new StringBuilder();
        sb.AppendLine("NOTICIAS ARQUITECTURA CHILE ULTIMAS 48H:");

        var queries = new[] { "arquitectura Chile", "MINVU Chile", "OGUC Chile", "Colegio de Arquitectos Chile", "concurso arquitectura Chile" };
        foreach (var q in queries)
        {
            try
            {
                var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(q)}&hl=es-419&gl=CL&ceid=CL:es-419";
                var rss = await client.GetStringAsync(url);
                var items = Regex.Matches(rss, @"<item>(.*?)</item>", RegexOptions.Singleline);
                int c = 0;
                foreach (Match item in items)
                {
                    var titleM = Regex.Match(item.Value, @"<title>(.*?)</title>");
                    var dateM = Regex.Match(item.Value, @"<pubDate>(.*?)</pubDate>");
                    if (!titleM.Success || !dateM.Success) continue;
                    var titulo = Regex.Replace(titleM.Groups[1].Value, "<.*?>", "").Trim();
                    if (titulo.Contains("Google News")) continue;
                    if (DateTime.TryParse(dateM.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
                    {
                        if ((DateTime.UtcNow - fecha.ToUniversalTime()).TotalHours > 48) continue;
                        sb.AppendLine($"- [{fecha:dd/MM HH:mm}] {titulo}");
                        c++;
                        if (c >= 2) break;
                    }
                }
            }
            catch { }
        }
        var r = sb.ToString();
        cacheArq = r; cacheArqHora = DateTime.UtcNow;
        return r;
    }
    catch { return cacheArq; }
}

async Task<string> ResponderConGroq(string texto)
{
    try
    {
        var lower = texto.ToLower();
        bool esSantiago = lower.Contains("santiago") || lower.Contains("paso hoy") || lower.Contains("clima") || lower.Contains("tiempo") || lower.Contains("metro") || lower.Contains("lluvia");
        bool esArqNoticias = lower.Contains("arquitectura") || lower.Contains("minvu") || lower.Contains("oguc") || lower.Contains("colegio de arquitectos") || lower.Contains("concurso") || lower.Contains("noticia") && lower.Contains("arquitect");
        bool esNormativa = lower.Contains("oguc") || lower.Contains("lguc") || lower.Contains("permiso") || lower.Contains("anteproyecto") || lower.Contains("recepcion") || lower.Contains("recepción") || lower.Contains("dom") || lower.Contains("normativa") || lower.Contains("articulo") || lower.Contains("artículo");
        bool esTrabajo = lower.Contains("memoria") || lower.Contains("carta") || lower.Contains("expediente") || lower.Contains("presupuesto") || lower.Contains("especificacion") || lower.Contains("especificación") || lower.Contains("redacta") || lower.Contains("checklist") || lower.Contains("informe");

        string systemPrompt;
        string datosExtra = "";

        if (esArqNoticias || lower.Contains("noticias arquitectura"))
        {
            datosExtra = await ObtenerNoticiasArquitectura();
            systemPrompt = $"Eres EspacioNitro, asistente de arquitectura en Chile. Eres arquitecto chileno con experiencia en OGUC, LGUC, MINVU y DOM. Responde corto, claro, en español chileno, con criterio profesional. REGLA: Solo usa las noticias reales de abajo para noticias. No inventes concursos ni cambios de norma. Si no hay noticia, dilo. DATOS:\n{datosExtra}";
        }
        else if (esNormativa)
        {
            systemPrompt = "Eres EspacioNitro, asistente de arquitectura en Chile. Eres arquitecto chileno experto en OGUC, LGUC y trámites DOM. REGLA OBLIGATORIA: Nunca inventes números de artículos, ni plazos, ni documentos. Si no estás 100% seguro del artículo exacto, dilo: 'No tengo la fuente confirmada, verifica en OGUC / consulta en la DOM'. Explica el criterio general, el procedimiento y qué revisar, no el artículo inventado. Termina siempre con: 'Verifica con la DOM / PRC de tu comuna, esto es orientación, no reemplaza la norma'. Responde estructurado y práctico.";
        }
        else if (esTrabajo)
        {
            systemPrompt = "Eres EspacioNitro, asistente de arquitectura en Chile. Ayudas a redactar documentos profesionales: memoria de proyecto, carta a DOM, expediente de permiso, checklist, especificaciones, presupuestos. Usa formato profesional chileno, claro, sin florituras. Si te piden redactar, entrega el documento listo para copiar, con [corchetes] para los datos que faltan. Si te piden checklist, entrega checklist con casillas. Pregunta solo lo esencial si falta un dato clave.";
        }
        else if (esSantiago)
        {
            datosExtra = await ObtenerDatosSantiago();
            systemPrompt = $"Eres EspacioNitro, bot de Telegram en Chile. Responde corto, claro y en español chileno. REGLA: Solo usa DATOS REALES de abajo. No inventes Metro ni tráfico. DATOS:\n{datosExtra}";
        }
        else
        {
            systemPrompt = "Eres EspacioNitro, asistente de arquitectura en Chile y bot de Telegram. Responde corto, claro y en español chileno. Eres amable, profesional, con criterio de arquitecto. Si el usuario habla de arquitectura, responde como arquitecto chileno.";
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
            max_tokens = 800
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await groq.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
        var json = await res.Content.ReadAsStringAsync();
        Console.WriteLine($"GROQ: {res.StatusCode}");

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices)) return "Estoy con problemas con Groq ahora, prueba de nuevo.";

        var resp = choices[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(resp)) return "No pude armar la respuesta, prueba de nuevo.";
        return resp;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR GROQ: {ex.Message}");
        return "Estoy con problemas para conectar, prueba de nuevo.";
    }
}