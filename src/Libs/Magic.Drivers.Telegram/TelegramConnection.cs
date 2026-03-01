using Telegram.Bot;
using Telegram.Bot.Types;

namespace Magic.Drivers.Telegram
{
    /// <summary>Single Telegram bot connection: send and long-polling receive in one instance.</summary>
    public sealed class TelegramConnection
    {
        private readonly TelegramBotClient _client;
        private readonly string _botToken;

        public TelegramConnection(string botToken)
        {
            _botToken = botToken ?? throw new ArgumentNullException(nameof(botToken));
            _client = new TelegramBotClient(_botToken);
        }

        /// <summary>Send text to chat. Uses the same client instance as receive.</summary>
        public async Task<int> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
        {
            var message = await _client.SendMessage(chatId, text, cancellationToken: cancellationToken);
            return message.MessageId;
        }

        /// <summary>Runs long-polling loop and invokes onMessage for each received text message. Exits when cancellation is requested.</summary>
        public async Task RunReceiveLoopAsync(Action<long, string, string?> onMessage, CancellationToken cancellationToken = default)
        {
            int? offset = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var updates = await _client.GetUpdates(offset, timeout: 60, cancellationToken: cancellationToken);
                    foreach (var update in updates)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        offset = update.Id + 1;
                        var msg = update.Message;
                        if (msg?.Text == null) continue;
                        var chatId = msg.Chat.Id;
                        var username = msg.Chat.Username;
                        try
                        {
                            onMessage(chatId, msg.Text, username);
                        }
                        catch
                        {
                            // consumer should not break the loop
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
    }
}
