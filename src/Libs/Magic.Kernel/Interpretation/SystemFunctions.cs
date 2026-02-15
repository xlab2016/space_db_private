using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.SSC;
using Magic.Kernel.Devices.Streams;
using Magic.Kernel.Functions;
using Magic.Kernel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Magic.Kernel.Interpretation
{
    public class SystemFunctions
    {
        private readonly KernelConfiguration? _configuration;
        private readonly List<object> _stack;
        private readonly Dictionary<long, object> _memory;
        private readonly PrintFunctions _printFunctions;

        public SystemFunctions(KernelConfiguration? configuration, List<object> stack, Dictionary<long, object> memory)
        {
            _configuration = configuration;
            _stack = stack;
            _memory = memory;
            _printFunctions = new PrintFunctions(configuration, stack, memory);
        }

        public async Task<bool> ExecuteAsync(CallInfo callInfo)
        {
            switch (callInfo.FunctionName.ToLower())
            {
                case "origin":
                    await ExecuteOriginAsync(callInfo);
                    return true;

                case "print":
                    await ExecutePrintAsync(callInfo);
                    return true;

                case "intersect":
                    await ExecuteIntersectAsync(callInfo);
                    return true;

                case "stream_open":
                    await ExecuteStreamOpenAsync(callInfo);
                    return true;

                case "stream_await":
                    await ExecuteStreamAwaitAsync(callInfo);
                    return true;

                case "compile":
                    await ExecuteCompileAsync(callInfo);
                    return true;

                default:
                    return false;
            }
        }

        private Task ExecuteStreamOpenAsync(CallInfo callInfo)
        {
            if (!callInfo.Parameters.TryGetValue("path", out var pathParam) && !callInfo.Parameters.TryGetValue("0", out pathParam))
            {
                throw new InvalidOperationException("stream_open requires path parameter.");
            }
            var path = pathParam is string s ? s : pathParam?.ToString() ?? "";
            var handle = new StreamHandle { Path = path };
            _stack.Add(handle);
            return Task.CompletedTask;
        }

        private Task ExecuteStreamAwaitAsync(CallInfo callInfo)
        {
            object? streamObj = null;
            if (callInfo.Parameters?.TryGetValue("0", out var p) == true)
                streamObj = p is MemoryAddress ma && ma.Index.HasValue && _memory.TryGetValue(ma.Index.Value, out var memVal) ? memVal : p;
            if (streamObj == null && _stack.Count > 0)
            {
                streamObj = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
            }
            if (streamObj == null)
                throw new InvalidOperationException("stream_await requires stream on stack or path parameter.");
            if (streamObj is MemoryAddress ma2 && ma2.Index.HasValue && _memory.TryGetValue(ma2.Index.Value, out var memVal2))
                streamObj = memVal2;
            if (streamObj is not StreamHandle handle)
                throw new InvalidOperationException("stream_await expects a stream handle.");
            var data = ReadStreamData(handle);
            _stack.Add(data);
            return Task.CompletedTask;
        }

        private async Task ExecuteCompileAsync(CallInfo callInfo)
        {
            object? dataObj = null;
            if (_stack.Count > 0)
            {
                dataObj = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
            }
            else if (callInfo.Parameters?.TryGetValue("0", out var p) == true)
            {
                dataObj = p is MemoryAddress memAddr && memAddr.Index.HasValue && _memory.TryGetValue(memAddr.Index.Value, out var memVal) ? memVal : p;
            }
            if (dataObj == null)
                dataObj = new Dictionary<string, object> { ["content"] = "" };

            // Если на стеке был stream handle — вызываем disk.CompileAsync(device, compiler)
            if (dataObj is StreamHandle streamHandle && _configuration?.DefaultDisk != null && _configuration.DefaultSSCCompiler != null)
            {
                var device = new FileStreamDevice(streamHandle.Path);
                var result = await _configuration.DefaultDisk.CompileAsync(device, _configuration.DefaultSSCCompiler).ConfigureAwait(false);
                _stack.Add(result);
                return;
            }

            var ssfs = CompileToSsfs(dataObj);
            _stack.Add(ssfs);
        }

        private static object ReadStreamData(StreamHandle handle)
        {
            if (string.IsNullOrEmpty(handle.Path))
                return new Dictionary<string, object> { ["content"] = "" };
            try
            {
                if (File.Exists(handle.Path))
                {
                    var content = File.ReadAllText(handle.Path);
                    return new Dictionary<string, object> { ["content"] = content, ["path"] = handle.Path };
                }
            }
            catch
            {
                // fallback for tests: path may be logical name
            }
            return new Dictionary<string, object> { ["content"] = "", ["path"] = handle.Path };
        }

        private static object CompileToSsfs(object data)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "SSFS",
                ["structural_layer"] = new Dictionary<string, object>(StringComparer.Ordinal),
                ["semantic_layer"] = new Dictionary<string, object>(StringComparer.Ordinal),
                ["ontology_layer"] = new Dictionary<string, object>(StringComparer.Ordinal)
            };
            if (data is Dictionary<string, object> dataDict && dataDict.TryGetValue("content", out var content))
            {
                ((Dictionary<string, object>)dict["semantic_layer"])["source"] = content;
            }
            else
            {
                ((Dictionary<string, object>)dict["semantic_layer"])["source"] = data?.ToString() ?? "";
            }
            return dict;
        }

        private sealed class StreamHandle
        {
            public string Path { get; set; } = "";
        }

        private async Task ExecuteOriginAsync(CallInfo callInfo)
        {
            var shapes = new List<Shape>();

            // Собираем все shapes из параметров
            foreach (var param in callInfo.Parameters.Values)
            {
                Shape? shape = null;

                if (param is Dictionary<string, object> entityRef)
                {
                    // Параметр вида: { "type": EntityType.Shape, "index": 1 }
                    if (entityRef.TryGetValue("type", out var typeObj) &&
                        entityRef.TryGetValue("index", out var indexObj) &&
                        typeObj is EntityType entityType &&
                        entityType == EntityType.Shape &&
                        indexObj is long index)
                    {
                        if (_configuration?.DefaultDisk != null)
                        {
                            shape = await _configuration.DefaultDisk.GetShape(index, null, _configuration.CurrentExecutableUnit?.SpaceName);
                        }
                    }
                }
                else if (param is MemoryAddress memoryAddress && memoryAddress.Index.HasValue)
                {
                    // Параметр из памяти
                    if (_memory.TryGetValue(memoryAddress.Index.Value, out var memoryValue) && memoryValue is Shape memShape)
                    {
                        shape = memShape;
                    }
                }
                else if (param is Shape directShape)
                {
                    shape = directShape;
                }

                if (shape != null)
                {
                    shapes.Add(shape);
                }
            }

            if (shapes.Count == 0)
            {
                throw new InvalidOperationException("Origin function requires at least one shape parameter.");
            }

            // Вычисляем центр массы из всех Position shapes
            var positions = new List<Position>();
            foreach (var shape in shapes)
            {
                if (shape.Position != null)
                {
                    positions.Add(shape.Position);
                }
                else if (shape.Origin != null)
                {
                    positions.Add(shape.Origin);
                }

                // Если у shape есть VertexIndices, загружаем вершины и добавляем их Positions
                if (shape.VertexIndices != null && shape.VertexIndices.Count > 0 && _configuration?.DefaultDisk != null)
                {
                    foreach (var vertexIndex in shape.VertexIndices)
                    {
                        var vertex = await _configuration.DefaultDisk.GetVertex(vertexIndex, null, _configuration.CurrentExecutableUnit?.SpaceName);
                        if (vertex?.Position != null)
                        {
                            positions.Add(vertex.Position);
                        }
                    }
                }
            }

            if (positions.Count == 0)
            {
                throw new InvalidOperationException("Origin function requires shapes with positions.");
            }

            // Вычисляем центр массы
            var centerOfMass = MathFunctions.CalculateCenterOfMass(positions);
            _stack.Add(centerOfMass);
        }

        private async Task ExecutePrintAsync(CallInfo callInfo)
        {
            await _printFunctions.ExecutePrintAsync(callInfo);
        }

        private async Task ExecuteIntersectAsync(CallInfo callInfo)
        {
            Shape? shapeA = null;
            Shape? shapeB = null;

            // Ищем shapeA и shapeB в параметрах
            if (callInfo.Parameters.TryGetValue("shapeA", out var paramA))
            {
                var valueA = await GetValueFromParameterAsync(paramA);
                shapeA = valueA as Shape;
            }
            else if (callInfo.Parameters.TryGetValue("0", out var param0))
            {
                var value0 = await GetValueFromParameterAsync(param0);
                shapeA = value0 as Shape;
            }

            if (callInfo.Parameters.TryGetValue("shapeB", out var paramB))
            {
                var valueB = await GetValueFromParameterAsync(paramB);
                shapeB = valueB as Shape;
            }
            else if (callInfo.Parameters.TryGetValue("1", out var param1))
            {
                var value1 = await GetValueFromParameterAsync(param1);
                shapeB = value1 as Shape;
            }

            if (shapeA == null || shapeB == null)
            {
                throw new InvalidOperationException("Intersect function requires two shape parameters (shapeA and shapeB).");
            }

            // Вычисляем пересечение
            var intersection = await MathFunctions.CalculateIntersectionAsync(shapeA, shapeB, _configuration?.DefaultDisk, _configuration?.CurrentExecutableUnit?.SpaceName);
            _stack.Add(intersection);
        }

        private async Task<object?> GetValueFromParameterAsync(object param)
        {
            if (param is Dictionary<string, object> entityRef)
            {
                // Параметр вида: { "type": EntityType.Shape, "index": 1 }
                if (entityRef.TryGetValue("type", out var typeObj) &&
                    entityRef.TryGetValue("index", out var indexObj) &&
                    typeObj is EntityType entityType &&
                    indexObj is long index)
                {
                    if (_configuration?.DefaultDisk != null)
                    {
                        var sn = _configuration.CurrentExecutableUnit?.SpaceName;
                        switch (entityType)
                        {
                            case EntityType.Vertex:
                                return await _configuration.DefaultDisk.GetVertex(index, null, sn);
                            case EntityType.Shape:
                                return await _configuration.DefaultDisk.GetShape(index, null, sn);
                            case EntityType.Relation:
                                return await _configuration.DefaultDisk.GetRelation(index, null, sn);
                        }
                    }
                }
            }
            else if (param is MemoryAddress memoryAddress && memoryAddress.Index.HasValue)
            {
                // Параметр из памяти
                if (_memory.TryGetValue(memoryAddress.Index.Value, out var memoryValue))
                {
                    return memoryValue;
                }
            }

            return param;
        }

    }
}

