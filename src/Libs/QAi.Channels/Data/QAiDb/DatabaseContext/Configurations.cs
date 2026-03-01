using QAi.Data.QAiDb.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QAi.Data.QAiDb.Entities.Agents;

namespace QAi.Data.QAiDb.DatabaseContext
{
    public class RolesConfiguration : IEntityTypeConfiguration<Role>
    {
        public void Configure(EntityTypeBuilder<Role> builder)
        {
            builder.HasKey(x => x.Id);
        }
    }

    public class UsersConfiguration : IEntityTypeConfiguration<User>
    {
        public bool IsInMemoryDb { get; set; }

        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder.HasKey(x => x.Id);

            if (!IsInMemoryDb)
            {
                builder.Property(_ => _.RefreshToken).HasColumnType("jsonb");
            }
            else
            {
                builder.Ignore(_ => _.RefreshToken);
            }
        }
    }

    public class UserRolesConfiguration : IEntityTypeConfiguration<UserRole>
    {
        public void Configure(EntityTypeBuilder<UserRole> builder)
        {
            builder.HasKey(x => x.Id);
        }
    }

    public class TenantsConfiguration : IEntityTypeConfiguration<Tenant>
    {
        public bool IsInMemoryDb { get; set; }

        public void Configure(EntityTypeBuilder<Tenant> builder)
        {
            builder.HasKey(x => x.Id);

            if (!IsInMemoryDb)
            {
                builder.Property(_ => _.Logo).HasColumnType("jsonb");
            }
            else
            {
                builder.Ignore(_ => _.Logo);
            }
        }
    }

    public class AgentTypesConfiguration : IEntityTypeConfiguration<AgentType>
    {
        public bool IsInMemoryDb { get; set; }

        public void Configure(EntityTypeBuilder<AgentType> builder)
        {
            builder.HasKey(x => x.Id);

            if (!IsInMemoryDb)
            {
                builder.Property(_ => _.Functions).HasColumnType("jsonb");
            }
            else
            {
                builder.Ignore(_ => _.Functions);
            }
        }
    }

    public class AgentsConfiguration : IEntityTypeConfiguration<Agent>
    {
        public bool IsInMemoryDb { get; set; }

        public void Configure(EntityTypeBuilder<Agent> builder)
        {
            builder.HasKey(x => x.Id);

            if (!IsInMemoryDb)
            {
                builder.Property(_ => _.Functions).HasColumnType("jsonb");
                builder.Property(_ => _.Tools).HasColumnType("jsonb");
            }
            else
            {
                builder.Ignore(_ => _.Functions);
                builder.Ignore(_ => _.Tools);
            }
        }
    }

    public class AgentStatesConfiguration : IEntityTypeConfiguration<AgentState>
    {
        public void Configure(EntityTypeBuilder<AgentState> builder)
        {
            builder.HasKey(x => x.Id);
        }
    }

    public class ToolsConfiguration : IEntityTypeConfiguration<Tool>
    {
        public bool IsInMemoryDb { get; set; }

        public void Configure(EntityTypeBuilder<Tool> builder)
        {
            builder.HasKey(x => x.Id);

            if (!IsInMemoryDb)
            {
                builder.Property(_ => _.Args).HasColumnType("jsonb");
            }
            else
            {
                builder.Ignore(_ => _.Args);
            }
        }
    }

    public class AgentRunsConfiguration : IEntityTypeConfiguration<AgentRun>
    {
        public bool IsInMemoryDb { get; set; }

        public void Configure(EntityTypeBuilder<AgentRun> builder)
        {
            builder.HasKey(x => x.Id);

            if (!IsInMemoryDb)
            {
                builder.Property(_ => _.AgentStack).HasColumnType("jsonb");
            }
            else
            {
                builder.Ignore(_ => _.AgentStack);
            }
        }
    }

    public class ChannelsConfiguration : IEntityTypeConfiguration<Channel>
    {
        public void Configure(EntityTypeBuilder<Channel> builder)
        {
            builder.HasKey(x => x.Id);
        }
    }

    public class ChannelTypesConfiguration : IEntityTypeConfiguration<ChannelType>
    {
        public void Configure(EntityTypeBuilder<ChannelType> builder)
        {
            builder.HasKey(x => x.Id);
        }
    }

    public class ChatsConfiguration : IEntityTypeConfiguration<Chat>
    {
        public void Configure(EntityTypeBuilder<Chat> builder)
        {
            builder.HasKey(x => x.Id);
        }
    }

    public class ThreadsConfiguration : IEntityTypeConfiguration<Data.QAiDb.Entities.Agents.Thread>
    {
        public bool IsInMemoryDb { get; set; }

        public void Configure(EntityTypeBuilder<Data.QAiDb.Entities.Agents.Thread> builder)
        {
            builder.HasKey(x => x.Id);

            if (!IsInMemoryDb)
            {
                builder.Property(_ => _.FunctionArgs).HasColumnType("jsonb");
            }
            else
            {
                builder.Ignore(_ => _.FunctionArgs);
            }
        }
    }
}
