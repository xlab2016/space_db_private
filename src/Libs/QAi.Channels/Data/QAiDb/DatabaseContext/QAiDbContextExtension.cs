using System.Reflection;
using Data.Repository;

namespace QAi.Data.QAiDb.DatabaseContext
{
    public static class QAiDbContextExtension
    {
        public static bool AllMigrationsApplied(this QAiDbContext context)
        {
            return context.AllMigrationsAppliedCore();
        }

        public static void EnsureSeeded(this QAiDbContext context)
        {
            context.EnsureSeededCore(_ =>
                {
                    var dbAssembly = Assembly.GetExecutingAssembly();
                    context.AddSeedFromJson(context.Roles, dbAssembly, "Role", _ => _.Id, null, null, "Data.QAiDb");
                    context.AddSeedFromJson(context.AgentStates, dbAssembly, "AgentState", _ => _.Id, null, null, "Data.QAiDb");
                    context.AddSeedFromJson(context.ChannelTypes, dbAssembly, "ChannelType", _ => _.Id, null, null, "Data.QAiDb");
                    context.SaveChanges();
                });
        }
    }
}
