using Data.Repository;

namespace QAi.Data.QAiDb.Entities.Agents
{
    /// <summary>
    /// Тип канала
    /// </summary>
    public partial class ChannelType : IEntityKey<int>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Название типа канала
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Код типа канала
        /// </summary>
        public string? Code { get; set; }
    }
}
