using System.Text.Json;
using Data.Repository;
using System.ComponentModel.DataAnnotations.Schema;

namespace QAi.Data.QAiDb.Entities
{
    /// <summary>
    /// Пользователь
    /// </summary>
    public partial class User : IEntityKey<int>
    {
        /// <summary>
        /// Ид
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Логин
        /// </summary>
        public string? UserName { get; set; }
        /// <summary>
        /// Хеш пароля
        /// </summary>
        public string? PasswordHash { get; set; }
        /// <summary>
        /// Телефон для восстановления
        /// </summary>
        public string? PhoneNumber { get; set; }
        /// <summary>
        /// Защита от взлома
        /// </summary>
        public int FailedLoginCount { get; set; }
        /// <summary>
        /// Заблокирование/разблокирование
        /// </summary>
        public bool IsActive { get; set; }
        /// <summary>
        /// Подтверждена регистрация
        /// </summary>
        public bool IsApproved { get; set; }
        /// <summary>
        /// БИН/ИИН
        /// </summary>
        public string? IdentityNumber { get; set; }
        /// <summary>
        /// Юридический адрес
        /// </summary>
        public string? LegalAddress { get; set; }
        /// <summary>
        /// Договор
        /// </summary>
        public string? ContractLine { get; set; }
        /// <summary>
        /// Инициалы принимающего договор руководителя
        /// </summary>
        public string? ContractSupervisorInitials { get; set; }
        /// <summary>
        /// ФИО
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Инициалы
        /// </summary>
        public string? Initials { get; set; }
        /// <summary>
        /// Должность
        /// </summary>
        public string? PositionLine { get; set; }
        /// <summary>
        /// Статус: 1 - ожидает кода регистрации, 2 - зарегистрирован, 3 - ввести пин код
        /// </summary>
        public int State { get; set; }
        /// <summary>
        /// Код регистрации
        /// </summary>
        public string? RegistrationToken { get; set; }
        /// <summary>
        /// Время истечения блокировки
        /// </summary>
        public DateTime BlockExpiration { get; set; }
        /// <summary>
        /// Пуш токен
        /// </summary>
        public string? PushToken { get; set; }
        /// <summary>
        /// Signalr токен
        /// </summary>
        public string? SignalrToken { get; set; }
        /// <summary>
        /// Пин код
        /// </summary>
        public string? PinCode { get; set; }
        /// <summary>
        /// Время истечения пин кода
        /// </summary>
        public DateTime PinCodeExpiration { get; set; }
        /// <summary>
        /// RefreshToken
        /// </summary>
        [Column(TypeName = "jsonb")]
        public string? RefreshToken { get; set; }

        [InverseProperty("User")]
        public List<UserRole>? Roles { get; set; }
    }
}
