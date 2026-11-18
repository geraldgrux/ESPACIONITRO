using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text;
using System.Text.Json;
using System.Net;

var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
var groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";

// --- MINI SERVIDOR PARA QUE UPTIMEROBOT LO MANTENGA DESPIERTO ---
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
var listener = new HttpListener();
listener.Prefixes.Add($"http://*:{port}/");
listener.Start();
Console.WriteLine($"Servidor keep-alive en puerto {port}");
_ = Task.Run(async () =>
{
    while (true)
    {
        var ctx = await listener.GetContextAsync();
        var bytes = Encoding.UTF8.GetBytes("ESPACIONITRO esta vivo");
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }
});
// --- FIN MINI SERVIDOR ---

if (string.IsNullOrEmpty(telegramToken))
{
    Console.WriteLine("ERROR: Falta TELEGRAM_TOKEN en Render");
    return;
}

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

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken token)
{
    try
    {
        if (update.Type != UpdateType.Message) return;
        if (update.Message?.Text is not { } mensaje) return;

        var chatId = update.Message.Chat.Id;
        var respuesta = await PreguntarGroq(mensaje);
        await botClient.SendMessage(chatId, respuesta, cancellationToken: token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken token)
{
    Console.WriteLine($"Error Telegram: {exception.Message}");
    return Task.CompletedTask;
}

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
                new { role = "system", content = "Eres ESPACIONITRO, un asistente buena onda que habla español chileno suave y natural, no como caricatura. Habla normal, claro y entretenido. Usa un modismo chileno solo de vez en cuando, maximo 1 por respuesta, y solo si calza. No termines frases con 'po.' solo. Para poemas y cosas serias, habla bonito y poetico sin modismos. Nunca uses ** ni negritas ni markdown, solo texto plano." },
                new { role = "user", content = preguntaUsuario }
            },
            temperature = 0.7,
            max_tokens = 800
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
        var textoRespuesta = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return $"Groq fallo: {response.StatusCode}";
        }

        using var doc = JsonDocument.Parse(textoRespuesta);
        var respuesta = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "Sin respuesta";

        respuesta = respuesta.Replace("**", "").Replace("__", "").Replace("### ", "").Replace("## ", "").Replace("# ", "").Replace("`", "");
        return respuesta;
    }
    catch (Exception ex)
    {
        return $"Ups, tuve un error: {ex.Message}";
    }
}