// DEDUCCION FLEXIBLE PARA CUALQUIER TEMA - NO HAY CATEGORIAS FIJAS
// El cerebro deduce a qué se refiere, no el código por palabras exactas
async Task<(string deduccion, string lugar, bool necesitaDatoVivo, string tipoDato)> DeducirFlexible(string texto, long chatId)
{
    try
    {
        using var groq = new HttpClient();
        groq.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);

        string historiaTxt = "";
        if (historiaPorChat.ContainsKey(chatId))
            historiaTxt = string.Join("\n", historiaPorChat[chatId].TakeLast(10).Select(h => $"{h.rol}: {h.texto}"));

        var prompt = $@"
Eres el deducidor de intención. Entiendes lenguaje chileno flexible: slang, faltas, preguntas cortas, referencias como 'esa cuestión', 'eso', 'y el otro?', 'y si lo cambio?', 'y el máximo?', 'y en la tarde?', 'y si es al revés?'.

HISTORIA COMPLETA:
{historiaTxt}

MENSAJE ACTUAL:
{texto}

Tu tarea: deduce a qué se refiere el usuario.
- Si dice 'y el máximo?', 'y mañana?', 'esa ventana', 'eso que me dijiste', usa la HISTORIA para completar a qué se refiere.
- Si dice 'como va a estar el tiempo en lautaro', deduce: tema clima, lugar Lautaro, necesita dato vivo.
- Si dice 'y si la ventana la pongo al poniente?', deduce: tema diseño de la casa de la historia, no necesita dato vivo, es razonamiento.
- Si dice 'como le fue a la católica?', deduce: tema deporte, necesita dato vivo.
- Si dice 'y ese permiso cuánto se demora?', deduce: tema normativa del permiso de la historia.
- Si dice cualquier otra cosa, deduce libremente, no lo fuerces a una categoría.

Devuelve SOLO JSON: {{""deduccion"":""en 1 frase a qué se refiere realmente, completando con la historia"", ""lugar"":""ciudad o lugar si hay, si no vacio"", ""necesitaDatoVivo"":true/false, ""tipoDato"":""clima|deporte|noticias|santiago|ninguno""}}
Si necesitaDatoVivo es false, tipoDato es ninguno.
";

        var payload = new
        {
            model = "openai/gpt-oss-20b",
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.0,
            max_completion_tokens = 300,
            reasoning_effort = "low"
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await groq.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
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

    // Respaldo: si el deducidor falla, no clasifiques, deja que el cerebro responda con la historia
    return (texto, "", false, "ninguno");
}

async Task<string> ResponderConGroq(string texto, long chatId)
{
    try
    {
        if (!historiaPorChat.ContainsKey(chatId)) historiaPorChat[chatId] = new List<(string, string)>();
        var historia = historiaPorChat[chatId];

        // 1. DEDUCIR, NO CLASIFICAR POR PALABRAS
        var (deduccion, lugar, necesitaDatoVivo, tipoDato) = await DeducirFlexible(texto, chatId);
        Console.WriteLine($"DEDUCCION: {deduccion} lugar={lugar} vivo={necesitaDatoVivo} tipo={tipoDato}");

        // 2. Si necesita dato vivo, el código busca ese dato. Si no, no busca nada, el cerebro razona.
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

        string reglas = "REGLAS GENERALES, NO SON RESPUESTAS: 1) Responde SOLO lo preguntado, no agregues temas que no pidió. 2) Todo en español chileno, PROHIBIDO palabras en inglés. 3) Texto plano para Telegram, PROHIBIDO # * _ y negritas, títulos en MAYUSCULAS, listas con guion -. 4) PROHIBIDO inventar datos, marcadores, artículos, plazos o medidas. 5) PROHIBIDO decir fontanería, di instalaciones sanitarias. 6) Elabora tú la respuesta con criterio, no copies una plantilla. 7) FLEXIBILIDAD TOTAL DE LENGUAJE: entiende slang, faltas, preguntas cortas, referencias como 'eso', 'esa cuestión', 'y el otro?', 'y si lo cambio?'. Deduce a qué se refiere con la HISTORIA, no por la palabra exacta. No pidas que escriba mejor. 8) CONTINUIDAD: esta conversación tiene historia, úsala siempre. Si dice 'y el máximo?', 'y si lo pongo al revés?', 'y esa ventana?', es del tema anterior. 9) PROHIBIDO decir 'NO PUEDO RESPONDER SIN MAS CONTEXTO' si hay historia. 10) Si te dan un dato vivo abajo, úsalo. Si no hay dato vivo, razona tú con criterio de arquitecto chileno. ";

        string systemPrompt = reglas + $"DEDUCCION DEL CEREBRO SOBRE LO QUE REALMENTE PIDE EL USUARIO: {deduccion} | LUGAR: {ciudadFinal} | FUENTES VERIDICAS: Deporte: {FUENTES_DEPORTE} | Clima: {FUENTES_CLIMA} | Norma: {FUENTES_NORMA} | Arquitectura: {FUENTES_ARQ} ";
        if (!string.IsNullOrWhiteSpace(datosVivos))
            systemPrompt += $"\nDATOS VIVOS REALES PARA USAR, NO INVENTES OTROS:\n{datosVivos}";
        else
            systemPrompt += "\nNo hay datos vivos, razona tú con criterio, con la historia y con lo que el usuario te dio. Si te falta un dato del sitio, marca [COMPLETAR: según CIP] o 'Propuesta: ... a validar', no inventes.";

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