using System.Text;
using System.Text.Json;

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

async Task<string> ResponderConGroq(string texto)
{
    try
    {
        using var groq = new HttpClient();
        groq.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", groqKey);

        var payload = new
        {
            model = "llama-3.1-8b-instant",
            messages = new[]
            {
                new { role = "system", content = "Eres EspacioNitro, bot de Telegram en Chile. Responde corto, claro y en español chileno. Si preguntan qué pasó hoy en Santiago, resume contingencia, tránsito, metro y clima." },
                new { role = "user", content = texto }
            },
            temperature = 0.7,
            max_tokens = 400
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