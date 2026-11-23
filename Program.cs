using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;

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

using var http = new HttpClient();
var offset = 0L;

// FUENTES VERIDICAS ESTABLECIDAS - NO SON RESPUESTAS, SON DE DONDE SACAR EL DATO
// El cerebro elabora, pero SOLO con lo que viene de estas fuentes.
const string FUENTES_DEPORTE = "Cooperativa.cl, BioBioChile.cl, TNTSports.cl, ANFP.cl, ESPN Chile. Si el dato no está ahí, no lo inventes.";
const string FUENTES_CLIMA = "Open-Meteo y Dirección Meteorológica de Chile (meteochile.gob.cl).";
const string FUENTES_NORMA = "MINVU, OGUC DS 47 oficial, LGUC, Diario Oficial, DOM de la comuna, CIP. No uses blogs ni foros.";
const string FUENTES_ARQ = "Colegio de Arquitectos de Chile, MINVU, concursos y premios oficiales, Diario Oficial.";
const string FUENTES_SANTIAGO = "Cooperativa, BioBioChile, TNT Sports para deporte, Open-Meteo para clima, MINVU / DOM para norma.";

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
            respuesta = respuesta.Replace("PROPOSAL", "Propuesta", StringComparison.OrdinalIgnoreCase);
            respuesta = respuesta.Replace("Proposal", "Propuesta");
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

string LimpiarTexto(string s)
{
    if (string.IsNullOrEmpty(s)) return "";
    s = WebUtility.HtmlDecode(s);
    s = WebUtility.HtmlDecode(s);
    s = Regex.Replace(s, "<.*?>", " ");
    s = Regex.Replace(s, @"\s+", " ").Trim();
    return s;
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
        sb.AppendLine($"FUENTES CLIMA VERIDICAS: {FUENTES_CLIMA}");

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
                var descM = Regex.Match(item.Value, @"<description>(.*?)</description>");
                if (!titleM.Success || !dateM.Success) continue;
                var titulo = LimpiarTexto(titleM.Groups[1].Value);
                var desc = descM.Success ? LimpiarTexto(descM.Groups[1].Value) : "";
                if (titulo.Contains("Google News")) continue;
                if (DateTime.TryParse(dateM.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
                {
                    if ((DateTime.UtcNow - fecha.ToUniversalTime()).TotalHours > 24) continue;
                    sb.AppendLine($"- [{fecha:dd/MM HH:mm}] {titulo} | {desc}");
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

async Task<string> ObtenerNoticias(string pregunta, string[] queries, int horas, int porQuery)
{
    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EspacioNitroBot");
        var sb = new StringBuilder();
        sb.AppendLine($"FECHA HOY: {DateTime.Now:dd/MM/yyyy HH:mm} America/Santiago");
        sb.AppendLine($"PREGUNTA: {pregunta}");
        sb.AppendLine("TITULAR + DESCRIPCION LIMPIA - AHI VIENE EL MARCADOR, EL GOLEADOR, EL DETALLE:");
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
                    var descM = Regex.Match(item.Value, @"<description>(.*?)</description>");
                    if (!titleM.Success || !dateM.Success) continue;
                    var titulo = LimpiarTexto(titleM.Groups[1].Value);
                    var desc = descM.Success ? LimpiarTexto(descM.Groups[1].Value) : "";
                    // Corta la descripción para que no venga basura, pero deja el dato
                    if (desc.Length > 300) desc = desc.Substring(0, 300);
                    if (titulo.Contains("Google News") || titulo.Length < 10) continue;
                    if (DateTime.TryParse(dateM.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
                    {
                        if ((DateTime.UtcNow - fecha.ToUniversalTime()).TotalHours > horas) continue;
                        sb.AppendLine($"- [{fecha:dd/MM HH:mm}] {titulo} | {desc}");
                        c++;
                        if (c >= porQuery) break;
                    }
                }
            }
            catch { }
        }
        return sb.ToString();
    }
    catch { return ""; }
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
        bool esDiseno = lower.Contains("ventana") || lower.Contains("norte") || lower.Contains("sur") || lower.Contains("oriente") || lower.Contains("poniente") || lower.Contains("cuantas piezas") || lower.Contains("cuántas piezas") || lower.Contains("cuantos dormitorios") || lower.Contains("cuántos dormitorios") || lower.Contains("cuantas personas") || lower.Contains("cuántas personas") || lower.Contains("viven") || lower.Contains("que diseño") || lower.Contains("qué diseño") || lower.Contains("diseño de casa") || lower.Contains("diseno de casa") || lower.Contains("modular") || lower.Contains("patio") || lower.Contains("terraza") || lower.Contains("orientacion") || lower.Contains("orientación") || lower.Contains("distribucion") || lower.Contains("distribución") || lower.Contains("planta") || lower.Contains("segundo piso") || lower.Contains("ampliacion") || lower.Contains("ampliación");
        bool esDeporte = lower.Contains("catolica") || lower.Contains("católica") || lower.Contains("colo colo") || lower.Contains("colo-colo") || lower.Contains("la u") || lower.Contains("universidad de chile") || lower.Contains("futbol") || lower.Contains("fútbol") || lower.Contains("partido") || lower.Contains("gano") || lower.Contains("ganó") || lower.Contains("como le fue") || lower.Contains("resultado") || lower.Contains("marcador") || lower.Contains("gol") || lower.Contains("liga") || lower.Contains("campeonato");

        string reglas = "REGLAS GENERALES, NO SON RESPUESTAS: 1) Responde SOLO lo preguntado, no agregues temas que no pidió. 2) Todo en español chileno, PROHIBIDO palabras en inglés. 3) Texto plano para Telegram, PROHIBIDO # * _ y negritas, títulos en MAYUSCULAS, listas con guion -. 4) PROHIBIDO inventar datos, marcadores, artículos, plazos o medidas. Si no tienes el dato real, dilo. 5) PROHIBIDO decir fontanería, di instalaciones sanitarias. 6) Elabora tú la respuesta con criterio, no copies una plantilla. ";

        string systemPrompt;

        if (esDeporte)
        {
            var datosDep = await ObtenerNoticias(texto, new[] { texto, "Colo Colo Universidad Católica resultado 3-0 OR goleada", "Liga de Primera Chile resultado" }, 24, 5);
            if (string.IsNullOrWhiteSpace(datosDep) || datosDep.Split('\n').Length < 5)
            {
                return "No tengo el resultado confirmado en vivo ahora.";
            }
            systemPrompt = reglas + $"FUENTES VERIDICAS OBLIGATORIAS PARA DEPORTE: {FUENTES_DEPORTE} Lee titular + descripción de abajo, ahí viene el marcador y el goleador. Elabora tú la respuesta SOLO con eso. Si el marcador está en la descripción aunque no esté en el titular, úsalo, es dato verídico. Si de verdad no está, di que no lo tienes. DATOS:\n" + datosDep;
        }
        else if (esArqNoticias)
        {
            var datosArq = await ObtenerNoticias(texto, new[] { "concurso arquitectura Chile", "Colegio de Arquitectos Chile", "premio arquitectura Chile", "proyecto arquitectura Chile" }, 48, 3);
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA ARQUITECTURA: {FUENTES_ARQ} Usa solo los titulares reales de abajo, elabora tú el resumen. No inventes concursos ni cambios de norma. TITULARES:\n" + datosArq;
        }
        else if (esDiseno)
        {
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA DISEÑO / NORMA: {FUENTES_NORMA} Eres EspacioNitro, arquitecto chileno experto en diseño de viviendas en Chile. Piensa como arquitecto, no como plantilla. Elabora tú la propuesta según el caso que te den: personas, m2 si te los dan, comuna, orientación del frente si te la dan. Criterio para razonar: en Chile, hemisferio sur, el norte recibe sol directo en invierno, el sur es más frío y con luz pareja, el oriente sol de mañana, el poniente sol fuerte de tarde en verano. Ventilación cruzada: abrir en 2 lados opuestos. Programa según personas y vida diaria, no con tabla fija. Si no te dan m2, terreno o CIP, propón con criterio y marca que es propuesta a validar con el terreno real, no inventes medidas del sitio. Si te piden 1 cosa, responde 1 cosa.";
        }
        else if (esNormativa)
        {
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA NORMA: {FUENTES_NORMA} Eres EspacioNitro, arquitecto chileno experto OGUC, LGUC y DOM. Sabes: OGUC es Ordenanza General DS 47 MINVU, norma NACIONAL. LGUC es Ley General de Urbanismo y Construcciones. DOM es Dirección de Obras Municipales. PRC es Plan Regulador Comunal. CIP es Certificado de Informaciones Previas, primer documento en DOM. EETT son Especificaciones Técnicas. Elabora tú la explicación según lo preguntado. Nunca inventes número de artículo, plazo o documento. Si no estás seguro, di que verifique en OGUC / CIP / DOM.";
        }
        else if (esTrabajo)
        {
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA DOCUMENTOS: {FUENTES_NORMA} Eres EspacioNitro, arquitecto chileno. Elabora tú el documento que te pidan, por ejemplo memoria, carta, checklist. Reglas de estructura, no de contenido: usa los 8 puntos si es memoria: 1 Datos, 2 CIP/PRC, 3 Emplazamiento, 4 Programa, 5 Estructural, 6 Instalaciones, 7 Eficiencia / habitabilidad, 8 Cumplimiento. Para datos del sitio que no tienes, usa [COMPLETAR: según CIP], no inventes calle, zona ni medidas. Para lo demás, elabora tú la propuesta con criterio y marca 'Propuesta: ... a validar', no afirmes como definitivo lo que no tienes. No inventes número de artículo OGUC.";
        }
        else if (esSantiago)
        {
            var datos = await ObtenerDatosSantiago();
            Console.WriteLine($"DATOS: {datos}");
            if (datos.Contains("Sin datos reales"))
            {
                return "No pude traer los datos en vivo justo ahora.";
            }
            systemPrompt = reglas + $"FUENTES VERIDICAS: {FUENTES_SANTIAGO} Eres EspacioNitro, bot Telegram Chile. Elabora tú la respuesta con los DATOS REALES de abajo. No inventes Metro ni tráfico. DATOS:\n{datos}";
        }
        else
        {
            systemPrompt = reglas + $"FUENTES VERIDICAS: {FUENTES_SANTIAGO} {FUENTES_NORMA} Eres EspacioNitro, arquitecto chileno, bot Telegram. Elabora tú la respuesta con criterio de arquitecto chileno.";
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
                        temperature = 0.3,
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
                        temperature = 0.3,
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

        return "No pude armar la respuesta, prueba de nuevo en 1 minuto.";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR GROQ: {ex.Message}");
        return "Estoy con problemas para conectar, prueba de nuevo.";
    }
}