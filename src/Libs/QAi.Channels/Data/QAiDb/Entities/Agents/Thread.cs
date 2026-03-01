using System.Text.Json;
using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities.Agents
{
    /// <summary>
    /// Сообщения чата
    /// </summary>
    public partial class Thread : IEntityKey<long>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public long Id { get; set; }
        /// <summary>
        /// Роль отправителя сообщения
        /// </summary>
        public string? Role { get; set; }
        /// <summary>
        /// Наименование
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Имя чата
        /// </summary>
        public string? ChatName { get; set; }
        /// <summary>
        /// Имя пользователя
        /// </summary>
        public string? UserName { get; set; }
        /// <summary>
        /// Текст сообщения
        /// </summary>
        public string? Text { get; set; }
        /// <summary>
        /// Время отправки
        /// </summary>
        public DateTime Time { get; set; }
        /// <summary>
        /// Функция сообщения
        /// </summary>
        public string? Function { get; set; }
        /// <summary>
        /// Аргументы функции (в формате JSON)
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? FunctionArgs { get; set; }
        /// <summary>
        /// Чат
        /// </summary>
        public long? ChatId { get; set; }
        /// <summary>
        /// Инструмент, привязанный к функции
        /// </summary>
        public int? ToolId { get; set; }
    }
}
