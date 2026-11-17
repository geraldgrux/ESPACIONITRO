using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text;
using System.Text.Json;

// --- Variables de Render ---
var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
var groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";

if (string.IsNullOrEmpty(telegramToken))
{
    Console.WriteLine("ERROR: Falta TELEGRAM_TOKEN en Render");
    return;
}

if (string.IsNullOrEmpty(groqKey))
{
    Console.WriteLine("ERROR: Falta GROQ_KEY en Render");
}

// --- Servidor web para que Render no se duerma ---
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "ESPACIONITRO esta vivo! 🚀");
_ = app.RunAsync($"http://*:{port}");

// --- Bot de Telegram ---
var bot = new TelegramBotClient(telegramToken);
var me = await bot.GetMe();
Console.WriteLine($"Bot conectado: @{me.Username}");

var cts = new CancellationTokenSource();

bot.StartReceiving(
    HandleUpdateAsync,
    HandleErrorAsync,
    new ReceiverOptions { AllowedUpdates = { } },
    cts.Token
);

Console.WriteLine("Bot esperando mensajes...");
await Task.Delay(-1);

// --- Cuando llega un mensaje ---
async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken token)
{
    try
    {
        if (update.Type != UpdateType.Message) return;
        if (update.Message?.Text is not { } mensaje) return;

        var chatId = update.Message.Chat.Id;
        Console.WriteLine($"Mensaje de {chatId}: {mensaje}");

        var respuesta = await PreguntarGroq(mensaje);

        await botClient.SendMessage(chatId, respuesta, cancellationToken: token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error en HandleUpdate: {ex.Message}");
    }
}

Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken token)
{
    Console.WriteLine($"Error Telegram: {exception.Message}");
    return Task.CompletedTask;
}

// --- Preguntar a Groq ---
async Task<string> PreguntarGroq(string preguntaUsuario)
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {groqKey}");

        var body = new
        {
            model = "openai/gpt-oss-20b",
            messages = new[]
            {
                new { role = "system", content = "Eres ESPACIONITRO, un asistente de buenas vibras en español chileno. Respondes divertido, con humor chileno, buena onda, usas palabras como po, cachai, órale, qué raja. Nunca usas ** ni negritas ni markdown, solo texto plano." },
                new { role = "user", content = preguntaUsuario }
            },
            temperature = 0.8,
            max_tokens = 800
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
        var textoRespuesta = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"Respuesta Groq: {textoRespuesta}");

        if (!response.IsSuccessStatusCode)
        {
            return $"Groq falló: {response.StatusCode} - {textoRespuesta}";
        }

        using var doc = JsonDocument.Parse(textoRespuesta);
        var respuesta = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "Sin respuesta";

        // Limpia los ** y markdown que a veces manda Groq
        respuesta = respuesta
            .Replace("**", "")
            .Replace("__", "")
            .Replace("### ", "")
            .Replace("## ", "")
            .Replace("# ", "")
            .Replace("`", "");

        return respuesta;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error Groq: {ex.Message}");
        return $"Ups, tuve un error: {ex.Message}";
    }
}