using System.Text.Json;
using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities
{
    /// <summary>
    /// Организация
    /// </summary>
    public partial class Tenant : IEntityKey<int>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Наименование
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Полное наименование
        /// </summary>
        public string? FullName { get; set; }
        /// <summary>
        /// БИН
        /// </summary>
        public string? BIN { get; set; }
        /// <summary>
        /// Город
        /// </summary>
        public string? CityLine { get; set; }
        /// <summary>
        /// Район
        /// </summary>
        public string? RegionLine { get; set; }
        /// <summary>
        /// Адрес
        /// </summary>
        public string? AddressLine { get; set; }
        /// <summary>
        /// Юридический адрес
        /// </summary>
        public string? LegalAddressLine { get; set; }
        /// <summary>
        /// Телефон
        /// </summary>
        public string? PhoneNumber { get; set; }
        /// <summary>
        /// Email
        /// </summary>
        public string? Email { get; set; }
        /// <summary>
        /// Счет банка
        /// </summary>
        public string? BankAccountNumber { get; set; }
        /// <summary>
        /// Наименование банка
        /// </summary>
        public string? BankName { get; set; }
        /// <summary>
        /// БИК банка
        /// </summary>
        public string? BankBik { get; set; }
        /// <summary>
        /// Код бенефициара банка
        /// </summary>
        public string? BeneficiaryCode { get; set; }
        /// <summary>
        /// БИН банка
        /// </summary>
        public string? BankBin { get; set; }
        /// <summary>
        /// ФИО руководителя
        /// </summary>
        public string? SupervisorName { get; set; }
        /// <summary>
        /// Должность руководителя
        /// </summary>
        public string? SupervisorPosition { get; set; }
        /// <summary>
        /// Номер документа руководителя
        /// </summary>
        public string? SupervisorDocumentNumber { get; set; }
        /// <summary>
        /// ИИН руководителя
        /// </summary>
        public string? SupervisorIdentityNumber { get; set; }
        /// <summary>
        /// Контактные телефоны
        /// </summary>
        public string? PhonesLine { get; set; }
        /// <summary>
        /// Реферальная ссылка
        /// </summary>
        public int ReferalSource { get; set; }
        /// <summary>
        /// Контакты текстом
        /// </summary>
        public string? ContactData { get; set; }
        /// <summary>
        /// Баланс
        /// </summary>
        public double Balance { get; set; }
        /// <summary>
        /// ОПФ
        /// </summary>
        public int LegalForm { get; set; }
        /// <summary>
        /// S3 файл логотипа
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? Logo { get; set; }
    }
}
