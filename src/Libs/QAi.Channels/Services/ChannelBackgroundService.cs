using AI;
using Api.AspNetCore.Services;
using Data.Repository.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Magic.Drivers.Telegram.Providers;
using QAi.Data.QAiDb.DatabaseContext;
using QAi.Data.QAiDb.Entities.Agents;
using QAi.Data.QAiDb.Ids;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QAi.Channels.Services
{
    public class ChannelBackgroundService : BackgroundServiceBase<BackgroundServiceOptions>
    {
        private readonly ILogger<ChannelBackgroundService> logger;
        private readonly IConfiguration configuration;

        private readonly Dictionary<int, ChannelClient> channels = new Dictionary<int, ChannelClient>();

        private readonly string apiToken = "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";

        public ChannelBackgroundService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<ChannelBackgroundService> logger,
            IConfiguration configuration)
            : base(serviceScopeFactory, configuration, "ChannelListener", 20000) // Задержка 10 сек
        {
            this.logger = logger;
            this.configuration = configuration;
        }

        /// <summary>
        /// Привязка настроек из конфигурации
        /// </summary>
        protected override void BindOptions(string name, BackgroundServiceOptions options, IConfiguration configuration)
        {
            configuration.Bind(name, options);
        }

        /// <summary>
        /// Основная логика обработки задач
        /// </summary>
        protected override async Task ProcessTaskAsync(CancellationToken cancellationToken, IServiceScope scope)
        {
            var i = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    i++;
                    await StartChannels(scope, i == 1, cancellationToken);
                    //await UpdateChannels(scope, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Exception on main loop");
                }

                await Task.Delay(delay);
            }
        }

        /// <summary>
        /// Запуск каналов
        /// </summary>
        private async Task StartChannels(IServiceScope scope, bool start, CancellationToken cancellationToken)
        {
            var db = scope.ServiceProvider.GetRequiredService<QAiDbContext>();

            try
            {
                var last = DateTime.UtcNow.AddMinutes(start ? 0 : -3);

                var activeChannels = await db.Channels.AsNoTracking()
                    .Include(channel => channel.EntryPointAgent)
                    .Where(channel => channel.EntryPointAgentId != null && channel.Active &&
                                      (channel.StateId != (int)ChannelStateIds.Running || 
                                        channel.StateId == (int)ChannelStateIds.StoppedByError || 
                                        channel.PingTime <= last))
                    .ToListAsync(cancellationToken);

                foreach (var channel in activeChannels)
                {
                    _ = Task.Run(async () =>
                    {
                        await RunChannelAsync(channel, scope.ServiceProvider, cancellationToken);
                    }, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при запуске каналов");
            }
        }
        
        /// <summary>
        /// Метод для запуска обработки канала
        /// </summary>
        private async Task RunChannelAsync(Channel channel, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            using var scope = serviceProvider.CreateScope();
            using var scope2 = serviceProvider.CreateScope();
            var db = scope2.ServiceProvider.GetRequiredService<QAiDbContext>();

            try
            {
                var channel2 = await db.Channels.FirstAsync(c => c.Id == channel.Id, cancellationToken);
                channel2.StateId = (int)ChannelStateIds.Running;

                if (string.IsNullOrEmpty(channel.SecretToken))
                    channel2.SecretToken = Guid.NewGuid().ToString() + RandomStringHelper.GetRandomAlphanumericString(32);
                await db.SaveChangesAsync(cancellationToken);

                logger.LogWarning($"Запуск клиента для канала: {channel.Name}");

                var telegramProvider = scope.ServiceProvider.GetRequiredService<TelegramProvider>();
                telegramProvider.ChannelName = channel.Name;
                telegramProvider.SecretToken = channel.SecretToken;
                telegramProvider.Webhook = channel.Webhook;
                telegramProvider.Start(channel.Token);

                var channelClient = scope.ServiceProvider.GetRequiredService<ChannelClient>();
                channelClient.UpdatedTime = channel.UpdatedTime;

                if (channels.ContainsKey(channel.Id))
                {
                    var existingChannel = channels[channel.Id];
                    await existingChannel.Stop();
                    channels.Remove(channel.Id);
                }
                channels.Add(channel.Id, channelClient);

                channel2.StateId = (int)ChannelStateIds.Running;
                await db.SaveChangesAsync(cancellationToken);

                channelClient.ChannelId = channel.Id;
                // TODO: remove dependency here!
                channelClient.IsStream = channel.Stream;
                channelClient.ApiToken = apiToken;

                await channelClient.Start(telegramProvider, cancellationToken);
                await channelClient.Wait(cancellationToken, async () =>
                {
                    channel2.PingTime = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                });

                logger.LogWarning($"[Channel thread exited]: {channel.Name}");

                //channel2.StateId = (int)ChannelStateIds.StoppedByError;
                //channel2.LastError = JsonConvert.SerializeObject(new { Error = "Connection failed" });

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Ошибка в работе канала {channel.Name}");

                var channel2 = await db.Channels.FirstAsync(c => c.Id == channel.Id, cancellationToken);
                channel2.StateId = (int)ChannelStateIds.StoppedByError;
                channel2.LastError = JsonConvert.SerializeObject(new { Error = ex.Message, ex.StackTrace });
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
