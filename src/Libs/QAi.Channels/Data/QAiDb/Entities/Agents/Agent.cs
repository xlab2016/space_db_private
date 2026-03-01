using System.Text.Json;
using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities.Agents
{
    /// <summary>
    /// Агент
    /// </summary>
    public partial class Agent : IEntityKey<int>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Имя агента
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Системный промпт
        /// </summary>
        public string? SystemPrompt { get; set; }
        /// <summary>
        /// Функции агента (в формате JSON)
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? Functions { get; set; }
        /// <summary>
        /// Инструменты агента (в формате JSON)
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? Tools { get; set; }
        /// <summary>
        /// Базовый тип агента
        /// </summary>
        public int? BaseTypeId { get; set; }
        /// <summary>
        /// Владелец агента
        /// </summary>
        public int? OwnerId { get; set; }
        /// <summary>
        /// Организация владельца
        /// </summary>
        public int? TenantId { get; set; }

        public AgentType? BaseType { get; set; }
        public User? Owner { get; set; }
        public Tenant? Tenant { get; set; }
    }
}
