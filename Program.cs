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

            respuesta = respuesta.Replace("fontanería", "instalaciones sanitarias", StringComparison.OrdinalIgnoreCase);
            respuesta = respuesta.Replace("Fontanería", "Instalaciones sanitarias");
            respuesta = respuesta.Replace("**", "");
            respuesta = respuesta.Replace("__", "");
            respuesta = Regex.Replace(respuesta, @"^#{1,6}\s*", "", RegexOptions.Multiline);
            respuesta = Regex.Replace(respuesta, @"^---+\s*$", "", RegexOptions.Multiline);
            respuesta = Regex.Replace(respuesta, @"\n{3,}", "\n\n");
            respuesta = respuesta.Trim();

            var trozos = new List<string>();
            for (int i = 0; i < respuesta.Length; i += 3800)
            {
                trozos.Add(respuesta.Substring(i, Math.Min(3800, respuesta.Length - i)));
            }
            for (int t = 0; t < trozos.Count; t++)
            {
                var txtEnviar = trozos.Count > 1 ? $"[Parte {t + 1}/{trozos.Count}]\n{trozos[t]}" : trozos[t];
                var payload = new { chat_id = chatId, text = txtEnviar };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var send = await http.PostAsync($"https://api.telegram.org/bot{telegramToken}/sendMessage", content);
                Console.WriteLine($"RESPONDIDO A {chatId} parte {t + 1}/{trozos.Count} - Telegram: {send.StatusCode}");
                if (trozos.Count > 1) await Task.Delay(500);
            }
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
    {
        Console.WriteLine("DATOS: usando caché Santiago");
        return cacheDatos;
    }

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
    {
        Console.WriteLine("DATOS: usando caché Arquitectura");
        return cacheArq;
    }

    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EspacioNitroBot");
        var sb = new StringBuilder();
        sb.AppendLine("NOTICIAS ARQUITECTURA CHILE ULTIMAS 48H:");

        var queries = new[] { "concurso arquitectura Chile", "Colegio de Arquitectos Chile", "premio arquitectura Chile", "proyecto arquitectura Chile", "bienal arquitectura Chile" };
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
        bool esArqNoticias = lower.Contains("noticias") && (lower.Contains("arquitect") || lower.Contains("minvu") || lower.Contains("oguc") || lower.Contains("colegio") || lower.Contains("concurso")) || lower.Contains("noticias de arquitectura") || lower.Contains("que noticias hay de arquitectura");
        bool esNormativa = lower.Contains("oguc") || lower.Contains("lguc") || lower.Contains("permiso") || lower.Contains("anteproyecto") || lower.Contains("recepcion") || lower.Contains("recepción") || lower.Contains("dom") || lower.Contains("normativa") || lower.Contains("articulo") || lower.Contains("artículo") || lower.Contains("cip") || lower.Contains("prc");
        bool esTrabajo = lower.Contains("memoria") || lower.Contains("carta") || lower.Contains("expediente") || lower.Contains("presupuesto") || lower.Contains("especificacion") || lower.Contains("especificación") || lower.Contains("redacta") || lower.Contains("checklist") || lower.Contains("informe") || lower.Contains("redactame") || lower.Contains("redáctame");

        string systemPrompt;

        if (esArqNoticias)
        {
            var datosArq = await ObtenerNoticiasArquitectura();
            Console.WriteLine($"DATOS ARQ: {datosArq}");
            systemPrompt = $"Eres EspacioNitro, arquitecto chileno. Responde corto, en español chileno. Solo usa las noticias reales de abajo. No inventes concursos ni cambios de norma. FORMATO: texto plano para Telegram, PROHIBIDO usar # * _ y negritas. Usa MAYUSCULAS para títulos y guion - para listas. DATOS:\n{datosArq}";
        }
        else if (esNormativa)
        {
            systemPrompt = @"Eres EspacioNitro, arquitecto chileno experto OGUC, LGUC y DOM.
OGUC = Ordenanza General de Urbanismo y Construcciones DS 47 MINVU, norma NACIONAL. LGUC = Ley General de Urbanismo y Construcciones. DOM = Dirección de Obras Municipales. PRC = Plan Regulador Comunal. CIP = Certificado de Informaciones Previas, primer documento en DOM. EETT = Especificaciones Técnicas.
PROHIBIDO: POT, PDU, decir OGUC municipal, decir fontanería (usa instalaciones sanitarias).
FORMATO: texto plano para Telegram, PROHIBIDO usar # * _ y negritas. Usa MAYUSCULAS para títulos y guion - para listas. Sin líneas de ---.
Nunca inventes artículo, plazo o documento. Si no estás seguro, di 'verifica en OGUC / CIP / DOM'.
Permiso: 1. CIP en DOM, 2. Revisar PRC, 3. Proyecto arquitectura + estructura + instalaciones, 4. Expediente: solicitud, planos, memoria, EETT, cálculo, certificados, patente, 5. Ingreso DOM, 6. Aprobación y pago derechos.
Termina con: 'Verifica con CIP y DOM de tu comuna, esto es orientación, no reemplaza la norma'.";
        }
        else if (esTrabajo)
        {
            systemPrompt = @"Eres EspacioNitro, arquitecto chileno. Redacta memoria.
GLOSARIO: OGUC = Ordenanza General DS 47. LGUC = Ley General Urbanismo. DOM = Dirección Obras Municipales. PRC = Plan Regulador Comunal. CIP = Certificado Informaciones Previas. EETT = Especificaciones Técnicas.

FORMATO OBLIGATORIO PARA TELEGRAM:
PROHIBIDO TOTAL usar: # * _ y negritas con **. PROHIBIDO líneas de ---.
Usa solo texto plano: Títulos en MAYUSCULAS, listas con guion - , y línea en blanco entre puntos.
Ejemplo:
MEMORIA DE PROYECTO - CASA EN ÑUÑOA
1. DATOS DEL SITIO
- Terreno: [COMPLETAR: según CIP]
- Altura máxima permitida: [COMPLETAR: según CIP]

REGLA DURA DE NUMEROS:
El usuario solo te dio UN numero: 120 m2. Ese es el UNICO numero que puedes usar.
PROHIBIDO escribir cualquier otro numero. Si necesitas una medida, pon [COMPLETAR: según CIP / según proyecto].

REGLA DURA DE CONTENIDO - ESTA ES LA MAS IMPORTANTE:
1) PUNTOS 1, 2, 3: solo datos de sitio, ahí SÍ va [COMPLETAR: según CIP].
2) PUNTOS 5, 6, 7: PROHIBIDO que el punto sea solo [COMPLETAR]. Cada uno de esos 3 puntos DEBE tener mínimo 2 líneas que empiecen con 'Propuesta:'.
Si pones solo [COMPLETAR] en 5, 6 o 7, la respuesta no sirve.

PROPUESTAS OBLIGATORIAS, cópialas y adáptalas, sin números:
5. ESTRUCTURAL
- Propuesta: albañilería armada + losa hormigón, a validar con cálculo
- Propuesta: cimentación con zapatas aisladas, a validar según estudio geotécnico
6. INSTALACIONES
- Propuesta: instalaciones sanitarias con agua potable y alcantarillado, a validar con DOM
- Propuesta: instalaciones eléctricas con cuadro de distribución y luminarias LED, a validar
- Propuesta: climatización con ventilación cruzada + doble vidriado, a validar
- Propuesta: energía renovable con panel solar fotovoltaico, a validar
7. EFICIENCIA / HABITABILIDAD OGUC TÍTULO 4
- Propuesta: aislamiento térmico en muros y cubierta, a validar
- Propuesta: ventilación natural cruzada en recintos habitables, a validar
- Propuesta: iluminación natural con ventanas orientadas a patio, a validar

PROHIBIDO la palabra fontanería. Siempre di instalaciones sanitarias.
PROHIBIDO decir 'Total palabras' o 'Total viñetas' al final.
PROHIBIDO inventar número de artículo OGUC. Di solo 'OGUC Título 4'.
Memoria 1-8: 1 Datos, 2 CIP/PRC, 3 Emplazamiento, 4 Programa y superficies, 5 Estructural, 6 Instalaciones, 7 Eficiencia / habitabilidad, 8 Cumplimiento. Máximo 3 viñetas por punto, completa del 1 al 8.";
        }
        else if (esSantiago)
        {
            var datos = await ObtenerDatosSantiago();
            Console.WriteLine($"DATOS: {datos}");
            if (datos.Contains("Sin datos reales"))
            {
                return "No pude traer los datos en vivo justo ahora (las fuentes están con límite), prueba de nuevo en 2 minutos.";
            }
            systemPrompt = $"Eres EspacioNitro, bot Telegram Chile. Responde corto, español chileno. Solo usa DATOS REALES de abajo. No inventes Metro ni tráfico. FORMATO: texto plano, PROHIBIDO # * _ y negritas, usa MAYUSCULAS para títulos y guion - para listas. DATOS:\n{datos}";
        }
        else
        {
            systemPrompt = "Eres EspacioNitro, arquitecto chileno, bot Telegram. Responde corto, español chileno. OGUC nacional DS 47, LGUC Ley General Urbanismo, DOM Dirección Obras Municipales, EETT Especificaciones Técnicas. Nunca POT/PDU. FORMATO: texto plano, PROHIBIDO # * _ y negritas.";
        }

        using var groq = new HttpClient();
        groq.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);

        var intentos = new[]
        {
            new { Modelo = "openai/gpt-oss-20b", Reasoning = "low", Tokens = 3000 },
            new { Modelo = "openai/gpt-oss-20b", Reasoning = "low", Tokens = 2000 },
            new { Modelo = "llama-3.3-70b-versatile", Reasoning = "", Tokens = 2000 },
        };

        foreach (var it in intentos)
        {
            try
            {
                object payload;
                if (string.IsNullOrEmpty(it.Reasoning))
                {
                    payload = new
                    {
                        model = it.Modelo,
                        messages = new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = texto }
                        },
                        temperature = 0.1,
                        max_tokens = it.Tokens
                    };
                }
                else
                {
                    payload = new
                    {
                        model = it.Modelo,
                        messages = new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = texto }
                        },
                        temperature = 0.1,
                        max_completion_tokens = it.Tokens,
                        reasoning_effort = it.Reasoning
                    };
                }

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var res = await groq.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
                var json = await res.Content.ReadAsStringAsync();
                Console.WriteLine($"GROQ {it.Modelo} {it.Reasoning}: {res.StatusCode} len={json.Length}");

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                var msg = choices[0].GetProperty("message");
                string? resp = null;
                if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    resp = c.GetString();
                if (string.IsNullOrWhiteSpace(resp) && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                    resp = rc.GetString();

                if (!string.IsNullOrWhiteSpace(resp)) return resp;
                Console.WriteLine($"GROQ VACIO {it.Modelo}, probando siguiente...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GROQ FALLO {it.Modelo}: {ex.Message}");
            }
        }

        return "No pude armar la respuesta, prueba de nuevo en 1 minuto. Si sigue, prueba: 'Memoria casa 120 m2 Ñuñoa, resumen'.";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR GROQ: {ex.Message}");
        return "Estoy con problemas para conectar, prueba de nuevo.";
    }
}