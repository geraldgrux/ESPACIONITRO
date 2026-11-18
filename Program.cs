using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Collections.Concurrent;

var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
var groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";

// --- MINI SERVIDOR KEEP-ALIVE PARA UPTIMEROBOT ---
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

// --- MEMORIA POR CHAT ---
var historial = new ConcurrentDictionary<long, List<object>>();

var bot = new TelegramBotClient(telegramToken);
var me = await bot.GetMe();
Console.WriteLine($"Bot conectado: @{me.Username}");

var cts = new CancellationTokenSource();
bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions { AllowedUpdates = { } }, cts.Token);
Console.WriteLine("Bot esperando mensajes...");
await Task.Delay(-1);

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken token)
{
    try
    {
        if (update.Type != UpdateType.Message) return;
        if (update.Message?.Text is not { } mensaje) return;
        var chatId = update.Message.Chat.Id;

        var lista = historial.GetOrAdd(chatId, _ => new List<object>());
        lista.Add(new { role = "user", content = mensaje });

        // Guardar solo ultimos 20 mensajes para que no se ponga lento
        if (lista.Count > 20) lista.RemoveRange(0, lista.Count - 20);

        var respuesta = await PreguntarGroq(lista);

        lista.Add(new { role = "assistant", content = respuesta });
        if (lista.Count > 20) lista.RemoveRange(0, lista.Count - 20);

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

async Task<string> PreguntarGroq(List<object> historialChat)
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {groqKey}");

        var messages = new List<object>
        {
            new { role = "system", content = "Eres ESPACIONITRO. Habla español neutro, claro, natural y con sentido. Prohibido usar modismos chilenos forzados como bacán, po, cachai, al tiro, weon o similares. No hables como caricatura. Habla como una persona normal, educada y amable. Sigue la conversación, recuerda lo que el usuario te dijo antes y no inventes temas. Si te dicen hola, saluda normal y pregunta en qué puedes ayudar. Para poemas y textos serios, habla bonito y poético. Respuestas cortas y directas, sin vueltas. Nunca uses ** ni negritas ni markdown, solo texto plano." }
        };
        messages.AddRange(historialChat);

        var body = new
        {
            model = "openai/gpt-oss-20b",
            messages = messages,
            temperature = 0.6,
            max_tokens = 800
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
        var textoRespuesta = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode) return $"Groq fallo: {response.StatusCode}";

        using var doc = JsonDocument.Parse(textoRespuesta);
        var respuesta = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "Sin respuesta";
        respuesta = respuesta.Replace("**", "").Replace("__", "").Replace("### ", "").Replace("## ", "").Replace("# ", "").Replace("`", "");
        return respuesta;
    }
    catch (Exception ex)
    {
        return $"Ups, tuve un error: {ex.Message}";
    }
}