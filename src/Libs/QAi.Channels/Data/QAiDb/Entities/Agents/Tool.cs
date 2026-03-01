using System.Text.Json;
using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities.Agents
{
    /// <summary>
    /// Инструмент
    /// </summary>
    public partial class Tool : IEntityKey<int>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Название инструмента
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Аргументы инструмента (в формате JSON)
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? Args { get; set; }
    }
}
