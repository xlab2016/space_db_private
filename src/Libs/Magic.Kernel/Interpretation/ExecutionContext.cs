using System.Threading;
using Magic.Kernel.Compilation;

namespace Magic.Kernel.Interpretation
{
    /// <summary>
    /// Ambient execution context for the interpreter, exposing the currently running ExecutableUnit
    /// via AsyncLocal so that lower-level components (drivers, devices) can access InstanceIndex safely.
    /// </summary>
    public static class ExecutionContext
    {
        private static readonly AsyncLocal<ExecutableUnit?> currentUnit = new AsyncLocal<ExecutableUnit?>();
        private static readonly AsyncLocal<Interpreter?> currentInterpreter = new AsyncLocal<Interpreter?>();
        private static readonly AsyncLocal<Devices.Store.DatabaseDevice?> currentDatabase = new AsyncLocal<Devices.Store.DatabaseDevice?>();
        private static readonly AsyncLocal<int> queryExecutionDepth = new AsyncLocal<int>();

        public static ExecutableUnit? CurrentUnit
        {
            get => currentUnit.Value;
            set => currentUnit.Value = value;
        }

        /// <summary>Current interpreter (set during execution) so devices can invoke lambdas (e.g. Table.any(predicate)).</summary>
        public static Interpreter? CurrentInterpreter
        {
            get => currentInterpreter.Value;
            set => currentInterpreter.Value = value;
        }

        /// <summary>Current runtime database (set when using Db). Used by Table.any to run predicate as SQL when lambda has ExprTree.</summary>
        public static Devices.Store.DatabaseDevice? CurrentDatabase
        {
            get => currentDatabase.Value;
            set => currentDatabase.Value = value;
        }

        public static bool IsExecutingQueryExpr
        {
            get => queryExecutionDepth.Value > 0;
        }

        public static void EnterQueryExprExecution()
        {
            queryExecutionDepth.Value++;
        }

        public static void ExitQueryExprExecution()
        {
            if (queryExecutionDepth.Value > 0)
                queryExecutionDepth.Value--;
        }

        /// <summary>
        /// Builds unified console prefix "executionUnit.Name: executionUnit.InstanceIndex: " (skipping missing parts).
        /// </summary>
        public static string GetPrefix(ExecutableUnit? unit = null)
        {
            unit ??= CurrentUnit;
            if (unit == null)
                return string.Empty;

            var namePart = string.IsNullOrWhiteSpace(unit.Name) ? string.Empty : unit.Name + ": ";
            var indexPart = unit.InstanceIndex is int i ? i + ": " : string.Empty;
            return namePart + indexPart;
        }
    }
}

