using Data.Repository;

namespace QAi.Data.QAiDb.Entities.Agents
{
    /// <summary>
    /// Состояние агента
    /// </summary>
    public partial class AgentState : IEntityKey<int>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Название состояния
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Код состояния
        /// </summary>
        public string? Code { get; set; }
    }
}
