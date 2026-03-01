using System.Text.Json;
using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities.Agents
{
    /// <summary>
    /// Данные о запуске агента
    /// </summary>
    public partial class AgentRun : IEntityKey<long>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public long Id { get; set; }
        /// <summary>
        /// Время начала
        /// </summary>
        public DateTime StartTime { get; set; }
        /// <summary>
        /// Время окончания
        /// </summary>
        public DateTime EndTime { get; set; }
        /// <summary>
        /// Стек агентов
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? AgentStack { get; set; }
        /// <summary>
        /// Чат
        /// </summary>
        public long? ChatId { get; set; }
        /// <summary>
        /// Пользователь
        /// </summary>
        public int? UserId { get; set; }
        /// <summary>
        /// Агент
        /// </summary>
        public int? AgentId { get; set; }
        /// <summary>
        /// Состояние агента
        /// </summary>
        public int? AgentStateId { get; set; }

        public Chat? Chat { get; set; }
        public User? User { get; set; }
        public Agent? Agent { get; set; }
        public AgentState? AgentState { get; set; }
    }
}
