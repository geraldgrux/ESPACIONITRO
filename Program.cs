using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net;

namespace EspacioNitro;

public class Program
{
    private static string telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
    private static string groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";

    private static Dictionary<long, List<(string rol, string texto)>> historiaPorChat = new();
    private static Dictionary<long, string> memoriaLugar = new();
    private static HttpClient http = new HttpClient();
    private static long offset = 0;

    private const string FUENTES_DEPORTE = "Cooperativa.cl, BioBioChile.cl, TNTSports.cl, ANFP.cl, ESPN Chile.";
    private const string FUENTES_CLIMA = "Open-Meteo geocoding + forecast y Direccion Meteorologica de Chile.";
    private const string FUENTES_NORMA = "MINVU, OGUC DS 47 oficial, LGUC, Diario Oficial, DOM de la comuna, CIP.";
    private const string FUENTES_ARQ = "Colegio de Arquitectos de Chile, MINVU, concursos y premios oficiales.";
    private const string FUENTES_SANTIAGO = "Cooperativa, BioBioChile, TNT Sports, Open-Meteo, MINVU / DOM.";

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        app.MapGet("/", () => "EspacioNitro Telegram OK");
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        _ = app.RunAsync($"http://+:{port}");
        Console.WriteLine("Bot Telegram iniciado...");

        while (true)
        {
            try
            {
                var updatesJson = await http.GetStringAsync($"https://api.telegram.org/bot{telegramToken}/getUpdates?offset={offset}&timeout=30");
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
                    respuesta = respuesta.Replace("**", "");
                    respuesta = respuesta.Replace("__", "");
                    respuesta = Regex.Replace(respuesta, @"^#{1,6}\s*", "", RegexOptions.Multiline);
                    respuesta = Regex.Replace(respuesta, @"^---+\s*$", "", RegexOptions.Multiline);
                    respuesta = Regex.Replace(respuesta, @"\n{3,}", "\n\n");
                    respuesta = respuesta.Trim();
                    var trozos = new List<string>();
                    for (int i = 0; i < respuesta.Length; i += 3800) trozos.Add(respuesta.Substring(i, Math.Min(3800, respuesta.Length - i)));
                    for (int t = 0; t < trozos.Count; t++)
                    {
                        var txtEnviar = trozos.Count > 1 ? $"[Parte {t + 1}/{trozos.Count}]\n{trozos[t]}" : trozos[t];
                        var payload = new { chat_id = chatId, text = txtEnviar };
                        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                        var send = await http.PostAsync($"https://api.telegram.org/bot{telegramToken}/sendMessage", content);
                        Console.WriteLine($"RESPONDIDO A {chatId} parte {t + 1}/{trozos.Count} - {send.StatusCode}");
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
    }

    private static string LimpiarTexto(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = WebUtility.HtmlDecode(s);
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, "<.*?>", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static string ExtraerCiudad(string texto)
    {
        var t = texto.ToLower().Trim();
        t = t.Replace("¿", "").Replace("?", "").Trim();
        if (t.StartsWith("y el") || t.StartsWith("y la") || t.StartsWith("y ")) return "";
        int idxEn = t.LastIndexOf(" en ");
        int idxDe = t.LastIndexOf(" de ");
        int idx = Math.Max(idxEn, idxDe);
        if (idx >= 0)
        {
            var ciudad = t.Substring(idx + 4).Trim();
            if (ciudad.StartsWith("el ")) ciudad = ciudad.Substring(3).Trim();
            if (ciudad.StartsWith("la ")) ciudad = ciudad.Substring(3).Trim();
            ciudad = ciudad.Replace(" hoy", "").Replace(" mañana", "").Replace(" ahora", "").Trim();
            if (ciudad.Length > 0 && ciudad.Length < 40)
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(ciudad);
        }
        if (t.Length > 2 && t.Length < 30 && !t.Contains(" "))
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(t);
        return "";
    }

    private static async Task<string> ObtenerClimaCiudad(string ciudad)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EspacioNitroBot");
            string[] intentosCiudad = new[] { ciudad, ciudad.Split(' ').Last() };
            double lat = 0, lon = 0;
            string nombreReal = ciudad, admin = "";
            bool encontrado = false;
            foreach (var intento in intentosCiudad.Distinct())
            {
                try
                {
                    var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(intento)}&count=5&language=es&format=json";
                    var geoJson = await client.GetStringAsync(geoUrl);
                    using var geoDoc = JsonDocument.Parse(geoJson);
                    if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) continue;
                    JsonElement elegido = results[0];
                    foreach (var r in results.EnumerateArray())
                    {
                        if (r.TryGetProperty("country", out var co) && co.GetString() == "Chile") { elegido = r; break; }
                        if (r.TryGetProperty("country_code", out var cc) && cc.GetString() == "CL") { elegido = r; break; }
                    }
                    lat = elegido.GetProperty("latitude").GetDouble();
                    lon = elegido.GetProperty("longitude").GetDouble();
                    nombreReal = elegido.TryGetProperty("name", out var n) ? n.GetString() ?? ciudad : ciudad;
                    admin = elegido.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "";
                    encontrado = true;
                    break;
                }
                catch { continue; }
            }
            if (!encontrado && ciudad.ToLower().Contains("lautaro")) { lat = -38.53; lon = -72.44; nombreReal = "Lautaro"; admin = "Araucanía"; encontrado = true; }
            if (!encontrado) return "";
            var sb = new StringBuilder();
            sb.AppendLine($"FUENTES CLIMA: {FUENTES_CLIMA}");
            sb.AppendLine($"CIUDAD: {ciudad} FECHA HOY REAL: {DateTime.Now:dd/MM/yyyy HH:mm} America/Santiago");
            sb.AppendLine($"LUGAR REAL: {nombreReal}, {admin} lat {lat} lon {lon}");
            sb.AppendLine($"HOY ES: {DateTime.Now:dd/MM/yyyy} - EL PRIMER PRONOSTICO DE LA LISTA ES HOY, EL SEGUNDO ES MANANA. NO LOS CONFUNDAS.");
            sb.AppendLine($"SI EL USUARIO PREGUNTA 'COMO VA A ESTAR', RESPONDE CON EL PRONOSTICO DE HOY. SI PREGUNTA 'MANANA', USA EL SEGUNDO.");
            var fcUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,precipitation,wind_speed_10m,wind_direction_10m,weather_code&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=America%2FSantiago&forecast_days=2";
            var fcJson = await client.GetStringAsync(fcUrl);
            using var d = JsonDocument.Parse(fcJson);
            var cur = d.RootElement.GetProperty("current");
            var daily = d.RootElement.GetProperty("daily");
            var fechas = daily.GetProperty("time");
            var maxs = daily.GetProperty("temperature_2m_max");
            var mins = daily.GetProperty("temperature_2m_min");
            var probs = daily.TryGetProperty("precipitation_probability_max", out var p) ? p : default;
            sb.AppendLine($"CLIMA ACTUAL {nombreReal}: {cur.GetProperty("temperature_2m").GetDouble()}°C, lluvia {cur.GetProperty("precipitation").GetDouble()} mm, viento {cur.GetProperty("wind_speed_10m").GetDouble()} km/h.");
            for (int i = 0; i < fechas.GetArrayLength(); i++)
            {
                var probTxt = probs.ValueKind == JsonValueKind.Array && probs.GetArrayLength() > i && probs[i].ValueKind != JsonValueKind.Null ? $"{probs[i].GetInt32()}%" : "sin dato";
                var etiqueta = i == 0 ? "HOY" : "MANANA";
                sb.AppendLine($"PRONOSTICO {etiqueta} {fechas[i].GetString()}: máxima {maxs[i].GetDouble()}°C, mínima {mins[i].GetDouble()}°C, prob. lluvia {probTxt}.");
            }
            return sb.ToString();
        }
        catch (Exception ex) { Console.WriteLine($"CLIMA FALLO: {ex.Message}"); return ""; }
    }

    private static async Task<string> ObtenerNoticias(string pregunta, string[] queries, int horas, int porQuery)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EspacioNitroBot");
            var sb = new StringBuilder();
            sb.AppendLine($"FECHA HOY REAL: {DateTime.Now:dd/MM/yyyy HH:mm} America/Santiago PREGUNTA: {pregunta}");
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

    private static async Task<(string deduccion, string lugar, bool necesitaDatoVivo, string tipoDato)> DeducirFlexible(string texto, long chatId)
    {
        try
        {
            using var groqD = new HttpClient();
            groqD.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);
            string historiaTxt = "";
            if (historiaPorChat.ContainsKey(chatId))
                historiaTxt = string.Join("\n", historiaPorChat[chatId].TakeLast(10).Select(h => $"{h.rol}: {h.texto}"));
            var prompt = $"Eres el deducidor de intención. Entiendes lenguaje chileno flexible: slang, faltas, preguntas cortas, 'esa cuestión', 'eso', 'y el máximo?'. HISTORIA:\n{historiaTxt}\nMENSAJE:\n{texto}\nDevuelve SOLO JSON: {{\"deduccion\":\"a qué se refiere, completando con historia\", \"lugar\":\"ciudad si hay\", \"necesitaDatoVivo\":true/false, \"tipoDato\":\"clima|deporte|noticias|santiago|ninguno\"}}";
            var payload = new { model = "openai/gpt-oss-20b", messages = new[] { new { role = "user", content = prompt } }, temperature = 0.0, max_completion_tokens = 300, reasoning_effort = "low" };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var res = await groqD.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var txt = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            var m = Regex.Match(txt, @"\{.*\}", RegexOptions.Singleline);
            if (m.Success)
            {
                using var j = JsonDocument.Parse(m.Value);
                var ded = j.RootElement.TryGetProperty("deduccion", out var a) ? a.GetString() ?? texto : texto;
                var lug = j.RootElement.TryGetProperty("lugar", out var b) ? b.GetString() ?? "" : "";
                var vivo = j.RootElement.TryGetProperty("necesitaDatoVivo", out var c) && c.GetBoolean();
                var tipo = j.RootElement.TryGetProperty("tipoDato", out var d) ? d.GetString() ?? "ninguno" : "ninguno";
                return (ded, lug, vivo, tipo.ToLower());
            }
        }
        catch (Exception ex) { Console.WriteLine($"DEDUCIR FALLO: {ex.Message}"); }
        return (texto, "", false, "ninguno");
    }

    private static async Task<string> ResponderConGroq(string texto, long chatId)
    {
        try
        {
            if (!historiaPorChat.ContainsKey(chatId)) historiaPorChat[chatId] = new List<(string, string)>();
            var historia = historiaPorChat[chatId];
            var (deduccion, lugar, necesitaDatoVivo, tipoDato) = await DeducirFlexible(texto, chatId);
            Console.WriteLine($"DEDUCCION: {deduccion} lugar={lugar} vivo={necesitaDatoVivo} tipo={tipoDato}");
            string datosVivos = "";
            string ciudadFinal = lugar;
            if (string.IsNullOrWhiteSpace(ciudadFinal) && memoriaLugar.ContainsKey(chatId)) ciudadFinal = memoriaLugar[chatId];
            if (!string.IsNullOrWhiteSpace(ciudadFinal)) memoriaLugar[chatId] = ciudadFinal;
            if (necesitaDatoVivo && tipoDato == "clima")
            {
                if (string.IsNullOrWhiteSpace(ciudadFinal)) ciudadFinal = "Santiago";
                datosVivos = await ObtenerClimaCiudad(ciudadFinal);
            }
            else if (necesitaDatoVivo && tipoDato == "deporte")
            {
                datosVivos = await ObtenerNoticias(deduccion, new[] { deduccion, "Liga de Primera Chile resultado" }, 24, 5);
            }
            else if (necesitaDatoVivo && (tipoDato == "noticias" || tipoDato == "santiago"))
            {
                datosVivos = await ObtenerNoticias(deduccion, new[] { deduccion, "Santiago Chile", "arquitectura Chile" }, 48, 3);
            }
            string reglas = $"REGLAS: 1) Responde SOLO lo preguntado. 2) Todo en español chileno, PROHIBIDO inglés. 3) Texto plano, PROHIBIDO # * _ negritas, títulos en MAYUSCULAS, listas con guion -. 4) PROHIBIDO inventar datos. 5) PROHIBIDO decir fontanería, di instalaciones sanitarias. 6) Elabora tú la respuesta. 7) FLEXIBILIDAD TOTAL: entiende slang, faltas, 'eso', 'esa cuestión', 'y el otro?'. Deduce con la HISTORIA. 8) CONTINUIDAD: usa la historia. 9) PROHIBIDO decir 'NO PUEDO RESPONDER SIN MAS CONTEXTO' si hay historia. 10) FECHA: hoy es {DateTime.Now:dd/MM/yyyy}. Usa esa fecha, no inventes otra. Si te preguntan hoy, usa el pronóstico etiquetado HOY. Si te preguntan mañana, usa el etiquetado MANANA. ";
            string systemPrompt = reglas + $"DEDUCCION: {deduccion} | LUGAR: {ciudadFinal} | FUENTES: Deporte: {FUENTES_DEPORTE} | Clima: {FUENTES_CLIMA} | Norma: {FUENTES_NORMA} | Arq: {FUENTES_ARQ} ";
            if (!string.IsNullOrWhiteSpace(datosVivos)) systemPrompt += $"\nDATOS VIVOS REALES:\n{datosVivos}";
            else systemPrompt += "\nNo hay datos vivos, razona tú. Si falta dato del sitio, marca [COMPLETAR: según CIP] o 'Propuesta: ... a validar'.";
            using var groq = new HttpClient();
            groq.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);
            var mensajes = new List<object>();
            mensajes.Add(new { role = "system", content = systemPrompt });
            foreach (var h in historia.TakeLast(10)) mensajes.Add(new { role = h.rol, content = h.texto });
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
                        payload = new { model = it.Modelo, messages = mensajes, temperature = 0.3, max_tokens = it.Tokens };
                    else
                        payload = new { model = it.Modelo, messages = mensajes, temperature = 0.3, max_completion_tokens = it.Tokens, reasoning_effort = it.Reasoning };
                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    var res = await groq.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
                    var json = await res.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                    var msg = choices[0].GetProperty("message");
                    string? resp = null;
                    if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) resp = c.GetString();
                    if (string.IsNullOrWhiteSpace(resp) && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String) resp = rc.GetString();
                    if (!string.IsNullOrWhiteSpace(resp))
                    {
                        historia.Add(("user", texto));
                        historia.Add(("assistant", resp));
                        if (historia.Count > 14) historia.RemoveRange(0, historia.Count - 14);
                        return resp;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"GROQ FALLO {it.Modelo}: {ex.Message}"); }
            }
            return "No pude armar la respuesta, prueba de nuevo en 1 minuto.";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR GROQ: {ex.Message}");
            return "Estoy con problemas para conectar, prueba de nuevo.";
        }
    }
}