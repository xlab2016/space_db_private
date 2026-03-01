using AI;
using Magic.Drivers.Telegram.Providers;
using Magic.Drivers.Telegram.Services;
using QAi.Channels.Services;

namespace QAi.Helpers
{
    static class StartupHelper
    {
        public static void AddServices(this WebApplicationBuilder source)
        {
            var services = source.Services;

            var configuration = source.Configuration;

            services.AddTransient<TelegramProvider>();
            services.AddTransient<ChannelClient>().AddHttpClient();

            var agentConnection = new AgentConnection
            {
                Host = configuration["Providers:Api:Host"]
            };
            services.AddSingleton(agentConnection);

            services.AddSingleton<TelegramBotService>();

            services.AddHostedService<ChannelBackgroundService>();
        }

        public static void AddWorkflows(this WebApplicationBuilder source)
        {
            var services = source.Services;
        }
    }
}
