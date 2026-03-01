using Microsoft.AspNetCore.Mvc;
using Magic.Drivers.Telegram.Services;
using Telegram.Bot.Types;

[ApiController]
[Route("api/telegram/webhook")]
public class WebhookController : ControllerBase
{
    private readonly ILogger<WebhookController> _logger;
    private readonly TelegramBotService service;

    public WebhookController(ILogger<WebhookController> logger, TelegramBotService service)
    {
        _logger = logger;
        this.service = service;
    }

    [HttpPost]
    [Route("/api/telegram/webhook")]
    public async Task<IActionResult> Post([FromBody] Update update, [FromQuery] string name)
    {
        // 1. Проверяем, что заголовок `X-Telegram-Bot-Api-Secret-Token` присутствует
        if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var secretToken))
        {
            _logger.LogWarning("Missing Secret Token");
            return Unauthorized("Missing Secret Token");
        }

        if (string.IsNullOrEmpty(name))
        {
            _logger.LogWarning("Empty name");
            return Unauthorized("Empty name");
        }

        var botEntry = service[name];

        if (botEntry == null)
        {
            _logger.LogWarning("Bot not found: {Name}", name);
            return NotFound($"Bot not found: {name}");
        }

        if (botEntry.SecretToken != secretToken)
        {
            _logger.LogWarning("Invalid Secret Token: {Token}", secretToken);
            return Unauthorized("Invalid Secret Token");
        }

        _logger.LogWarning($"Webhook: {name}");
        await service.HandleUpdate(update, botEntry);

        return Ok();
    }

    //private async Task HandleUpdateAsync(Update update)
    //{
    //    if (update.Message is { } message)
    //    {
    //        long chatId = message.Chat.Id;
    //        string text = message.Text ?? "Нет текста";

    //        _logger.LogInformation("Received message from {ChatId}: {Text}", chatId, text);
    //        await _botClient.SendTextMessageAsync(chatId, "Ваше сообщение получено!");
    //    }
    //}
}
