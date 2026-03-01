using System.Text.Json;
using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities.Agents
{
    /// <summary>
    /// Тип агента
    /// </summary>
    public partial class AgentType : IEntityKey<int>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Название типа агента
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Описание типа агента
        /// </summary>
        public string? Description { get; set; }
        /// <summary>
        /// Системный промпт
        /// </summary>
        public string? SystemPrompt { get; set; }
        /// <summary>
        /// Функции (в формате JSON)
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? Functions { get; set; }
    }
}
