using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities.Agents
{
    /// <summary>
    /// Канал для прослушивания
    /// </summary>
    public partial class Channel : IEntityKey<int>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Название канала
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Дата создания
        /// </summary>
        public DateTime CreatedTime { get; set; }
        /// <summary>
        /// Токен для подключения (например, Telegram Bot Token)
        /// </summary>
        public string? Token { get; set; }
        /// <summary>
        /// Активность канала
        /// </summary>
        public bool Active { get; set; }
        /// <summary>
        /// Потоковый
        /// </summary>
        public bool Stream { get; set; }
        /// <summary>
        /// Последняя ошибка в JSON формате
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? LastError { get; set; }
        /// <summary>
        /// Последнее время успешного пинга канала
        /// </summary>
        public DateTime PingTime { get; set; }
        /// <summary>
        /// Тип канала
        /// </summary>
        public int? TypeId { get; set; }
        /// <summary>
        /// Входной агент для обработки сообщений
        /// </summary>
        public int? EntryPointAgentId { get; set; }
        /// <summary>
        /// Текущее состояние канала
        /// </summary>
        public int? StateId { get; set; }
        /// <summary>
        /// Токен для webhook
        /// </summary>
        public string? SecretToken { get; set; }
        /// <summary>
        /// Webhook?
        /// </summary>
        public bool Webhook { get; set; }
        /// <summary>
        /// Последнее время обновления канала
        /// </summary>
        public DateTime UpdatedTime { get; set; }

        public ChannelType? Type { get; set; }
        public Agent? EntryPointAgent { get; set; }
    }
}
