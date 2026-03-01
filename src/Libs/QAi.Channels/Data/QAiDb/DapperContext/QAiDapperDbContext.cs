using Data.Repository.Dapper;

namespace QAi.Data.QAiDb.DapperContext
{
    public class QAiDapperDbContext : DapperDbContext
    {
        public QAiDapperDbContext(IConfiguration configuration) : base(configuration)
        {
        }
    }
}
