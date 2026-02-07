using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel.Devices;
using Magic.Kernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Magic.Kernel.Functions
{
    public class PrintFunctions
    {
        private readonly KernelConfiguration? _configuration;
        private readonly List<object> _stack;
        private readonly Dictionary<long, object>? _memory;

        public PrintFunctions(KernelConfiguration? configuration, List<object> stack, Dictionary<long, object>? memory = null)
        {
            _configuration = configuration;
            _stack = stack;
            _memory = memory;
        }

        public async Task ExecutePrintAsync(CallInfo callInfo)
        {
            object? valueToPrint = null;

            // Ищем параметр с индексом [0] или первый параметр
            if (callInfo.Parameters.TryGetValue("0", out var param0))
            {
                valueToPrint = await GetValueFromParameterAsync(param0);
            }
            else if (callInfo.Parameters.Count > 0)
            {
                var firstParam = callInfo.Parameters.Values.First();
                valueToPrint = await GetValueFromParameterAsync(firstParam);
            }

            if (valueToPrint == null)
            {
                throw new InvalidOperationException("Print function requires a value parameter.");
            }

            // Проверяем тип вывода
            var outputType = "console";
            if (callInfo.Parameters.TryGetValue("type", out var typeParam) && typeParam is string typeStr)
            {
                outputType = typeStr.ToLower();
            }

            if (outputType == "infer")
            {
                // Вывод на inference device и получение результата
                if (_configuration?.DefaultInferenceDevice == null)
                {
                    throw new InvalidOperationException("DefaultInferenceDevice is not configured. Cannot print to inference device.");
                }

                var inputData = ConvertToEntityData(valueToPrint);
                var outputData = new EntityData
                {
                    Type = new HierarchicalDataType()
                };

                var result = await _configuration.DefaultInferenceDevice.Print(inputData, outputData);
                if (result != DeviceOperationResult.Success)
                {
                    throw new InvalidOperationException($"Failed to print to inference device: {result}");
                }

                // Результат inference помещаем в стек
                _stack.Add(outputData);
            }
            else
            {
                // Вывод в консоль с форматированием по типам
                var formattedOutput = FormatValue(valueToPrint);
                Console.WriteLine(formattedOutput);
            }
        }

        private string FormatValue(object value)
        {
            return value switch
            {
                Position pos => FormatPosition(pos),
                Shape shape => FormatShape(shape),
                Vertex vertex => FormatVertex(vertex),
                Relation relation => FormatRelation(relation),
                EntityBase entity => FormatEntityBase(entity),
                List<object> list => FormatList(list),
                Dictionary<string, object> dict => FormatDictionary(dict),
                null => "None",
                _ => value.ToString() ?? "null"
            };
        }

        private string FormatPosition(Position pos)
        {
            if (pos.Dimensions == null || pos.Dimensions.Count == 0)
            {
                return "Position([])";
            }
            return $"Position([{string.Join(", ", pos.Dimensions.Select(d => d.ToString("G9")))}])";
        }

        private string FormatShape(Shape shape)
        {
            var parts = new List<string>();
            
            if (!string.IsNullOrEmpty(shape.Name))
            {
                parts.Add($"Name='{shape.Name}'");
            }
            
            if (shape.Origin != null)
            {
                parts.Add($"Origin={FormatPosition(shape.Origin)}");
            }
            
            if (shape.Position != null)
            {
                parts.Add($"Position={FormatPosition(shape.Position)}");
            }
            
            if (shape.Index.HasValue)
            {
                parts.Add($"Index={shape.Index.Value}");
            }
            
            if (shape.Vertices != null && shape.Vertices.Count > 0)
            {
                parts.Add($"Vertices=[{string.Join(", ", shape.Vertices.Select(v => FormatVertex(v)))}]");
            }
            
            if (shape.VertexIndices != null && shape.VertexIndices.Count > 0)
            {
                parts.Add($"VertexIndices=[{string.Join(", ", shape.VertexIndices)}]");
            }

            return $"Shape({string.Join(", ", parts)})";
        }

        private string FormatVertex(Vertex vertex)
        {
            var parts = new List<string>();
            
            if (vertex.Index.HasValue)
            {
                parts.Add($"Index={vertex.Index.Value}");
            }
            
            if (!string.IsNullOrEmpty(vertex.Name))
            {
                parts.Add($"Name='{vertex.Name}'");
            }
            
            if (vertex.Position != null)
            {
                parts.Add($"Position={FormatPosition(vertex.Position)}");
            }
            
            if (vertex.Weight.HasValue)
            {
                parts.Add($"Weight={vertex.Weight.Value}");
            }

            return $"Vertex({string.Join(", ", parts)})";
        }

        private string FormatRelation(Relation relation)
        {
            var parts = new List<string>();
            
            if (relation.Index.HasValue)
            {
                parts.Add($"Index={relation.Index.Value}");
            }
            
            if (!string.IsNullOrEmpty(relation.Name))
            {
                parts.Add($"Name='{relation.Name}'");
            }

            return $"Relation({string.Join(", ", parts)})";
        }

        private string FormatEntityBase(EntityBase entity)
        {
            var parts = new List<string>();
            
            parts.Add($"Type={entity.Type}");
            
            if (entity.Index.HasValue)
            {
                parts.Add($"Index={entity.Index.Value}");
            }
            
            if (!string.IsNullOrEmpty(entity.Name))
            {
                parts.Add($"Name='{entity.Name}'");
            }
            
            if (entity.Position != null)
            {
                parts.Add($"Position={FormatPosition(entity.Position)}");
            }
            
            if (entity.Weight.HasValue)
            {
                parts.Add($"Weight={entity.Weight.Value}");
            }

            return $"{entity.Type}({string.Join(", ", parts)})";
        }

        private string FormatList(List<object> list)
        {
            if (list.Count == 0)
            {
                return "[]";
            }
            return $"[{string.Join(", ", list.Select(FormatValue))}]";
        }

        private string FormatDictionary(Dictionary<string, object> dict)
        {
            if (dict.Count == 0)
            {
                return "{}";
            }
            var items = dict.Select(kvp => $"'{kvp.Key}': {FormatValue(kvp.Value)}");
            return $"{{{string.Join(", ", items)}}}";
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
                        switch (entityType)
                        {
                            case EntityType.Vertex:
                                return await _configuration.DefaultDisk.GetVertex(index, null);
                            case EntityType.Shape:
                                return await _configuration.DefaultDisk.GetShape(index, null);
                            case EntityType.Relation:
                                return await _configuration.DefaultDisk.GetRelation(index, null);
                        }
                    }
                }
            }
            else if (param is MemoryAddress memoryAddress && memoryAddress.Index.HasValue)
            {
                // Параметр из памяти
                if (_memory != null && _memory.TryGetValue(memoryAddress.Index.Value, out var memoryValue))
                {
                    return memoryValue;
                }
                return param;
            }

            return param;
        }

        private EntityData ConvertToEntityData(object value)
        {
            // Простое преобразование в EntityData
            // В реальной реализации может потребоваться сериализация
            var entityData = new EntityData
            {
                Type = new HierarchicalDataType()
            };

            if (value is string str)
            {
                entityData.Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(str));
            }
            else
            {
                entityData.Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value.ToString() ?? ""));
            }

            return entityData;
        }
    }
}
