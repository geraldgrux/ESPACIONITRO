using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

class Program
{
    static string telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
    static string groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";
    static HttpClient http = new HttpClient();

    static async Task Main()
    {
        if (string.IsNullOrWhiteSpace(telegramToken))
        {
            Console.WriteLine("Falta TELEGRAM_TOKEN. Ponlo en variables de entorno");
            return;
        }

        // TRUCO PARA RENDER GRATIS: abrir puerto para que Render no lo apague
        var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
        _ = Task.Run(async () =>
        {
            try
            {
                var listener = new System.Net.HttpListener();
                listener.Prefixes.Add($"http://*:{port}/");
                listener.Start();
                Console.WriteLine($"Puerto web abierto en {port}");
                while (true)
                {
                    var ctx = await listener.GetContextAsync();
                    var bytes = Encoding.UTF8.GetBytes("Bot ESPACIONITRO activo");
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error puerto: " + ex.Message);
            }
        });

        var bot = new TelegramBotClient(telegramToken);
        var me = await bot.GetMe();
        Console.WriteLine($"Bot conectado: @{me.Username}");

        var cts = new CancellationTokenSource();
        var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };

        bot.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            cts.Token
        );

        Console.WriteLine("Bot ESPACIONITRO prendido, esperando mensajes...");
        await Task.Delay(-1);
    }

    static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { } message) return;
        if (message.Text is not { } text) return;

        Console.WriteLine($"Mensaje de {message.Chat.Id}: {text}");

        try
        {
            string respuesta;

            if (text == "/start")
            {
                respuesta = "Hola! Soy ESPACIONITRO 🚀\nEscríbeme lo que quieras y te respondo con IA.";
            }
            else
            {
                respuesta = await PreguntarGroq(text);
            }

            await bot.SendMessage(message.Chat.Id, respuesta, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            await bot.SendMessage(message.Chat.Id, "Hubo un error, intenta de nuevo.", cancellationToken: ct);
        }
    }

    static async Task<string> PreguntarGroq(string pregunta)
{
    try
    {
        var body = new
        {
            model = "openai/gpt-oss-20b",
            messages = new[]
            {
                new { role = "system", content = "Eres ESPACIONITRO, un asistente amigable y divertido que responde en español chileno, corto y buena onda." },
                new { role = "user", content = pregunta }
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        req.Headers.Add("Authorization", $"Bearer {groqKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var res = await http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();
        Console.WriteLine("Respuesta Groq: " + json);

        using var doc = JsonDocument.Parse(json);

        // Si Groq devuelve error, mostrarlo
        if (doc.RootElement.TryGetProperty("error", out var errorProp))
        {
            return "Error de Groq: " + errorProp.GetProperty("message").GetString();
        }

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "No pude responder.";
    }
    catch (Exception ex)
    {
        return "Error con Groq: " + ex.Message;
    }
}

    static Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        Console.WriteLine("Error Telegram: " + ex.Message);
        return Task.CompletedTask;
    }
}