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

// CONTINUIDAD REAL: historia por chat, no solo último lugar
// Guarda: usuario dice -> bot responde -> usuario dice -> bot responde...
var historiaPorChat = new Dictionary<long, List<(string rol, string texto)>>();
var memoriaLugar = new Dictionary<long, string>();

using var http = new HttpClient();
var offset = 0L;

const string FUENTES_DEPORTE = "Cooperativa.cl, BioBioChile.cl, TNTSports.cl, ANFP.cl, ESPN Chile. Si el dato no está ahí, no lo inventes.";
const string FUENTES_CLIMA = "Open-Meteo geocoding + forecast y Dirección Meteorológica de Chile (meteochile.gob.cl). PROHIBIDO inventar temperatura si no viene del pronóstico.";
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

            var respuesta = await ResponderConGroq(texto, chatId);
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

async Task<string> ObtenerClimaCiudad(string ciudad)
{
    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EspacioNitroBot");
        var sb = new StringBuilder();
        sb.AppendLine($"FUENTES CLIMA: {FUENTES_CLIMA}");
        sb.AppendLine($"CIUDAD PEDIDA: {ciudad}");
        sb.AppendLine($"FECHA HOY: {DateTime.Now:dd/MM/yyyy HH:mm} America/Santiago");

        var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(ciudad)}&count=1&language=es&format=json";
        var geoJson = await client.GetStringAsync(geoUrl);
        using var geoDoc = JsonDocument.Parse(geoJson);
        if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return $"No encontré la comuna {ciudad} en el geocodificador de Open-Meteo.";
        }
        var r0 = results[0];
        var lat = r0.GetProperty("latitude").GetDouble();
        var lon = r0.GetProperty("longitude").GetDouble();
        var nombreReal = r0.TryGetProperty("name", out var n) ? n.GetString() : ciudad;
        var admin = r0.TryGetProperty("admin1", out var a) ? a.GetString() : "";
        sb.AppendLine($"LUGAR REAL ENCONTRADO: {nombreReal}, {admin} lat {lat} lon {lon}");

        var fcUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,precipitation,wind_speed_10m,wind_direction_10m,weather_code&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=America%2FSantiago&forecast_days=2";
        var fcJson = await client.GetStringAsync(fcUrl);
        using var d = JsonDocument.Parse(fcJson);
        var cur = d.RootElement.GetProperty("current");
        var daily = d.RootElement.GetProperty("daily");
        var fechas = daily.GetProperty("time");
        var maxs = daily.GetProperty("temperature_2m_max");
        var mins = daily.GetProperty("temperature_2m_min");
        var probs = daily.TryGetProperty("precipitation_probability_max", out var p) ? p : default;

        sb.AppendLine($"CLIMA ACTUAL {nombreReal}: {cur.GetProperty("temperature_2m").GetDouble()}°C, lluvia {cur.GetProperty("precipitation").GetDouble()} mm, viento {cur.GetProperty("wind_speed_10m").GetDouble()} km/h dirección {cur.GetProperty("wind_direction_10m").GetDouble()}°, código clima {cur.GetProperty("weather_code").GetInt32()}.");
        for (int i = 0; i < fechas.GetArrayLength(); i++)
        {
            var probTxt = probs.ValueKind == JsonValueKind.Array && probs.GetArrayLength() > i && probs[i].ValueKind != JsonValueKind.Null ? $"{probs[i].GetInt32()}%" : "sin dato";
            sb.AppendLine($"PRONOSTICO {fechas[i].GetString()}: máxima {maxs[i].GetDouble()}°C, mínima {mins[i].GetDouble()}°C, prob. lluvia máx {probTxt}.");
        }
        sb.AppendLine("INSTRUCCION: usa SOLO estos números. Si te piden máxima, usa la máxima de hoy de arriba. No inventes otra.");
        return sb.ToString();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"CLIMA CIUDAD FALLO: {ex.Message}");
        return "";
    }
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
        sb.AppendLine("TITULAR + DESCRIPCION LIMPIA:");
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

string ExtraerCiudad(string texto)
{
    var t = texto.ToLower();
    t = t.Replace("como va a estar el clima en ", "").Replace("como va a estar el clima el ", "").Replace("como va a estar el clima en el ", "")
         .Replace("como esta el clima en ", "").Replace("como está el clima en ", "").Replace("clima en ", "").Replace("clima el ", "")
         .Replace("que clima va a hacer en ", "").Replace("qué clima va a hacer en ", "").Replace("el clima en ", "")
         .Replace("como va a estar el clima ", "").Replace("?", "").Replace("¿", "").Trim();
    if (t.Length > 0 && t.Length < 40) return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(t);
    return "";
}

async Task<string> ResponderConGroq(string texto, long chatId)
{
    try
    {
        // 1. Recuperar historia de este chat
        if (!historiaPorChat.ContainsKey(chatId)) historiaPorChat[chatId] = new List<(string, string)>();
        var historia = historiaPorChat[chatId];

        // 2. Armar texto con contexto para detectar tema, si es seguimiento usa la historia
        string textoParaDetectar = texto;
        if (historia.Count > 0)
        {
            // Junta los últimos 4 mensajes para entender "y el máximo?", "y en la tarde?", etc.
            var ultimos = string.Join(" | ", historia.TakeLast(4).Select(h => $"{h.rol}: {h.texto}"));
            textoParaDetectar = texto + " || HISTORIA: " + ultimos;
        }

        var lower = textoParaDetectar.ToLower();
        bool esClima = lower.Contains("clima") || lower.Contains("tiempo") || lower.Contains("lluvia") || lower.Contains("temperatura") || lower.Contains("maximo") || lower.Contains("máximo") || lower.Contains("minimo") || lower.Contains("mínimo") || lower.Contains("va a llover") || lower.Contains("como va a estar");
        bool esSantiagoNoticias = lower.Contains("paso hoy") || lower.Contains("noticias de santiago") || lower.Contains("que paso en santiago");
        bool esArqNoticias = lower.Contains("noticias") && (lower.Contains("arquitect") || lower.Contains("minvu") || lower.Contains("oguc") || lower.Contains("colegio") || lower.Contains("concurso")) || lower.Contains("noticias de arquitectura");
        bool esNormativa = lower.Contains("oguc") || lower.Contains("lguc") || lower.Contains("permiso") || lower.Contains("anteproyecto") || lower.Contains("recepcion") || lower.Contains("recepción") || lower.Contains("dom") || lower.Contains("normativa") || lower.Contains("articulo") || lower.Contains("artículo") || lower.Contains("cip") || lower.Contains("prc");
        bool esTrabajo = lower.Contains("memoria") || lower.Contains("carta") || lower.Contains("expediente") || lower.Contains("presupuesto") || lower.Contains("especificacion") || lower.Contains("especificación") || lower.Contains("redacta") || lower.Contains("checklist") || lower.Contains("informe") || lower.Contains("redactame") || lower.Contains("redáctame");
        bool esDiseno = lower.Contains("ventana") || lower.Contains("norte") || lower.Contains("sur") || lower.Contains("oriente") || lower.Contains("poniente") || lower.Contains("cuantas piezas") || lower.Contains("cuántas piezas") || lower.Contains("cuantos dormitorios") || lower.Contains("cuántos dormitorios") || lower.Contains("cuantas personas") || lower.Contains("cuántas personas") || lower.Contains("viven") || lower.Contains("que diseño") || lower.Contains("qué diseño") || lower.Contains("diseño de casa") || lower.Contains("diseno de casa") || lower.Contains("modular") || lower.Contains("patio") || lower.Contains("terraza") || lower.Contains("orientacion") || lower.Contains("orientación") || lower.Contains("distribucion") || lower.Contains("distribución") || lower.Contains("planta") || lower.Contains("segundo piso") || lower.Contains("ampliacion") || lower.Contains("ampliación");
        bool esDeporte = lower.Contains("catolica") || lower.Contains("católica") || lower.Contains("colo colo") || lower.Contains("colo-colo") || lower.Contains("la u") || lower.Contains("universidad de chile") || lower.Contains("futbol") || lower.Contains("fútbol") || lower.Contains("partido") || lower.Contains("gano") || lower.Contains("ganó") || lower.Contains("como le fue") || lower.Contains("resultado") || lower.Contains("marcador") || lower.Contains("gol") || lower.Contains("liga") || lower.Contains("campeonato");

        string reglas = "REGLAS GENERALES, NO SON RESPUESTAS: 1) Responde SOLO lo preguntado, no agregues temas que no pidió. 2) Todo en español chileno, PROHIBIDO palabras en inglés. 3) Texto plano para Telegram, PROHIBIDO # * _ y negritas, títulos en MAYUSCULAS, listas con guion -. 4) PROHIBIDO inventar datos, marcadores, artículos, plazos o medidas. Si no tienes el dato real, dilo. 5) PROHIBIDO decir fontanería, di instalaciones sanitarias. 6) Elabora tú la respuesta con criterio, no copies una plantilla. 7) CONTINUIDAD: esta conversación tiene historia, úsala. Si el usuario dice 'y el máximo?', 'y mañana?', 'y en la tarde?', se refiere al mismo lugar / tema de los mensajes anteriores, no pidas contexto de nuevo. 8) PROHIBIDO decir 'NO PUEDO RESPONDER SIN MAS CONTEXTO' si hay historia en la conversación, usa la historia. ";

        string systemPrompt;
        string ciudadDetectada = "";

        if (esClima)
        {
            ciudadDetectada = ExtraerCiudad(texto);
            if ((string.IsNullOrWhiteSpace(ciudadDetectada) || ciudadDetectada.ToLower().Contains("y el") || ciudadDetectada.ToLower().Contains("maximo")) && memoriaLugar.ContainsKey(chatId)) ciudadDetectada = memoriaLugar[chatId];
            if (string.IsNullOrWhiteSpace(ciudadDetectada))
            {
                // Busca en historia la última ciudad mencionada
                foreach (var h in historia.AsEnumerable().Reverse())
                {
                    var c = ExtraerCiudad(h.texto);
                    if (!string.IsNullOrWhiteSpace(c) && !c.ToLower().Contains("y el")) { ciudadDetectada = c; break; }
                }
            }
            if (string.IsNullOrWhiteSpace(ciudadDetectada)) ciudadDetectada = "Santiago";
            memoriaLugar[chatId] = ciudadDetectada;
            var datosClima = await ObtenerClimaCiudad(ciudadDetectada);
            if (string.IsNullOrWhiteSpace(datosClima))
            {
                return "No pude traer el pronóstico en vivo justo ahora.";
            }
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA CLIMA: {FUENTES_CLIMA} El usuario preguntó por {ciudadDetectada}. Responde SOLO lo preguntado, con los números reales de abajo. Si preguntó máxima, da solo la máxima. DATOS:\n" + datosClima;
        }
        else if (esDeporte)
        {
            var datosDep = await ObtenerNoticias(texto, new[] { texto, "Liga de Primera Chile resultado" }, 24, 5);
            if (string.IsNullOrWhiteSpace(datosDep) || datosDep.Split('\n').Length < 5)
            {
                return "No tengo el resultado confirmado en vivo ahora.";
            }
            systemPrompt = reglas + $"FUENTES VERIDICAS OBLIGATORIAS PARA DEPORTE: {FUENTES_DEPORTE} Lee titular + descripción de abajo, ahí viene el marcador y el goleador. Elabora tú la respuesta SOLO con eso. DATOS:\n" + datosDep;
        }
        else if (esArqNoticias)
        {
            var datosArq = await ObtenerNoticias(texto, new[] { "concurso arquitectura Chile", "Colegio de Arquitectos Chile", "premio arquitectura Chile", "proyecto arquitectura Chile" }, 48, 3);
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA ARQUITECTURA: {FUENTES_ARQ} Usa solo los titulares reales de abajo, elabora tú el resumen. TITULARES:\n" + datosArq;
        }
        else if (esDiseno)
        {
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA DISEÑO / NORMA: {FUENTES_NORMA} Eres EspacioNitro, arquitecto chileno experto en diseño de viviendas en Chile. Elabora tú la propuesta según el caso que te den, usando la historia si te dieron personas, m2, comuna antes. Criterio: en Chile, hemisferio sur, el norte recibe sol directo en invierno, el sur es más frío, el oriente sol de mañana, el poniente sol fuerte de tarde. Ventilación cruzada: abrir en 2 lados opuestos.";
        }
        else if (esNormativa)
        {
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA NORMA: {FUENTES_NORMA} Eres EspacioNitro, arquitecto chileno experto OGUC, LGUC y DOM. Elabora tú la explicación según lo preguntado. Nunca inventes número de artículo, plazo o documento.";
        }
        else if (esTrabajo)
        {
            systemPrompt = reglas + $"FUENTES VERIDICAS PARA DOCUMENTOS: {FUENTES_NORMA} Eres EspacioNitro, arquitecto chileno. Elabora tú el documento que te pidan, usando la historia si ya te dieron datos. Si es memoria, usa 8 puntos: 1 Datos, 2 CIP/PRC, 3 Emplazamiento, 4 Programa, 5 Estructural, 6 Instalaciones, 7 Eficiencia / habitabilidad, 8 Cumplimiento. Para datos del sitio que no tienes, usa [COMPLETAR: según CIP], no inventes.";
        }
        else if (esSantiagoNoticias)
        {
            var datos = await ObtenerNoticias(texto, new[] { "Santiago Chile" }, 24, 6);
            systemPrompt = reglas + $"FUENTES VERIDICAS: {FUENTES_SANTIAGO} Elabora tú la respuesta con los DATOS REALES de abajo. DATOS:\n{datos}";
        }
        else
        {
            systemPrompt = reglas + $"FUENTES VERIDICAS: {FUENTES_SANTIAGO} {FUENTES_NORMA} Eres EspacioNitro, arquitecto chileno, bot Telegram. Elabora tú la respuesta con criterio de arquitecto chileno, usando la historia de la conversación.";
        }

        using var groq = new HttpClient();
        groq.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);

        // Armar mensajes con historia: system + últimos 8 mensajes + pregunta actual
        var mensajes = new List<object>();
        mensajes.Add(new { role = "system", content = systemPrompt });
        foreach (var h in historia.TakeLast(8))
        {
            mensajes.Add(new { role = h.rol, content = h.texto });
        }
        mensajes.Add(new { role = "user", content = texto });

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
                    payload = new { model = it.Modelo, messages = mensajes, temperature = 0.3, max_tokens = it.Tokens };
                }
                else
                {
                    payload = new { model = it.Modelo, messages = mensajes, temperature = 0.3, max_completion_tokens = it.Tokens, reasoning_effort = it.Reasoning };
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

                if (!string.IsNullOrWhiteSpace(resp))
                {
                    // Guardar en historia para la próxima pregunta
                    historia.Add(("user", texto));
                    historia.Add(("assistant", resp));
                    // Guarda solo los últimos 12 para no explotar tokens
                    if (historia.Count > 12) historia.RemoveRange(0, historia.Count - 12);
                    return resp;
                }
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