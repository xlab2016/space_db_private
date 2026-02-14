using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Interpretation
{
    public class Interpreter
    {
        private long instructionPointer = 0;
        private KernelConfiguration? _configuration;
        private ExecutableUnit? _unit;
        private ExecutionBlock? _currentBlock;

        private readonly Stack<CallFrame> _callStack = new Stack<CallFrame>();

        private sealed class CallFrame
        {
            public required ExecutionBlock Block { get; init; }
            public required long ReturnIp { get; init; }
            public string? Name { get; init; }
        }

        public List<object> Stack = new List<object>();
        public List<object> Registers = new List<object>();
        public Dictionary<long, object> Memory = new Dictionary<long, object>();

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
            instructionPointer = 0;
            _unit = executableUnit ?? throw new ArgumentNullException(nameof(executableUnit));
            _currentBlock = executableUnit.EntryPoint ?? new ExecutionBlock();
            _callStack.Clear();

            while (true)
            {
                if (_currentBlock == null)
                    break;

                // Implicit return when reaching end of a procedure/function body.
                if (instructionPointer >= _currentBlock.Count)
                {
                    if (_callStack.Count == 0)
                        break;

                    var frame = _callStack.Pop();
                    _currentBlock = frame.Block;
                    instructionPointer = frame.ReturnIp;
                    continue;
                }

                var command = _currentBlock[(int)instructionPointer];
                instructionPointer++; // advance first; Call/Ret may override instructionPointer
                await ExecuteAsync(command);
            }

            return new InterpretationResult
            {
                Success = true,
            };
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

            var result = await _configuration.DefaultDisk.AddVertex(vertex);
            
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

            var result = await _configuration.DefaultDisk.AddRelation(relation);
            
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

            var result = await _configuration.DefaultDisk.AddShape(shape);
            
            if (result != SpaceOperationResult.Success)
            {
                throw new InvalidOperationException($"Failed to add shape: {result}");
            }
        }

        private async Task ExecuteCallAsync(Command command)
        {
            if (command.Operand1 is not CallInfo callInfo)
            {
                throw new InvalidOperationException($"Call command expects CallInfo as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            // Аргументы на стеке: сверху arity, под ним arg1, arg2, ...
            if ((callInfo.Parameters == null || callInfo.Parameters.Count == 0) && Stack.Count >= 1)
            {
                var arityObj = Stack[Stack.Count - 1];
                Stack.RemoveAt(Stack.Count - 1);
                var n = arityObj is long l ? (int)l : (arityObj is int k ? k : 0);
                if (n > 0 && Stack.Count >= n)
                {
                    callInfo.Parameters ??= new Dictionary<string, object>();
                    for (var j = 0; j < n; j++)
                    {
                        var v = Stack[Stack.Count - 1];
                        Stack.RemoveAt(Stack.Count - 1);
                        callInfo.Parameters[$"{n - 1 - j}"] = v!;
                    }
                }
            }

            // 1) User-defined procedure/function in current executable unit
            if (_unit != null)
            {
                if (_unit.Procedures != null && _unit.Procedures.TryGetValue(callInfo.FunctionName, out var proc))
                {
                    if (_currentBlock == null) throw new InvalidOperationException("Interpreter current block is not initialized.");
                    _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = proc.Name });
                    _currentBlock = proc.Body ?? new ExecutionBlock();
                    instructionPointer = 0;
                    return;
                }

                if (_unit.Functions != null && _unit.Functions.TryGetValue(callInfo.FunctionName, out var func))
                {
                    if (_currentBlock == null) throw new InvalidOperationException("Interpreter current block is not initialized.");
                    _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = func.Name });
                    _currentBlock = func.Body ?? new ExecutionBlock();
                    instructionPointer = 0;
                    return;
                }
            }

            // 2) Fallback: system functions (backward compatible behavior)
            var systemFunctions = new SystemFunctions(_configuration, Stack, Memory);
            var handled = await systemFunctions.ExecuteAsync(callInfo);

            if (!handled)
                throw new NotImplementedException($"Function '{callInfo.FunctionName}' is not implemented (not found in Procedures/Functions and not a system function).");
        }

        private async Task ExecuteSysCallAsync(Command command)
        {
            if (command.Operand1 is not CallInfo callInfo)
            {
                throw new InvalidOperationException($"SysCall command expects CallInfo as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            var systemFunctions = new SystemFunctions(_configuration, Stack, Memory);
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

            var frame = _callStack.Pop();
            _currentBlock = frame.Block;
            instructionPointer = frame.ReturnIp;
            return Task.CompletedTask;
        }

        private async Task ExecutePopAsync(Command command)
        {
            if (command.Operand1 is not MemoryAddress memoryAddress)
            {
                throw new InvalidOperationException($"Pop command expects MemoryAddress as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            if (Stack.Count == 0)
            {
                throw new InvalidOperationException("Stack is empty. Cannot pop value.");
            }

            if (memoryAddress.Index == null)
            {
                throw new InvalidOperationException("Memory address index is not specified.");
            }

            // Извлекаем значение из стека
            var value = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            // Сохраняем в память
            Memory[memoryAddress.Index.Value] = value;
        }

        private Task ExecutePushAsync(Command command)
        {
            if (command.Operand1 is MemoryAddress memoryAddress)
            {
                if (memoryAddress.Index == null)
                    throw new InvalidOperationException("Memory address index is not specified.");
                if (!Memory.TryGetValue(memoryAddress.Index.Value, out var value))
                    throw new InvalidOperationException($"Memory[{memoryAddress.Index.Value}] is not set. Cannot push.");
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
                    _ => null
                };
                Stack.Add(val);
                return Task.CompletedTask;
            }
            throw new InvalidOperationException($"Push command expects MemoryAddress or PushOperand, but got {command.Operand1?.GetType().Name ?? "null"}");
        }

        private Task ExecuteDefAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Def: stack underflow.");
            var typeObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(typeObj);
            return Task.CompletedTask;
        }

        private Task ExecuteDefGenAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("DefGen: stack underflow.");
            var arity = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var typeArg = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (Stack.Count < 1) throw new InvalidOperationException("DefGen: base type missing.");
            var baseObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(baseObj);
            return Task.CompletedTask;
        }

        private Task ExecuteCallObjAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("CallObj: stack underflow.");
            var arity = Stack[Stack.Count - 1];
            var n = arity is long l ? (int)l : (arity is int k ? k : 0);
            if (Stack.Count < 1 + n + 1) throw new InvalidOperationException("CallObj: not enough args on stack.");
            for (var j = 0; j < n + 2; j++) Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(null!);
            return Task.CompletedTask;
        }

        private Task ExecuteAwaitObjAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("AwaitObj: stack underflow.");
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(null!);
            return Task.CompletedTask;
        }
    }
}
