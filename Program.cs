using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

class Program
{
    static string groqKey = Environment.GetEnvironmentVariable("GROQ_KEY") ?? "";
    static string telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
    static HttpClient http = new HttpClient();

    static async Task<string> PreguntarAI(string mensajeUsuario)
    {
        try
        {
            var url = "https://api.groq.com/openai/v1/chat/completions";

            var body = new
            {
                model = "openai/gpt-oss-20b",
                messages = new[]
                {
                    new { role = "system", content = "Eres ESPACIONITRO, un amigo calido, divertido y empatico para jovenes en Chile. Hablas como un amigo chileno buena onda, usas emojis, eres corto, no juzgas, escuchas." },
                    new { role = "user", content = mensajeUsuario }
                },
                temperature = 0.8,
                max_tokens = 300
            };

            var json = JsonSerializer.Serialize(body);
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", $"Bearer {groqKey}");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var res = await http.SendAsync(req);
            var resTexto = await res.Content.ReadAsStringAsync();
            Console.WriteLine("RESPUESTA GROQ: " + resTexto);

            using var doc = JsonDocument.Parse(resTexto);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "No te entendi, repiteme?";
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error Groq: " + ex.Message);
            return "Uyy, se me fue el internet cosmico un segundo, me lo repites? 😅";
        }
    }

    static async Task Main()
    {
        // Para que funcione en tu PC tambien, si no hay variables, usa las de aqui para probar
        if (string.IsNullOrEmpty(telegramToken))
        {
            Console.WriteLine("Falta TELEGRAM_TOKEN. Ponlo en variables de entorno o aqui para probar local.");
            return;
        }

        var bot = new TelegramBotClient(telegramToken);
        var me = await bot.GetMe();
        Console.WriteLine($"ESPACIONITRO encendido! Soy @{me.Username}");
        var cts = new CancellationTokenSource();

        // Mantiene vivo a Render
        _ = Task.Run(async () =>
        {
            try
            {
                var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
                var listener = new System.Net.HttpListener();
                listener.Prefixes.Add($"http://*:{port}/");
                listener.Start();
                Console.WriteLine($"Servidor web vivo en puerto {port}");
                while (true)
                {
                    var ctx = await listener.GetContextAsync();
                    var buf = Encoding.UTF8.GetBytes("ESPACIONITRO esta vivo 🚀");
                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                    ctx.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error servidor web: " + ex.Message);
            }
        });

        bot.StartReceiving(
            async (botClient, update, ct) =>
            {
                if (update.Message?.Text == null) return;
                var chatId = update.Message.Chat.Id;
                var texto = update.Message.Text;
                Console.WriteLine($"Usuario: {texto}");
                string respuesta = texto == "/start"
                    ? "Holaa! Soy ESPACIONITRO 🚀 Ahora tengo cerebro super rapido. Preguntame lo que quieras. Como estas hoy?"
                    : await PreguntarAI(texto);
                await botClient.SendMessage(chatId, respuesta, cancellationToken: ct);
            },
            async (botClient, ex, ct) => { Console.WriteLine(ex.Message); },
            new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message } },
            cancellationToken: cts.Token
        );

        Console.WriteLine("Pulsa ENTER para apagar...");
        Console.ReadLine();
        cts.Cancel();
    }
}