using AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Magic.Drivers.Telegram.Services;

namespace QAi.Channels.Controllers
{
    [ApiController]
    [Route("api/telegram")]
    [Authorize]
    public class TelegramController : ControllerBase
    {
        private readonly ILogger<TelegramController> _logger;
        private readonly TelegramBotService _botService;

        public TelegramController(ILogger<TelegramController> logger, TelegramBotService botService)
        {
            _logger = logger;
            _botService = botService;
        }

        [HttpPost]
        [Route("/api/telegram/send")]
        public async Task<IActionResult> Send([FromBody] SendMessageRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.BotName))
                {
                    return BadRequest("BotName is required");
                }

                if (string.IsNullOrEmpty(request.ChatId))
                {
                    return BadRequest("ChatId is required");
                }

                if (string.IsNullOrEmpty(request.Text))
                {
                    return BadRequest("Text is required");
                }

                // Parse chat ID
                if (!long.TryParse(request.ChatId, out var chatId))
                {
                    return BadRequest("Invalid ChatId format");
                }

                // Send message using the service method
                var messageId = await _botService.SendMessage(
                    request.BotName,
                    chatId,
                    request.Text,
                    request.ParseMode,
                    CancellationToken.None
                );

                _logger.LogInformation("Message sent successfully. Bot: {BotName}, ChatId: {ChatId}, MessageId: {MessageId}", 
                    request.BotName, request.ChatId, messageId);

                return Ok(new SendMessageResponse
                {
                    Success = true,
                    MessageId = messageId,
                    BotName = request.BotName,
                    ChatId = request.ChatId
                });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("Bot not found: {BotName}", request.BotName);
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message. Bot: {BotName}, ChatId: {ChatId}", 
                    request.BotName, request.ChatId);
                
                return StatusCode(500, new SendMessageResponse
                {
                    Success = false,
                    Error = ex.Message,
                    BotName = request.BotName,
                    ChatId = request.ChatId
                });
            }
        }
    }

    public class SendMessageRequest
    {
        public string BotName { get; set; }
        public string ChatId { get; set; }
        public string Text { get; set; }
        public string? ParseMode { get; set; } // "MarkdownV2", "HTML", or null for default
    }

    public class SendMessageResponse
    {
        public bool Success { get; set; }
        public int? MessageId { get; set; }
        public string? Error { get; set; }
        public string BotName { get; set; }
        public string ChatId { get; set; }
    }
} 