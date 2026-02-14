namespace Magic.Kernel.Processor
{
    /// <summary>Operand for Push: slot [n], type literal (stream, file), int literal (arity), or string literal.</summary>
    public class PushOperand
    {
        public string Kind { get; set; } = "Slot"; // Slot | Type | IntLiteral | StringLiteral
        public object? Value { get; set; }
    }
}
