using Data.Repository;
using QAi.Data.QAiDb.Entities;
using Microsoft.EntityFrameworkCore;
using QAi.Data.QAiDb.Entities.Agents;

namespace QAi.Data.QAiDb.DatabaseContext
{
    public partial class QAiDbContext : DbContext
    {
        public DbSet<Role>? Roles { get; set; }
        public DbSet<User>? Users { get; set; }
        public DbSet<UserRole>? UserRoles { get; set; }
        public DbSet<Tenant>? Tenants { get; set; }
        public DbSet<AgentType>? AgentTypes { get; set; }
        public DbSet<Agent>? Agents { get; set; }
        public DbSet<AgentState>? AgentStates { get; set; }
        public DbSet<Tool>? Tools { get; set; }
        public DbSet<AgentRun>? AgentRuns { get; set; }
        public DbSet<Channel>? Channels { get; set; }
        public DbSet<ChannelType>? ChannelTypes { get; set; }
        public DbSet<Chat>? Chats { get; set; }
        public DbSet<Data.QAiDb.Entities.Agents.Thread>? Threads { get; set; }

        public QAiDbContext(DbContextOptions<QAiDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new RolesConfiguration());
            modelBuilder.ApplyConfiguration(new UsersConfiguration { IsInMemoryDb = this.IsInMemoryDb() });
            modelBuilder.ApplyConfiguration(new UserRolesConfiguration());
            modelBuilder.ApplyConfiguration(new TenantsConfiguration { IsInMemoryDb = this.IsInMemoryDb() });
            modelBuilder.ApplyConfiguration(new AgentTypesConfiguration { IsInMemoryDb = this.IsInMemoryDb() });
            modelBuilder.ApplyConfiguration(new AgentsConfiguration { IsInMemoryDb = this.IsInMemoryDb() });
            modelBuilder.ApplyConfiguration(new AgentStatesConfiguration());
            modelBuilder.ApplyConfiguration(new ToolsConfiguration { IsInMemoryDb = this.IsInMemoryDb() });
            modelBuilder.ApplyConfiguration(new AgentRunsConfiguration { IsInMemoryDb = this.IsInMemoryDb() });
            modelBuilder.ApplyConfiguration(new ChannelsConfiguration());
            modelBuilder.ApplyConfiguration(new ChannelTypesConfiguration());
            modelBuilder.ApplyConfiguration(new ChatsConfiguration());
            modelBuilder.ApplyConfiguration(new ThreadsConfiguration { IsInMemoryDb = this.IsInMemoryDb() });
        }
    }
}
