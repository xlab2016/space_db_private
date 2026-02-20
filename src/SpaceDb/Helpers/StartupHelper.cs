using AI.Client;
using AI.Client.Configuration;
using Magic.Kernel;
using Magic.Kernel.Devices.SSC;
using Microsoft.Extensions.Options;
using SpaceDb.Mappings;
using SpaceDb.Services;
using SpaceDb.Services.Parsers;

namespace SpaceDb.Helpers
{
    static class StartupHelper
    {
        public static void AddMapping(this WebApplicationBuilder source)
        {
            var services = source.Services;

            services.AddScoped<DbMapContext>();

            services.AddScoped<UserMap>();
            services.AddScoped<RoleMap>();
            services.AddScoped<UserRoleMap>();
            services.AddScoped<TenantMap>();
            services.AddScoped<SingularityMap>();
            services.AddScoped<WorkflowLogMap>();
        }

        public static void AddServices(this WebApplicationBuilder source)
        {
            var services = source.Services;
            var configuration = source.Configuration;

            var connection = new QAiConnection
            {
                Host = configuration["Providers:QAi:Host"],
                UserName = configuration["Providers:QAi:Username"],
                Password = configuration["Providers:QAi:Password"],
                ApiToken = configuration["Providers:QAi:ApiToken"],
            };
            services.AddSingleton(connection);
            var filesConnection = new QAiFilesConnection
            {
                Host = configuration["Providers:QAiFiles:Host"]
            };
            services.AddSingleton(filesConnection);
            services.AddScoped<QAiClient>().AddHttpClient();

            // Embedding Provider
            services.AddScoped<IEmbeddingProvider, QAiEmbeddingProvider>();

            // RocksDB Service
            services.AddRocksDb(configuration);

            services.AddSingleton<MagicKernel>();

            services.AddSingleton<Magic.SSC.TextCompiler>();
            services.AddSingleton<ISSCompiler>(sp => sp.GetRequiredService<Magic.SSC.TextCompiler>());

            // Qdrant Services
            services.Configure<QdrantConfig>(configuration.GetSection("Providers:Qdrant"));
            services.AddScoped<IQdrantService>(provider =>
            {
                var config = provider.GetRequiredService<IOptions<QdrantConfig>>().Value;
                var logger = provider.GetRequiredService<ILogger<QdrantService>>();
                var embeddingProvider = provider.GetRequiredService<IEmbeddingProvider>();
                return new QdrantService(config, logger, embeddingProvider);
            });

            services.AddScoped<IQdrantPayloadIndexService>(provider =>
            {
                var qdrantService = provider.GetRequiredService<IQdrantService>();
                var config = provider.GetRequiredService<IOptions<QdrantConfig>>().Value;
                var logger = provider.GetRequiredService<ILogger<QdrantPayloadIndexService>>();
                return new QdrantPayloadIndexService(qdrantService, config, logger);
            });

            // SpaceDb Service
            services.AddScoped<ISpaceDbService, SpaceDbService>();

            // Workflow Log Service
            services.AddScoped<IWorkflowLogService, WorkflowLogService>();

            // Content Parsers
            services.AddSingleton<PayloadParserBase, TextPayloadParser>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<TextPayloadParser>>();
                return new TextPayloadParser(logger, minParagraphLength: 50, maxParagraphLength: 2000);
            });

            services.AddSingleton<PayloadParserBase, JsonPayloadParser>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<JsonPayloadParser>>();
                return new JsonPayloadParser(logger, maxDepth: 10, includeArrays: true);
            });

            services.AddSingleton<PayloadParserBase, OwlPayloadParser>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<OwlPayloadParser>>();
                return new OwlPayloadParser(logger, maxDepth: 10, includeAnnotations: true);
            });

            // Content Parser Service
            services.AddScoped<IContentParserService>(provider =>
            {
                var spaceDbService = provider.GetRequiredService<ISpaceDbService>();
                var embeddingProvider = provider.GetRequiredService<IEmbeddingProvider>();
                var logger = provider.GetRequiredService<ILogger<ContentParserService>>();
                var parsers = provider.GetServices<PayloadParserBase>();
                var config = provider.GetRequiredService<IOptions<QdrantConfig>>().Value;
                var workflowLogService = provider.GetRequiredService<IWorkflowLogService>();

                return new ContentParserService(
                    spaceDbService,
                    embeddingProvider,
                    logger,
                    parsers,
                    config.EmbeddingType,
                    workflowLogService);
            });
        }

        public static void AddProviders(this WebApplicationBuilder source)
        {
            var services = source.Services;

        }
    }
}
