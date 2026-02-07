using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel;

namespace Magic.Kernel.Compilation
{
    public class SemanticAnalyzer
    {
        public Command Analyze(InstructionNode instruction)
        {
            var command = new Command();

            // Преобразуем opcode
            command.Opcode = MapOpcode(instruction.Opcode);

            // Преобразуем параметры в операнды
            if (command.Opcode == Opcodes.AddVertex)
            {
                var vertex = BuildVertex(instruction.Parameters);
                command.Operand1 = vertex;
            }
            else if (command.Opcode == Opcodes.AddRelation)
            {
                var relation = BuildRelation(instruction.Parameters);
                command.Operand1 = relation;
            }
            else if (command.Opcode == Opcodes.AddShape)
            {
                var shape = BuildShape(instruction.Parameters);
                command.Operand1 = shape;
            }
            else if (command.Opcode == Opcodes.Call)
            {
                var callInfo = BuildCallInfo(instruction.Parameters);
                command.Operand1 = callInfo;
            }
            else if (command.Opcode == Opcodes.Pop || command.Opcode == Opcodes.Push)
            {
                var memoryAddress = BuildMemoryAddress(instruction.Parameters);
                command.Operand1 = memoryAddress;
            }

            return command;
        }

        private Opcodes MapOpcode(string opcode)
        {
            return opcode.ToLower() switch
            {
                "addvertex" => Opcodes.AddVertex,
                "addrelation" => Opcodes.AddRelation,
                "addshape" => Opcodes.AddShape,
                "call" => Opcodes.Call,
                "push" => Opcodes.Push,
                "pop" => Opcodes.Pop,
                "syscall" => Opcodes.SysCall,
                "ret" => Opcodes.Ret,
                "move" => Opcodes.Move,
                "getvertex" => Opcodes.GetVertex,
                _ => Opcodes.Nop
            };
        }

        private Vertex BuildVertex(List<ParameterNode> parameters)
        {
            var vertex = new Vertex();

            foreach (var param in parameters)
            {
                switch (param)
                {
                    case IndexParameterNode indexParam:
                        vertex.Index = indexParam.Value;
                        break;

                    case DimensionsParameterNode dimensionsParam:
                        vertex.Position = new Position { Dimensions = dimensionsParam.Values };
                        break;

                    case WeightParameterNode weightParam:
                        vertex.Weight = weightParam.Value;
                        break;

                    case DataParameterNode dataParam:
                        var dataTypes = new List<DataType>();
                        
                        // Допустимые типы данных (строго)
                        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text", "binary", "base64" };
                        
                        // Проверяем формат без двоеточия (недопустимый формат)
                        // Требование: `data: text "V1"` должно давать ошибку (нет `:` после типа), и "V1" не должен теряться.
                        if (!dataParam.HasColon && !string.IsNullOrEmpty(dataParam.Type) && !string.IsNullOrEmpty(dataParam.Value))
                        {
                            var errorString = !string.IsNullOrEmpty(dataParam.OriginalString) 
                                ? dataParam.OriginalString 
                                : $"{dataParam.Type} \"{dataParam.Value}\"";
                            throw new InvalidOperationException($"недопустимый тип данных: {errorString}");
                        }
                        
                        // Если есть список типов (для вложенных типов типа binary: base64)
                        if (dataParam.Types != null && dataParam.Types.Count > 0)
                        {
                            foreach (var typeStr in dataParam.Types)
                            {
                                var normalizedType = typeStr.ToLower();
                                if (!allowedTypes.Contains(normalizedType))
                                {
                                    // Используем OriginalString если есть, иначе формируем строку
                                    var typeValueString = !string.IsNullOrEmpty(dataParam.OriginalString) 
                                        ? dataParam.OriginalString 
                                        : string.Join(" ", dataParam.Types) + (string.IsNullOrEmpty(dataParam.Value) ? "" : $" \"{dataParam.Value}\"");
                                    throw new InvalidOperationException($"недопустимый тип данных: {typeValueString}");
                                }
                                var mappedType = MapDataType(typeStr);
                                dataTypes.Add(mappedType);
                            }
                        }
                        else
                        {
                            // Обратная совместимость: используем Type если Types пуст
                            var normalizedType = dataParam.Type.ToLower();
                            if (!string.IsNullOrEmpty(dataParam.Type) && !allowedTypes.Contains(normalizedType))
                            {
                                // Используем OriginalString если есть, иначе формируем строку
                                var typeValueString = !string.IsNullOrEmpty(dataParam.OriginalString) 
                                    ? dataParam.OriginalString 
                                    : dataParam.Type + (string.IsNullOrEmpty(dataParam.Value) ? "" : $" \"{dataParam.Value}\"");
                                throw new InvalidOperationException($"недопустимый тип данных: {typeValueString}");
                            }
                            var dataType = MapDataType(dataParam.Type);
                            dataTypes.Add(dataType);
                        }
                        
                        vertex.Data = new EntityData
                        {
                            Type = new HierarchicalDataType
                            {
                                Types = dataTypes
                            },
                            Data = dataParam.Value
                        };
                        break;
                }
            }

            return vertex;
        }

        private DataType MapDataType(string type)
        {
            return type.ToLower() switch
            {
                "text" => DataType.Text,
                "binary" => DataType.Binary,
                "json" => DataType.Json,
                "base64" => DataType.Base64,
                "string" => DataType.String,
                "integer" => DataType.Integer,
                "float" => DataType.Float,
                "date" => DataType.Date,
                "datetime" => DataType.DateTime,
                "boolean" => DataType.Boolean,
                "time" => DataType.Time,
                "array" => DataType.Array,
                "image" => DataType.Image,
                _ => DataType.Text
            };
        }

        private Relation BuildRelation(List<ParameterNode> parameters)
        {
            var relation = new Relation();

            foreach (var param in parameters)
            {
                switch (param)
                {
                    case IndexParameterNode indexParam:
                        relation.Index = indexParam.Value;
                        break;

                    case FromParameterNode fromParam:
                        relation.FromIndex = fromParam.Index;
                        relation.FromType = MapEntityType(fromParam.EntityType);
                        break;

                    case ToParameterNode toParam:
                        relation.ToIndex = toParam.Index;
                        relation.ToType = MapEntityType(toParam.EntityType);
                        break;

                    case WeightParameterNode weightParam:
                        relation.Weight = weightParam.Value;
                        break;
                }
            }

            return relation;
        }

        private EntityType MapEntityType(string type)
        {
            return type.ToLower() switch
            {
                "vertex" => EntityType.Vertex,
                "relation" => EntityType.Relation,
                "shape" => EntityType.Shape,
                _ => EntityType.Undefined
            };
        }

        private Shape BuildShape(List<ParameterNode> parameters)
        {
            var shape = new Shape();

            foreach (var param in parameters)
            {
                switch (param)
                {
                    case IndexParameterNode indexParam:
                        shape.Index = indexParam.Value;
                        break;

                    case VerticesParameterNode verticesParam:
                        shape.VertexIndices = verticesParam.Indices;
                        break;

                    case WeightParameterNode weightParam:
                        shape.Weight = weightParam.Value;
                        break;

                    case DimensionsParameterNode dimensionsParam:
                        shape.Origin = new Position { Dimensions = dimensionsParam.Values };
                        break;
                }
            }

            return shape;
        }

        private CallInfo BuildCallInfo(List<ParameterNode> parameters)
        {
            var callInfo = new CallInfo();
            var callParams = new Dictionary<string, object>();

            foreach (var param in parameters)
            {
                switch (param)
                {
                    case FunctionNameParameterNode nameParam:
                        callInfo.FunctionName = nameParam.FunctionName;
                        break;

                    case FunctionParameterNode funcParam:
                        var paramName = !string.IsNullOrWhiteSpace(funcParam.ParameterName) ? funcParam.ParameterName : funcParam.Name;
                        
                        // Проверяем, является ли параметр memory address
                        if (funcParam.EntityType == "memory")
                        {
                            // Создаем memory address для параметра
                            var memoryAddress = new MemoryAddress { Index = funcParam.Index };
                            callParams[paramName] = memoryAddress;
                        }
                        else
                        {
                            // Создаем entity reference для параметра
                            var entityType = MapEntityType(funcParam.EntityType);
                            var entityRef = new Dictionary<string, object>
                            {
                                { "type", entityType },
                                { "index", funcParam.Index }
                            };
                            callParams[paramName] = entityRef;
                        }
                        break;

                    case ComplexValueParameterNode complexParam:
                        var complexParamName = !string.IsNullOrWhiteSpace(complexParam.ParameterName) ? complexParam.ParameterName : complexParam.Name;
                        // Поддержка inline entity literals в call, например:
                        // shapeB: shape: { vertices: [ { dimensions: [...] }, ... ] }
                        if (complexParam.Value is Dictionary<string, object> dict)
                        {
                            // 1) Обертка вида { "shape": { ... } }
                            if (dict.TryGetValue("shape", out var shapeObj) && shapeObj is Dictionary<string, object> shapeDict)
                            {
                                callParams[complexParamName] = BuildShapeFromComplexValue(shapeDict);
                                break;
                            }

                            // 2) Если это уже shape-подобный объект без обертки
                            if (dict.ContainsKey("vertices") || dict.ContainsKey("dimensions") || dict.ContainsKey("origin"))
                            {
                                callParams[complexParamName] = BuildShapeFromComplexValue(dict);
                                break;
                            }
                        }

                        callParams[complexParamName] = complexParam.Value;
                        break;
                }
            }

            callInfo.Parameters = callParams;
            return callInfo;
        }

        private Shape BuildShapeFromComplexValue(Dictionary<string, object> shapeDict)
        {
            var shape = new Shape();

            if (shapeDict.TryGetValue("index", out var indexObj))
            {
                if (indexObj is long l) shape.Index = l;
                else if (indexObj is int i) shape.Index = i;
                else if (indexObj is double d) shape.Index = (long)d;
            }

            if (shapeDict.TryGetValue("weight", out var weightObj))
            {
                if (weightObj is double wd) shape.Weight = (float)wd;
                else if (weightObj is float wf) shape.Weight = wf;
                else if (weightObj is long wl) shape.Weight = wl;
            }

            // origin/dimensions как Origin
            if (shapeDict.TryGetValue("dimensions", out var dimsObj))
            {
                var dims = ConvertToFloatList(dimsObj);
                if (dims.Count > 0)
                {
                    shape.Origin = new Position { Dimensions = dims };
                }
            }
            else if (shapeDict.TryGetValue("origin", out var originObj) && originObj is Dictionary<string, object> originDict)
            {
                if (originDict.TryGetValue("dimensions", out var originDimsObj))
                {
                    var dims = ConvertToFloatList(originDimsObj);
                    if (dims.Count > 0)
                    {
                        shape.Origin = new Position { Dimensions = dims };
                    }
                }
            }

            // vertices: [ { dimensions: [...] }, ... ]
            if (shapeDict.TryGetValue("vertices", out var verticesObj) && verticesObj is List<object> verticesList)
            {
                var vertices = new List<Vertex>();
                foreach (var v in verticesList)
                {
                    if (v is Dictionary<string, object> vDict)
                    {
                        var vertex = new Vertex();
                        if (vDict.TryGetValue("dimensions", out var vDimsObj))
                        {
                            var dims = ConvertToFloatList(vDimsObj);
                            if (dims.Count > 0)
                            {
                                vertex.Position = new Position { Dimensions = dims };
                            }
                        }
                        vertices.Add(vertex);
                    }
                }

                if (vertices.Count > 0)
                {
                    shape.Vertices = vertices;
                }
            }

            return shape;
        }

        private List<float> ConvertToFloatList(object obj)
        {
            var result = new List<float>();
            if (obj is List<object> list)
            {
                foreach (var item in list)
                {
                    switch (item)
                    {
                        case double d:
                            result.Add((float)d);
                            break;
                        case float f:
                            result.Add(f);
                            break;
                        case long l:
                            result.Add(l);
                            break;
                        case int i:
                            result.Add(i);
                            break;
                        case string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sd):
                            result.Add((float)sd);
                            break;
                    }
                }
            }
            return result;
        }

        private MemoryAddress BuildMemoryAddress(List<ParameterNode> parameters)
        {
            var memoryAddress = new MemoryAddress();

            foreach (var param in parameters)
            {
                if (param is MemoryParameterNode memoryParam)
                {
                    memoryAddress.Index = memoryParam.Index;
                    break;
                }
            }

            return memoryAddress;
        }
    }
}
