namespace Magic.Kernel.Processor
{
    public class Function
    {
        public string Name { get; set; } = string.Empty;
        public ExecutionBlock Body { get; set; } = new ExecutionBlock();
    }
}
