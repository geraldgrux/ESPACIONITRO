using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Variables de Render - Environment
var verifyToken = Environment.GetEnvironmentVariable("VERIFY_TOKEN") ?? "nitro123";
var whatsappToken = Environment.GetEnvironmentVariable("WHATSAPP_TOKEN") ?? "";
var phoneNumberId = Environment.GetEnvironmentVariable("PHONE_NUMBER_ID") ?? "";

// Para que Render vea que está vivo
app.MapGet("/", () => "EspacioNitro OK - NET10");

// Verificación de Meta - cuando le das Verify en developers.facebook.com
app.MapGet("/webhook", (HttpRequest req) =>
{
    var mode = req.Query["hub.mode"];
    var token = req.Query["hub.verify_token"];
    var challenge = req.Query["hub.challenge"];

    if (mode == "subscribe" && token == verifyToken)
    {
        return Results.Text(challenge, "text/plain");
    }
    return Results.Forbid();
});

// Acá llegan los mensajes de WhatsApp
app.MapPost("/webhook", async (HttpRequest req) =>
{
    try
    {
        using var reader = new StreamReader(req.Body);
        var body = await reader.ReadToEndAsync();
        Console.WriteLine($"WEBHOOK RECIBIDO: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var messages = root
            .GetProperty("entry")[0]
            .GetProperty("changes")[0]
            .GetProperty("value")
            .TryGetProperty("messages", out var msgs) ? msgs : default;

        if (msgs.ValueKind == JsonValueKind.Undefined) return Results.Ok();

        foreach (var msg in msgs.EnumerateArray())
        {
            var from = msg.GetProperty("from").GetString() ?? "";
            var text = msg.TryGetProperty("text", out var t)
                ? t.GetProperty("body").GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(text)) continue;

            var respuesta = Responder(text);
            await EnviarWhatsApp(from, respuesta);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR WEBHOOK: {ex.Message}");
    }
    return Results.Ok();
});

string Responder(string texto)
{
    var t = texto.ToLower();

    if (t.Contains("santiago") || t.Contains("paso hoy") || t.Contains("pasó hoy") || t.Contains("contingencia") || t.Contains("actualidad"))
    {
        return "Hoy en Santiago: contingencia activa, revisa las stories de @espacionitro para el resumen del día. ¿Quieres que te avise de algo en particular? Tránsito, metro o clima?";
    }

    if (t.Contains("hola") || t.Contains("buenas") || t.Contains("hello"))
    {
        return "Hola! Soy EspacioNitro. Pregúntame: ¿Qué pasó hoy en Santiago? y te resumo el día.";
    }

    return $"Te escuché: \"{texto}\". Pregúntame por Santiago y te cuento lo de hoy.";
}

async Task EnviarWhatsApp(string to, string mensaje)
{
    if (string.IsNullOrEmpty(whatsappToken) || string.IsNullOrEmpty(phoneNumberId))
    {
        Console.WriteLine("FALTA WHATSAPP_TOKEN o PHONE_NUMBER_ID en Render");
        return;
    }

    using var client = new HttpClient();
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", whatsappToken);

    var payload = new
    {
        messaging_product = "whatsapp",
        to = to,
        type = "text",
        text = new { body = mensaje }
    };

    var json = JsonSerializer.Serialize(payload);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    var res = await client.PostAsync(
        $"https://graph.facebook.com/v21.0/{phoneNumberId}/messages",
        content);

    Console.WriteLine($"ENVIADO A {to} - Status: {res.StatusCode}");
}

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://+:{port}");