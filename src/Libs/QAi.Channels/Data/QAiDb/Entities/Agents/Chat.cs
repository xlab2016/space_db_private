using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities.Agents
{
    public partial class Chat : IEntityKey<long>
    {
        /// <summary>
        /// Ид
        /// </summary>
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }
        /// <summary>
        /// Ид чата
        /// </summary>
        public string? ChatId { get; set; }
        /// <summary>
        /// Время создания чата
        /// </summary>
        public DateTime CreatedTime { get; set; }
        /// <summary>
        /// Имя пользователя
        /// </summary>
        public string? Username { get; set; }
        /// <summary>
        /// Начальный агент
        /// </summary>
        public int? StartAgentId { get; set; }
        /// <summary>
        /// Конечный агент
        /// </summary>
        public int? EndAgentId { get; set; }
        /// <summary>
        /// Пользователь
        /// </summary>
        public int? UserId { get; set; }
        /// <summary>
        /// Канал
        /// </summary>
        public int? ChannelId { get; set; }

        public Agent? StartAgent { get; set; }
        public Agent? EndAgent { get; set; }
        public User? User { get; set; }
        public Channel? Channel { get; set; }
    }
}
