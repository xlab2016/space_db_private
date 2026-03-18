using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Processor;
using Magic.Kernel.Types;
using Magic.Kernel.Space;
using Magic.Kernel.Devices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

namespace Magic.Kernel.Interpretation
{
    public class Interpreter
    {
        private long instructionPointer = 0;
        private KernelConfiguration? _configuration;
        private ExecutableUnit? _unit;
        private ExecutionBlock? _currentBlock;

        private readonly Stack<CallFrame> _callStack = new Stack<CallFrame>();
        private Dictionary<string, int> _currentLabelOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool _skipToDefExpr;
        private readonly Stack<(ExecutionBlock Block, long ReturnIp)> _lambdaCallStack = new Stack<(ExecutionBlock, long)>();
        private readonly Stack<object?[]> _lambdaArgsStack = new Stack<object?[]>();

        private sealed class CallFrame
        {
            public required ExecutionBlock Block { get; init; }
            public required long ReturnIp { get; init; }
            public string? Name { get; init; }
        }

        public List<object> Stack = new List<object>();
        public List<object> Registers = new List<object>();

        /// <summary>Слои памяти текущего интерпретатора.</summary>
        public MemoryContext MemoryContext { get; } = new MemoryContext();

        /// <summary>
        /// Глобальная память интерпретатора (обертка над <see cref="MemoryContext"/> для обратной совместимости).
        /// Старые тесты и код обращались к <c>GlobalMemory</c> напрямую.
        /// </summary>
        public Dictionary<long, object> GlobalMemory => MemoryContext.Global;

        public KernelConfiguration? Configuration
        {
            get => _configuration;
            set => _configuration = value;
        }

        public async Task<List<InterpretationResult>> InterpreteAsync(List<ExecutableUnit> list)
        {
            var result = new List<InterpretationResult>();
            foreach (var item in list)
            {
                var unitResult = await InterpreteAsync(item);
                result.Add(unitResult);
            }
            return result;
        }

        public async Task<InterpretationResult> InterpreteAsync(ExecutableUnit executableUnit)
        {
            if (executableUnit == null)
                throw new ArgumentNullException(nameof(executableUnit));

            // Если сконфигурирован Erlang-подобный runtime, интерпретатор не выполняет unit сам,
            // а только делает spawn entrypoint в TaskQueue. Это fire-and-forget, как spawn/1.
            if (_configuration?.Runtime != null)
            {
                await _configuration.Runtime.SpawnAsync(executableUnit).ConfigureAwait(false);
                return new InterpretationResult
                {
                    Success = true,
                };
            }

            // Fallback: однопоточное выполнение entrypoint (старое поведение без runtime).
            instructionPointer = 0;
            _unit = executableUnit;
            _currentBlock = executableUnit.EntryPoint ?? new ExecutionBlock();
            _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
            _callStack.Clear();
            MemoryContext.ClearLocalScopes();

            // Для корневого запуска нет наследуемой памяти, локальная/глобальная начинаются с нуля.
            MemoryContext.Inherited.Clear();
            MemoryContext.Local.Clear();
            MemoryContext.Global.Clear();

            // SpaceName в ExecutableUnit: system|module|program для префикса ключей диска
            executableUnit.SpaceName = BuildSpaceName(executableUnit.System, executableUnit.Module, executableUnit.Name);

            var previousUnit = ExecutionContext.CurrentUnit;
            ExecutionContext.CurrentUnit = executableUnit;
            try
            {
                while (true)
                {
                    if (_currentBlock == null)
                        break;

                    // Implicit return when reaching end of a procedure/function body.
                    if (instructionPointer >= _currentBlock.Count)
                    {
                        if (_callStack.Count == 0)
                            break;
                        MemoryContext.PopLocalScopeAndClear();
                        var frame = _callStack.Pop();
                        _currentBlock = frame.Block;
                        instructionPointer = frame.ReturnIp;
                        continue;
                    }

                    var command = _currentBlock[(int)instructionPointer];
                    instructionPointer++; // advance first; Call/Ret may override instructionPointer
                    if (_skipToDefExpr)
                    {
                        if (command.Opcode == Opcodes.DefExpr)
                        {
                            await ExecuteDefExprAsync(command).ConfigureAwait(false);
                            _skipToDefExpr = false;
                        }
                        continue;
                    }
                    await ExecuteAsync(command);
                }

                return new InterpretationResult
                {
                    Success = true,
                };
            }
            finally
            {
                ExecutionContext.CurrentUnit = previousUnit;
            }
        }

        /// <summary>
        /// Run unit starting at entry point or at the given procedure/function/label (for runtime spawn).
        /// When entryName and callInfo are set, parameters are pushed onto stack (arg0..argN, then arity) and execution starts at that body.
        /// </summary>
        public async Task<InterpretationResult> InterpreteFromEntryAsync(ExecutableUnit executableUnit, string? entryName = null, Processor.CallInfo? callInfo = null, ExecutionBlock? startBlock = null)
        {
            _unit = executableUnit ?? throw new ArgumentNullException(nameof(executableUnit));
            _callStack.Clear();
            MemoryContext.ClearLocalScopes();
            // Global / Inherited уже проставлены рантаймом как наследованные слои.
            MemoryContext.Local.Clear();
            executableUnit.SpaceName = BuildSpaceName(executableUnit.System, executableUnit.Module, executableUnit.Name);

            if (!string.IsNullOrEmpty(entryName) && _unit.Procedures != null && _unit.Procedures.TryGetValue(entryName, out var proc))
            {
                _currentBlock = proc.Body ?? new ExecutionBlock();
                instructionPointer = 0;
                PushCallArgs(callInfo);
            }
            else if (!string.IsNullOrEmpty(entryName) && _unit.Functions != null && _unit.Functions.TryGetValue(entryName, out var func))
            {
                _currentBlock = func.Body ?? new ExecutionBlock();
                instructionPointer = 0;
                PushCallArgs(callInfo);
            }
            else if (!string.IsNullOrEmpty(entryName))
            {
                _currentBlock = startBlock ?? executableUnit.EntryPoint ?? new ExecutionBlock();
                _currentLabelOffsets = BuildLabelOffsets(_currentBlock);

                if (_currentLabelOffsets.TryGetValue(entryName, out var labelOffset))
                {
                    instructionPointer = labelOffset;
                    PushCallArgs(callInfo);
                }
                else
                {
                    _currentBlock = executableUnit.EntryPoint ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);

                    if (_currentLabelOffsets.TryGetValue(entryName, out labelOffset))
                    {
                        instructionPointer = labelOffset;
                        PushCallArgs(callInfo);
                    }
                    else
                    {
                        instructionPointer = 0;
                    }
                }
            }
            else
            {
                _currentBlock = executableUnit.EntryPoint ?? new ExecutionBlock();
                instructionPointer = 0;
            }

            _currentLabelOffsets = BuildLabelOffsets(_currentBlock!);

            var previousUnit = ExecutionContext.CurrentUnit;
            ExecutionContext.CurrentUnit = executableUnit;
            try
            {
                while (true)
                {
                    if (_currentBlock == null)
                        break;
                    if (instructionPointer >= _currentBlock.Count)
                    {
                        if (_callStack.Count == 0)
                            break;
                        MemoryContext.PopLocalScopeAndClear();
                        var frame = _callStack.Pop();
                        _currentBlock = frame.Block;
                        _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                        instructionPointer = frame.ReturnIp;
                        continue;
                    }
                    var command = _currentBlock[(int)instructionPointer];
                    instructionPointer++;
                    if (_skipToDefExpr)
                    {
                        if (command.Opcode == Opcodes.DefExpr)
                        {
                            await ExecuteDefExprAsync(command).ConfigureAwait(false);
                            _skipToDefExpr = false;
                        }
                        continue;
                    }
                    await ExecuteAsync(command);
                }
                return new InterpretationResult { Success = true };
            }
            finally
            {
                ExecutionContext.CurrentUnit = previousUnit;
            }
        }

        private void PushCallArgs(Processor.CallInfo? callInfo)
        {
            if (callInfo?.Parameters == null || callInfo.Parameters.Count == 0)
            {
                Stack.Add(0);
                return;
            }
            var indexed = callInfo.Parameters.Keys
                .Where(k => k.Length <= 2 && k.All(char.IsDigit))
                .OrderBy(k => int.Parse(k))
                .Select(k => callInfo.Parameters[k])
                .ToArray();
            foreach (var v in indexed)
                Stack.Add(v!);
            Stack.Add(indexed.Length);
        }

        private async Task ExecuteAsync(Command command)
        {
            switch (command.Opcode)
            {
                case Opcodes.Nop:
                    break;

                case Opcodes.AddVertex:
                    await ExecuteAddVertexAsync(command);
                    break;

                case Opcodes.AddRelation:
                    await ExecuteAddRelationAsync(command);
                    break;

                case Opcodes.AddShape:
                    await ExecuteAddShapeAsync(command);
                    break;

                case Opcodes.Call:
                    await ExecuteCallAsync(command);
                    break;

                case Opcodes.ACall:
                    await ExecuteACallAsync(command);
                    break;

                case Opcodes.Pop:
                    await ExecutePopAsync(command);
                    break;

                case Opcodes.Push:
                    await ExecutePushAsync(command);
                    break;

                case Opcodes.Def:
                    await ExecuteDefAsync(command);
                    break;
                case Opcodes.DefGen:
                    await ExecuteDefGenAsync(command);
                    break;
                case Opcodes.CallObj:
                    await ExecuteCallObjAsync(command);
                    break;
                case Opcodes.AwaitObj:
                    await ExecuteAwaitObjAsync(command);
                    break;
                case Opcodes.StreamWaitObj:
                    await ExecuteStreamWaitObjAsync(command);
                    break;
                case Opcodes.Await:
                    await ExecuteAwaitAsync(command);
                    break;
                case Opcodes.Label:
                    await ExecuteLabelAsync(command);
                    break;
                case Opcodes.Cmp:
                    await ExecuteCmpAsync(command);
                    break;
                case Opcodes.Je:
                    await ExecuteJeAsync(command);
                    break;
                case Opcodes.Jmp:
                    await ExecuteJmpAsync(command);
                    break;
                case Opcodes.GetObj:
                    await ExecuteGetObjAsync(command);
                    break;
                case Opcodes.SetObj:
                    await ExecuteSetObjAsync(command);
                    break;
                case Opcodes.StreamWait:
                    await ExecuteStreamWaitAsync(command);
                    break;

                case Opcodes.Expr:
                    await ExecuteExprAsync(command);
                    break;
                case Opcodes.DefExpr:
                    await ExecuteDefExprAsync(command);
                    break;
                case Opcodes.Lambda:
                    break; // no-op; lambda ref already on stack from Expr
                case Opcodes.Equals:
                    await ExecuteEqualsAsync(command);
                    break;
                case Opcodes.Lt:
                    await ExecuteLtAsync(command);
                    break;
                case Opcodes.Not:
                    await ExecuteNotAsync(command);
                    break;

                case Opcodes.SysCall:
                    await ExecuteSysCallAsync(command);
                    break;

                case Opcodes.Ret:
                    await ExecuteRetAsync();
                    break;

                default:
                    throw new NotImplementedException($"The OpCode '{command.Opcode}' is not implemented.");
            }
        }

        private async Task ExecuteAddVertexAsync(Command command)
        {
            if (command.Operand1 is not Vertex vertex)
            {
                throw new InvalidOperationException($"AddVertex command expects Vertex as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            if (_configuration?.DefaultDisk == null)
            {
                throw new InvalidOperationException("DefaultSpaceDisk is not configured. Cannot execute AddVertex command.");
            }

            var result = await _configuration.DefaultDisk.AddVertex(vertex, _unit!.SpaceName);

            if (result != SpaceOperationResult.Success)
            {
                throw new InvalidOperationException($"Failed to add vertex: {result}");
            }
        }

        private async Task ExecuteAddRelationAsync(Command command)
        {
            if (command.Operand1 is not Relation relation)
            {
                throw new InvalidOperationException($"AddRelation command expects Relation as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            if (_configuration?.DefaultDisk == null)
            {
                throw new InvalidOperationException("DefaultSpaceDisk is not configured. Cannot execute AddRelation command.");
            }

            var result = await _configuration.DefaultDisk.AddRelation(relation, _unit!.SpaceName);

            if (result != SpaceOperationResult.Success)
            {
                throw new InvalidOperationException($"Failed to add relation: {result}");
            }
        }

        private async Task ExecuteAddShapeAsync(Command command)
        {
            if (command.Operand1 is not Shape shape)
            {
                throw new InvalidOperationException($"AddShape command expects Shape as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            if (_configuration?.DefaultDisk == null)
            {
                throw new InvalidOperationException("DefaultSpaceDisk is not configured. Cannot execute AddShape command.");
            }

            var result = await _configuration.DefaultDisk.AddShape(shape, _unit!.SpaceName);

            if (result != SpaceOperationResult.Success)
            {
                throw new InvalidOperationException($"Failed to add shape: {result}");
            }
        }

        private async Task ExecuteCallAsync(Command command)
        {
            // Важно: не мутируем CallInfo, лежащий внутри команды (Operand1),
            // чтобы байткод оставался иммутабельным во время исполнения.
            var callInfoTemplate = GetCallInfo(command, "Call");
            var callInfo = CloneCallInfo(callInfoTemplate);
            PopulateCallParametersFromStack(callInfo);
            await ExecuteResolvedCallAsync(callInfo).ConfigureAwait(false);
        }

        private async Task ExecuteACallAsync(Command command)
        {
            // Аналогично Call: работаем только с копией CallInfo.
            var callInfoTemplate = GetCallInfo(command, "ACall");
            var callInfo = CloneCallInfo(callInfoTemplate);
            PopulateCallParametersFromStack(callInfo);

            if (await TrySpawnCallAsync(callInfo).ConfigureAwait(false))
                return;

            await ExecuteResolvedCallAsync(callInfo).ConfigureAwait(false);
        }

        private CallInfo GetCallInfo(Command command, string opcodeName)
        {
            if (command.Operand1 is not CallInfo callInfo)
                throw new InvalidOperationException($"{opcodeName} command expects CallInfo as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");

            return callInfo;
        }

        private static CallInfo CloneCallInfo(CallInfo source)
        {
            var clone = new CallInfo
            {
                FunctionName = source.FunctionName,
                Parameters = source.Parameters != null
                    ? new Dictionary<string, object>(source.Parameters, StringComparer.Ordinal)
                    : new Dictionary<string, object>(StringComparer.Ordinal)
            };
            return clone;
        }

        private void PopulateCallParametersFromStack(CallInfo callInfo)
        {
            // Аргументы на стеке: сверху arity, под ним arg1, arg2, ...
            if ((callInfo.Parameters == null || callInfo.Parameters.Count == 0) && Stack.Count > 0)
            {
                var (n, args) = PopArityAndArgs(extraRequired: 0);
                callInfo.Parameters ??= new Dictionary<string, object>();
                for (var i = 0; i < n; i++)
                    callInfo.Parameters[$"{i}"] = args[i]!;
            }
        }

        private async Task<bool> TrySpawnCallAsync(CallInfo callInfo)
        {
            if (_configuration?.Runtime == null || _unit == null || string.IsNullOrEmpty(callInfo.FunctionName))
                return false;

            string? entryNameForSpawn = null;
            ExecutionBlock? startBlockForSpawn = null;

            if (_unit.Procedures != null && _unit.Procedures.TryGetValue(callInfo.FunctionName, out var proc))
            {
                entryNameForSpawn = proc.Name;
            }
            else if (_unit.Functions != null && _unit.Functions.TryGetValue(callInfo.FunctionName, out var func))
            {
                entryNameForSpawn = func.Name;
            }
            else if (_currentBlock != null && _currentLabelOffsets.TryGetValue(callInfo.FunctionName, out _))
            {
                entryNameForSpawn = callInfo.FunctionName;
                startBlockForSpawn = _currentBlock;
            }

            if (string.IsNullOrEmpty(entryNameForSpawn))
                return false;

            Dictionary<long, object>? inheritedLocal = null;
            if (MemoryContext.Local.Count > 0)
                inheritedLocal = new Dictionary<long, object>(MemoryContext.Local);

            Dictionary<long, object>? inheritedGlobal = null;
            if (MemoryContext.Global.Count > 0)
                inheritedGlobal = new Dictionary<long, object>(MemoryContext.Global);

            await _configuration.Runtime.SpawnAsync(
                _unit,
                entryNameForSpawn,
                callInfo,
                tag: null,
                inheritedLocalMemory: inheritedLocal,
                inheritedGlobalMemory: inheritedGlobal,
                startBlock: startBlockForSpawn
            ).ConfigureAwait(false);

            return true;
        }

        private async Task ExecuteResolvedCallAsync(CallInfo callInfo)
        {
            // 1) User-defined procedure/function in current executable unit.
            if (_unit != null)
            {
                if (_unit.Procedures != null && _unit.Procedures.TryGetValue(callInfo.FunctionName, out var proc))
                {
                    if (_currentBlock == null) throw new InvalidOperationException("Interpreter current block is not initialized.");
                    MemoryContext.PushLocalScope();
                    _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = proc.Name });
                    _currentBlock = proc.Body ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                    instructionPointer = 0;
                    return;
                }

                if (_unit.Functions != null && _unit.Functions.TryGetValue(callInfo.FunctionName, out var func))
                {
                    if (_currentBlock == null) throw new InvalidOperationException("Interpreter current block is not initialized.");
                    MemoryContext.PushLocalScope();
                    _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = func.Name });
                    _currentBlock = func.Body ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                    instructionPointer = 0;
                    return;
                }
            }

            // 2) Локальная подпрограмма по label в текущем блоке (для streamwait-loop делегатов и прочих локальных "функций").
            if (_currentBlock != null && !string.IsNullOrEmpty(callInfo.FunctionName) &&
                _currentLabelOffsets.TryGetValue(callInfo.FunctionName, out _))
            {
                MemoryContext.PushLocalScope();
                _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = callInfo.FunctionName });
                instructionPointer = ResolveLabelOffset(callInfo.FunctionName);
                return;
            }

            // 3) Fallback: system functions (backward compatible behavior)
            var vaultReader = _configuration?.VaultReader ?? new EnvironmentVaultReader();
            var systemFunctions = CreateSystemFunctions(vaultReader);
            var handled = await systemFunctions.ExecuteAsync(callInfo).ConfigureAwait(false);

            if (!handled)
                throw new NotImplementedException($"Function '{callInfo.FunctionName}' is not implemented (not found in Procedures/Functions and not a system function).");
        }
        private async Task ExecuteSysCallAsync(Command command)
        {
            if (command.Operand1 is not CallInfo callInfo)
            {
                throw new InvalidOperationException($"SysCall command expects CallInfo as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            var vaultReader = _configuration?.VaultReader ?? new EnvironmentVaultReader();
            var systemFunctions = CreateSystemFunctions(vaultReader);
            var handled = await systemFunctions.ExecuteAsync(callInfo);

            if (!handled)
                throw new NotImplementedException($"System function '{callInfo.FunctionName}' is not implemented.");
        }

        private Task ExecuteRetAsync()
        {
            if (_callStack.Count == 0)
            {
                // Return from entrypoint: terminate execution
                if (_currentBlock != null)
                    instructionPointer = _currentBlock.Count;
                return Task.CompletedTask;
            }

            MemoryContext.PopLocalScopeAndClear();
            var frame = _callStack.Pop();
            _currentBlock = frame.Block;
            _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
            instructionPointer = frame.ReturnIp;
            return Task.CompletedTask;
        }

        private Task ExecutePopAsync(Command command)
        {
            if (Stack.Count == 0)
            {
                // Нечего снимать со стека — просто выходим.
                return Task.CompletedTask;
            }

            // Вариант без операнда: просто снять верхнее значение со стека и отбросить.
            if (command.Operand1 is not MemoryAddress memoryAddress)
            {
                Stack.RemoveAt(Stack.Count - 1);
                return Task.CompletedTask;
            }

            if (memoryAddress.Index == null)
            {
                // Исторически пустой pop мог компилироваться как pop с MemoryAddress без индекса.
                // В таком случае ведём себя как унарный pop: просто снимаем верхнее значение.
                Stack.RemoveAt(Stack.Count - 1);
                return Task.CompletedTask;
            }

            // Извлекаем значение из стека
            var value = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            // Сохраняем в память: верхний executionUnit — по умолчанию GlobalMemory
            MemoryContext.Write(memoryAddress, value, IsExecutingEntryPoint(), _configuration?.Runtime != null);
            return Task.CompletedTask;
        }

        private SystemFunctions CreateSystemFunctions(IVaultReader vaultReader)
        {
            return new SystemFunctions(
                _configuration,
                Stack,
                memoryAddress =>
                {
                    var found = MemoryContext.TryRead(memoryAddress, IsExecutingEntryPoint(), out var value);
                    return (found, value);
                },
                (memoryAddress, value) =>
                {
                    MemoryContext.Write(memoryAddress, value, IsExecutingEntryPoint(), _configuration?.Runtime != null);
                },
                vaultReader);
        }

        private bool IsExecutingEntryPoint()
        {
            return _unit?.EntryPoint != null
                && _currentBlock != null
                && ReferenceEquals(_currentBlock, _unit.EntryPoint);
        }

        private Task ExecutePushAsync(Command command)
        {
            if (command.Operand1 is MemoryAddress memoryAddress)
            {
                var value = MemoryContext.Read(memoryAddress, IsExecutingEntryPoint());
                Stack.Add(value);
                return Task.CompletedTask;
            }
            if (command.Operand1 is PushOperand po)
            {
                object? val = po.Kind switch
                {
                    "Type" => po.Value,
                    "IntLiteral" => po.Value,
                    "StringLiteral" => po.Value,
                    "LambdaArg" => _lambdaArgsStack.Count > 0 && po.Value is int argIndex && argIndex >= 0 && argIndex < _lambdaArgsStack.Peek().Length
                        ? _lambdaArgsStack.Peek()[argIndex]
                        : null,
                    _ => null
                };
                Stack.Add(val!);
                return Task.CompletedTask;
            }
            throw new InvalidOperationException($"Push command expects MemoryAddress or PushOperand, but got {command.Operand1?.GetType().Name ?? "null"}");
        }

        private Task ExecuteDefAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Def: stack underflow.");

            var arityObj = Stack[Stack.Count - 1];
            if (arityObj is long or int)
            {
                var n = arityObj is long l ? (int)l : (int)arityObj;
                if (n > 0 && Stack.Count >= n + 1)
                {
                    var (_, args) = PopArityAndArgs(extraRequired: 0);
                    Stack.Add(Hal.Def(args, _unit)!);
                    return Task.CompletedTask;
                }
            }

            var typeObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(Hal.Def(typeObj, _unit));
            return Task.CompletedTask;
        }

        private Task ExecuteDefGenAsync(Command command)
        {
            (_, var genValues) = PopArityAndArgs(extraRequired: 1);
            var defObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(Hal.DefGen(defObj, genValues));
            return Task.CompletedTask;
        }

        private async Task ExecuteCallObjAsync(Command command)
        {
            (_, var args) = PopArityAndArgs(extraRequired: 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var methodName = command.Operand1 as string ?? "";
            var previous = ExecutionContext.CurrentInterpreter;
            ExecutionContext.CurrentInterpreter = this;
            try
            {
                var result = await Hal.CallObjAsync(obj, methodName, args).ConfigureAwait(false);
                Stack.Add(result!);
            }
            finally
            {
                ExecutionContext.CurrentInterpreter = previous;
            }
        }

        private Task ExecuteGetObjAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("GetObj: stack underflow.");
            var memberNameObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var value = Hal.GetObj(obj, memberNameObj?.ToString());
            Stack.Add(value!);
            return Task.CompletedTask;
        }

        private Task ExecuteSetObjAsync(Command command)
        {
            if (Stack.Count < 3) throw new InvalidOperationException("SetObj: stack underflow.");
            var value = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var memberNameObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var updated = Hal.SetObj(obj, memberNameObj?.ToString(), value);
            Stack.Add(updated!);
            return Task.CompletedTask;
        }

        private Task ExecuteExprAsync(Command command)
        {
            if (_currentBlock == null) throw new InvalidOperationException("Expr: no current block.");
            // instructionPointer already points to first instruction after Expr (main loop incremented it before calling us)
            var body = new ExecutionBlock();
            long ip = instructionPointer;
            while (ip < _currentBlock.Count)
            {
                var cmd = _currentBlock[(int)ip];
                if (cmd.Opcode == Opcodes.DefExpr)
                    break;
                body.Add(cmd);
                ip++;
            }
            // Leave instructionPointer at first body instruction; main loop will skip until DefExpr then run ExecuteDefExprAsync
            Stack.Add(new LambdaValue(body));
            _skipToDefExpr = true;
            return Task.CompletedTask;
        }

        /// <summary>Build ExprTree from lambda body; resolves push [slot]/pop [slot] via MemoryContext.</summary>
        private ExprTree? TryBuildExprTreeFromBody(IReadOnlyList<Command> body)
        {
            if (body == null || body.Count == 0)
                return null;
            var exprStack = new Stack<ExprTree>();
            var exprMemory = new Dictionary<long, ExprTree>();
            var isEntry = IsExecutingEntryPoint();
            foreach (var cmd in body)
            {
                switch (cmd.Opcode)
                {
                    case Opcodes.Push:
                        if (cmd.Operand1 is MemoryAddress ma && ma.Index.HasValue)
                        {
                            if (exprMemory.TryGetValue(ma.Index.Value, out var stored))
                                exprStack.Push(stored);
                            else
                            {
                                var value = MemoryContext.Read(ma, isEntry);
                                exprStack.Push(new ExprConstant(value));
                            }
                            break;
                        }
                        if (cmd.Operand1 is PushOperand po)
                        {
                            if (po.Kind == "LambdaArg" && (po.Value is int iArg || (po.Value is long lArg && lArg >= 0 && lArg <= int.MaxValue)))
                            {
                                exprStack.Push(new ExprParameter(po.Value is int i ? i : (int)(long)po.Value));
                                break;
                            }
                            if (po.Kind == "StringLiteral" && po.Value is string s)
                            {
                                exprStack.Push(new ExprConstant(s));
                                break;
                            }
                            if (po.Kind == "IntLiteral" && po.Value != null)
                            {
                                exprStack.Push(new ExprConstant(po.Value));
                                break;
                            }
                        }
                        return null;
                    case Opcodes.Pop:
                        if (exprStack.Count == 0)
                            return null;
                        var top = exprStack.Pop();
                        if (cmd.Operand1 is MemoryAddress popMa && popMa.Index.HasValue)
                            exprMemory[popMa.Index.Value] = top;
                        break;
                    case Opcodes.GetObj:
                        if (exprStack.Count < 2)
                            return null;
                        var memberNameNode = exprStack.Pop();
                        var target = exprStack.Pop();
                        if (memberNameNode is not ExprConstant nameConst || nameConst.Value is not string memberName)
                            return null;
                        exprStack.Push(new ExprMemberAccess(target, memberName));
                        break;
                    case Opcodes.Equals:
                        if (exprStack.Count < 2)
                            return null;
                        var right = exprStack.Pop();
                        var left = exprStack.Pop();
                        exprStack.Push(new ExprEqual(left, right));
                        break;
                    case Opcodes.Lambda:
                        if (exprStack.Count == 0)
                            return null;
                        var lambdaBody = exprStack.Pop();
                        var args = new List<ExprTree>();
                        while (exprStack.Count > 0)
                        {
                            // вставляем в начало, чтобы сохранить порядок push-ей как порядок аргументов
                            args.Insert(0, exprStack.Pop());
                        }
                        exprStack.Push(new ExprLambda(args, lambdaBody));
                        break;
                    default:
                        return null;
                }
            }
            return exprStack.Count == 1 ? exprStack.Pop() : null;
        }

        private Task ExecuteDefExprAsync(Command command)
        {
            // If top of stack is LambdaValue without ExprTree: build tree from body (with memory resolution) and replace with LambdaValue(body, tree)
            if (Stack.Count > 0 && Stack[Stack.Count - 1] is LambdaValue lv && lv.ExprTree == null)
            {
                Stack.RemoveAt(Stack.Count - 1);
                var tree = TryBuildExprTreeFromBody(lv.Body);
                Stack.Add(new LambdaValue(lv.Body, tree));
                return Task.CompletedTask;
            }
            if (_lambdaCallStack.Count > 0)
            {
                var (block, returnIp) = _lambdaCallStack.Pop();
                _lambdaArgsStack.Pop();
                _currentBlock = block;
                _currentLabelOffsets = BuildLabelOffsets(block);
                instructionPointer = returnIp;
            }
            return Task.CompletedTask;
        }

        private Task ExecuteEqualsAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Equals: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var eq = a == null ? b == null : (b != null && a.Equals(b));
            Stack.Add(eq);
            return Task.CompletedTask;
        }

        private Task ExecuteNotAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Not: stack underflow.");
            var value = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(IsTruthy(value) ? 0L : 1L);
            return Task.CompletedTask;
        }

        private Task ExecuteLtAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Lt: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var lt = CompareForLt(a, b);
            Stack.Add(lt ? 1L : 0L);
            return Task.CompletedTask;
        }

        private static bool CompareForLt(object? a, object? b)
        {
            if (a is FloatDecimal leftFloatDecimal && b is FloatDecimal rightFloatDecimal)
                return leftFloatDecimal < rightFloatDecimal;

            if (TryConvertToDecimal(a, out var leftNum) && TryConvertToDecimal(b, out var rightNum))
                return leftNum < rightNum;
            var sa = a?.ToString() ?? "";
            var sb = b?.ToString() ?? "";
            return string.Compare(sa, sb, StringComparison.Ordinal) < 0;
        }

        /// <summary>Invoke a lambda with given arguments. Used by devices (e.g. Table.any(predicate)).</summary>
        public async Task<object?> InvokeLambdaAsync(LambdaValue lambda, object?[] args)
        {
            if (lambda == null) throw new ArgumentNullException(nameof(lambda));
            var stackBase = Stack.Count;
            _lambdaArgsStack.Push(args ?? Array.Empty<object?>());
            object? result = null;
            try
            {
                foreach (var cmd in lambda.Body)
                    await ExecuteAsync(cmd);
                if (Stack.Count > stackBase)
                {
                    result = Stack[Stack.Count - 1];
                    Stack.RemoveRange(stackBase, Stack.Count - stackBase);
                }
            }
            finally
            {
                if (Stack.Count > stackBase)
                    Stack.RemoveRange(stackBase, Stack.Count - stackBase);
                _lambdaArgsStack.Pop();
            }
            return result;
        }

        private Task ExecuteStreamWaitAsync(Command command)
        {
            if (Stack.Count < 1)
                throw new InvalidOperationException("StreamWait: stack underflow.");

            // New contract: stack = [..., functionName, arg0, ..., argN-1, arity]
            // For backward-compat (old binaries), when there is only a single value on stack,
            // treat it as the payload.
            if (Stack.Count == 1)
            {
                var single = Stack[Stack.Count - 1];
                Stack.RemoveAt(Stack.Count - 1);
                Hal.StreamWaitAsync(ExecutionContext.CurrentUnit, null, single);
                return Task.CompletedTask;
            }

            // Expect at least functionName + arity.
            if (Stack.Count < 2)
                throw new InvalidOperationException("StreamWait: invalid stack layout (expected function and arity).");

            // Pop arity and arguments, leaving functionName on the stack.
            var (n, args) = PopArityAndArgs(extraRequired: 1);
            if (Stack.Count < 1)
                throw new InvalidOperationException("StreamWait: missing function name on stack.");

            var fnObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var fnName = fnObj?.ToString() ?? string.Empty;

            Hal.StreamWaitAsync(ExecutionContext.CurrentUnit, fnName, args);
            return Task.CompletedTask;
        }

        private async Task ExecuteStreamWaitObjAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("StreamWaitObj: stack underflow.");
            var streamWaitTypeObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            var streamWaitType = streamWaitTypeObj?.ToString() ?? "delta";
            var result = await Hal.StreamWaitObjAsync(obj, streamWaitType).ConfigureAwait(false);
            if (string.Equals(streamWaitType, "data", StringComparison.OrdinalIgnoreCase))
                Stack.Add(result.Delta!);
            else
            {
                Stack.Add(result.Aggregate!);
                Stack.Add(result.Delta!);
                Stack.Add(result.IsEnd ? 1L : 0L);
            }
        }

        private async Task ExecuteAwaitObjAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("AwaitObj: stack underflow.");
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var result = await Hal.ExecuteAwaitObjAsync(obj).ConfigureAwait(false);
            Stack.Add(result!);
        }

        private async Task ExecuteAwaitAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Await: stack underflow.");
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            if (obj is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                    Stack.Add(resultProperty?.GetValue(task)!);
                }
                else
                {
                    Stack.Add(null!);
                }
                return;
            }

            if (obj is IDefType)
            {
                var awaited = await Hal.ExecuteAwaitAsync(obj).ConfigureAwait(false);
                Stack.Add(awaited!);
                return;
            }

            Stack.Add(obj!);
        }

        private Task ExecuteLabelAsync(Command command)
        {
            return Task.CompletedTask;
        }

        private Task ExecuteCmpAsync(Command command)
        {
            if (!TryResolveCmpOperandValue(command.Operand1, out var left))
            {
                Stack.Add(0L);
                return Task.CompletedTask;
            }

            if (!TryResolveCmpOperandValue(command.Operand2, out var right))
            {
                Stack.Add(0L);
                return Task.CompletedTask;
            }

            Stack.Add(AreCmpValuesEqual(left, right) ? 1L : 0L);
            return Task.CompletedTask;
        }

        private Task ExecuteJeAsync(Command command)
        {
            if (Stack.Count == 0)
                throw new InvalidOperationException("Je: stack underflow (condition is missing).");

            var conditionObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (!IsTruthy(conditionObj))
                return Task.CompletedTask;

            var label = command.Operand1?.ToString() ?? "";
            instructionPointer = ResolveLabelOffset(label);
            return Task.CompletedTask;
        }

        private Task ExecuteJmpAsync(Command command)
        {
            var label = command.Operand1?.ToString() ?? "";
            instructionPointer = ResolveLabelOffset(label);
            return Task.CompletedTask;
        }

        /// <summary>Pops arity (int/long) from stack, then pops n values. Returns (n, args) with args[0] = first arg below arity. Throws if stack has fewer than 1 + n + extraRequired elements.</summary>
        private (int n, object?[] args) PopArityAndArgs(int extraRequired = 0)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Stack underflow (arity).");
            var arityObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var n = arityObj is long l ? (int)l : (arityObj is int k ? k : 0);
            if (Stack.Count < n + extraRequired) throw new InvalidOperationException("Not enough arguments on stack.");
            var args = new object?[n];
            for (var i = 0; i < n; i++)
            {
                args[n - 1 - i] = Stack[Stack.Count - 1];
                Stack.RemoveAt(Stack.Count - 1);
            }
            return (n, args);
        }

        private bool TryResolveCmpOperandValue(object? operand, out object? value)
        {
            value = null;
            if (operand == null)
                return false;

            if (operand is MemoryAddress memoryAddress)
                return TryResolveMemoryValue(memoryAddress, out value);

            if (operand is PushOperand pushOperand)
            {
                value = pushOperand.Value;
                return true;
            }

            value = operand;
            return true;
        }

        private bool TryResolveMemoryValue(MemoryAddress memoryAddress, out object? value)
        {
            return MemoryContext.TryRead(memoryAddress, IsExecutingEntryPoint(), out value);
        }

        private static bool AreCmpValuesEqual(object? left, object? right)
        {
            if (left is FloatDecimal leftFloatDecimal && right is FloatDecimal rightFloatDecimal)
                return leftFloatDecimal == rightFloatDecimal;

            if (TryConvertToDecimal(left, out var leftNumber) && TryConvertToDecimal(right, out var rightNumber))
                return leftNumber == rightNumber;

            if (left is string leftString && right is string rightString)
                return string.Equals(leftString, rightString, StringComparison.Ordinal);

            return Equals(left, right);
        }

        private static bool IsTruthy(object? value)
        {
            return value switch
            {
                null => false,
                bool b => b,
                byte b => b != 0,
                sbyte sb => sb != 0,
                short s => s != 0,
                ushort us => us != 0,
                int i => i != 0,
                uint ui => ui != 0,
                long l => l != 0,
                ulong ul => ul != 0,
                float f => f != 0f,
                double d => d != 0d,
                decimal dm => dm != 0m,
                FloatDecimal fd => !fd.IsZero,
                string str => !string.IsNullOrEmpty(str) &&
                              (str.Equals("true", StringComparison.OrdinalIgnoreCase) || str == "1"),
                _ => true
            };
        }

        private static bool TryConvertToDecimal(object? value, out decimal result)
        {
            result = 0m;
            switch (value)
            {
                case null:
                    return true;
                case byte b:
                    result = b;
                    return true;
                case sbyte sb:
                    result = sb;
                    return true;
                case short s:
                    result = s;
                    return true;
                case ushort us:
                    result = us;
                    return true;
                case int i:
                    result = i;
                    return true;
                case uint ui:
                    result = ui;
                    return true;
                case long l:
                    result = l;
                    return true;
                case ulong ul:
                    result = ul;
                    return true;
                case float f:
                    result = (decimal)f;
                    return true;
                case double d:
                    result = (decimal)d;
                    return true;
                case decimal dm:
                    result = dm;
                    return true;
                case FloatDecimal fd when fd.IsFinite &&
                                          decimal.TryParse(fd.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                    result = parsed;
                    return true;
                case bool b:
                    result = b ? 1m : 0m;
                    return true;
                case object o:
                    result = o != null ? 1m : 0m;
                    return true;
            }
        }

        private static Dictionary<string, int> BuildLabelOffsets(ExecutionBlock? block)
        {
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            if (block == null)
                return labels;

            for (var i = 0; i < block.Count; i++)
            {
                if (block[i].Opcode != Opcodes.Label)
                    continue;
                var labelName = block[i].Operand1?.ToString() ?? "";
                if (!string.IsNullOrEmpty(labelName))
                    labels[labelName] = i + 1;
            }

            return labels;
        }

        private int ResolveLabelOffset(string labelName)
        {
            if (_currentBlock == null)
                throw new InvalidOperationException("Current block is not set.");
            if (string.IsNullOrEmpty(labelName) || !_currentLabelOffsets.TryGetValue(labelName, out var offset))
                throw new InvalidOperationException($"Label '{labelName}' is not defined.");
            return offset;
        }

        /// <summary>spaceName = system|module|program (non-empty parts joined by "|"); empty string when all empty.</summary>
        private static string BuildSpaceName(string? systemName, string? moduleName, string? programName)
        {
            var s = string.IsNullOrEmpty(systemName) ? null : systemName.Trim();
            var m = string.IsNullOrEmpty(moduleName) ? null : moduleName.Trim();
            var p = string.IsNullOrEmpty(programName) ? null : programName.Trim();
            var parts = new List<string>();
            if (s != null) parts.Add(s);
            if (m != null) parts.Add(m);
            if (p != null) parts.Add(p);
            return parts.Count > 0 ? string.Join("|", parts) : string.Empty;
        }
    }
}
