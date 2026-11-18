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

            // FIX CORTE TELEGRAM: partir en trozos de 3800 y mandar en orden, no truncar
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
            systemPrompt = $"Eres EspacioNitro, asistente de arquitectura en Chile. Eres arquitecto chileno con experiencia en OGUC, LGUC, MINVU y DOM. Responde corto, claro, en español chileno, con criterio profesional. REGLA: Solo usa las noticias reales de abajo. No inventes concursos ni cambios de norma. Si no hay noticia, dilo. DATOS:\n{datosArq}";
        }
        else if (esNormativa)
        {
            systemPrompt = @"Eres EspacioNitro, arquitecto chileno experto en OGUC, LGUC y trámites DOM en Chile.
GLOSARIO OBLIGATORIO, NO LO CAMBIES:
- OGUC = Ordenanza General de Urbanismo y Construcciones, Decreto Supremo N°47 del MINVU, norma NACIONAL.
- LGUC = Ley General de Urbanismo y Construcciones.
- DOM = Dirección de Obras Municipales.
- PRC = Plan Regulador Comunal, es lo que define cada comuna.
- CIP = Certificado de Informaciones Previas, es el primer documento que se pide en la DOM.
- EETT = Especificaciones Técnicas, nunca Estudio de Efectos.
- Permiso de Edificación se tramita en la DOM de la comuna, no en MINVU.
PROHIBIDO USAR: POT, PDU, Plan de Ordenamiento Territorial, o decir que OGUC es municipal. PROHIBIDO decir fontanería, usa instalaciones sanitarias / agua potable y alcantarillado.
REGLA: Nunca inventes número de artículo, plazo o documento. Si no estás seguro, di 'verifica en OGUC / CIP / DOM'.
Para Permiso de Edificación en Chile, la estructura siempre es: 1. Pedir CIP en DOM, 2. Revisar PRC (uso de suelo, altura, ocupación, antejardín), 3. Proyecto de arquitectura + estructura + instalaciones, 4. Expediente: solicitud, planos, memoria, EETT (Especificaciones Técnicas), cálculo, certificados, patente del arquitecto, 5. Ingreso DOM, observaciones, 6. Aprobación y pago de derechos.
Termina siempre con: 'Verifica con CIP y DOM de tu comuna, esto es orientación, no reemplaza la norma'.";
        }
        else if (esTrabajo)
        {
            systemPrompt = @"Eres EspacioNitro, arquitecto chileno. Ayudas a redactar memoria, carta DOM, expediente, checklist, EETT.
GLOSARIO OBLIGATORIO:
- OGUC = Ordenanza General de Urbanismo y Construcciones DS 47.
- LGUC = Ley General de Urbanismo y Construcciones.
- DOM = Dirección de Obras Municipales.
- PRC = Plan Regulador Comunal.
- CIP = Certificado de Informaciones Previas.
- EETT = Especificaciones Técnicas.
PROHIBIDO: decir que LGUC es Ley de Garantía, que DOM son ordenanzas, que NCh 4 es estructural, o usar POT/PDU. PROHIBIDO decir fontanería, usa instalaciones sanitarias.
REGLA DE ORO: Distingue 2 cosas.
1. DATO DE SITIO / CIP / PRC: altura, ocupación, densidad, antejardín, metros terreno, dirección, topografía. Eso NUNCA lo inventes. Pon [COMPLETAR: según CIP].
2. CRITERIO DE DISEÑO: estructura, instalaciones, habitabilidad. Eso SÍ proponlo como PROPUESTA A VALIDAR, no lo dejes en [COMPLETAR]. Ejemplo: 'Propuesta: estructura en albañilería armada + losa hormigón, a validar con cálculo' / 'Propuesta: agua caliente con calefón + panel solar, a validar' / 'Propuesta: ventilación cruzada + doble vidriado, a validar'.
PROHIBIDO dejar los puntos 5, 6 y 7 solo con [COMPLETAR]. Siempre da 2-3 viñetas de propuesta concreta.
Para memoria: usa estructura chilena real: 1. Datos proyecto y propietario, 2. Antecedentes CIP/PRC, 3. Emplazamiento, 4. Programa arquitectónico y superficies (útil, construida), 5. Criterio estructural, 6. Criterio instalaciones, 7. Criterio eficiencia / habitabilidad OGUC Título 4, 8. Cumplimiento normativo.
REGLA DE FORMATO CLAVE: Máximo 750 palabras en total. Cada punto máximo 3 viñetas cortas. Nunca uses tablas gigantes. Usa listas cortas con guiones. La memoria debe entregarse COMPLETA, del punto 1 al 8, sin cortar en el punto 6. Sé conciso para que quepa completa.
Para checklist de permiso: usa lista real DOM: Solicitud, CIP, planos arquitectura 1:50, planos estructura, memoria de cálculo, EETT, certificado de factibilidad agua/luz, patente profesional, certificado de dominio vigente.
Si falta un dato clave de sitio, pon [COMPLETAR: ...] y sigue. No inventes superficies, ni materiales, ni cálculos como si fueran definitivos, preséntalos como propuesta a validar.";
        }
        else if (esSantiago)
        {
            var datos = await ObtenerDatosSantiago();
            Console.WriteLine($"DATOS: {datos}");
            if (datos.Contains("Sin datos reales"))
            {
                return "No pude traer los datos en vivo justo ahora (las fuentes están con límite), prueba de nuevo en 2 minutos.";
            }
            systemPrompt = $"Eres EspacioNitro, bot de Telegram en Chile. Responde corto, claro y en español chileno. REGLA: Solo usa DATOS REALES de abajo. No inventes Metro ni tráfico. Si un dato no está, di 'no hay info confirmada'. DATOS:\n{datos}";
        }
        else
        {
            systemPrompt = "Eres EspacioNitro, asistente de arquitectura en Chile y bot de Telegram. Responde corto, claro y en español chileno. Eres amable, profesional, con criterio de arquitecto chileno. Nunca uses POT/PDU, OGUC es nacional DS 47, LGUC es Ley General de Urbanismo y Construcciones, DOM es Dirección de Obras Municipales, EETT son Especificaciones Técnicas.";
        }

        using var groq = new HttpClient();
        groq.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);

        for (int intento = 0; intento < 2; intento++)
        {
            var promptAUsar = intento == 0 ? systemPrompt : systemPrompt + "\nResponde en máximo 700 palabras, con listas cortas, completa del 1 al 8 sin cortar.";
            var tokensAUsar = intento == 0 ? 2500 : 1200;

            var payload = new
            {
                model = "openai/gpt-oss-20b",
                messages = new[]
                {
                    new { role = "system", content = promptAUsar },
                    new { role = "user", content = texto }
                },
                temperature = 0.2,
                max_tokens = tokensAUsar
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var res = await groq.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
            var json = await res.Content.ReadAsStringAsync();
            Console.WriteLine($"GROQ intento {intento}: {res.StatusCode} len={json.Length}");

            try
            {
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
                    return resp;
                }

                Console.WriteLine($"GROQ VACIO intento {intento}, reintentando...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PARSE GROQ FALLO: {ex.Message} json={json.Substring(0, Math.Min(500, json.Length))}");
            }
        }

        return "No pude armar la respuesta, prueba de nuevo con una versión más corta, por ejemplo: 'Memoria casa 120 m2 Ñuñoa, resumen'.";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR GROQ: {ex.Message}");
        return "Estoy con problemas para conectar, prueba de nuevo.";
    }
}