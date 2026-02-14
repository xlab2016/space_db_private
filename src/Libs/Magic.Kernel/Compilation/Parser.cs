using Magic.Kernel.Compilation.Ast;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Magic.Kernel.Compilation
{
    public class Parser
    {
        public InstructionNode Parse(string source)
        {
            source = source.Trim();
            var instruction = new InstructionNode();

            // Извлекаем opcode (до первого пробела)
            var spaceIndex = source.IndexOf(' ');
            if (spaceIndex == -1)
            {
                instruction.Opcode = source;
                return instruction;
            }

            instruction.Opcode = source.Substring(0, spaceIndex).ToLower();
            var parametersPart = source.Substring(spaceIndex + 1).Trim();

            // Специальная обработка для call - имя функции в кавычках
            if (instruction.Opcode == "call")
            {
                var parameters = ParseCallParameters(parametersPart);
                instruction.Parameters = parameters;
            }
            else if (instruction.Opcode == "pop" || instruction.Opcode == "push")
            {
                // push: [0] | stream | file | 1 | string: "..."
                var parameters = instruction.Opcode == "push"
                    ? ParsePushParameters(parametersPart)
                    : ParseMemoryParameters(parametersPart);
                instruction.Parameters = parameters;
            }
            else if (instruction.Opcode == "def" || instruction.Opcode == "awaitobj")
            {
                instruction.Parameters = new List<ParameterNode>();
            }
            else if (instruction.Opcode == "defgen")
            {
                instruction.Parameters = new List<ParameterNode>();
            }
            else if (instruction.Opcode == "callobj")
            {
                // callobj "open" — имя метода в кавычках
                var name = parametersPart.Trim();
                if (name.Length >= 2 && name.StartsWith("\"") && name.EndsWith("\""))
                    name = name.Substring(1, name.Length - 2).Replace("\\\"", "\"");
                instruction.Parameters = new List<ParameterNode> { new FunctionNameParameterNode { FunctionName = name } };
            }
            else
            {
            // Парсим параметры: name: value, name: value, ...
            var parameters = ParseParameters(parametersPart);
            instruction.Parameters = parameters;
            }

            return instruction;
        }

        private List<ParameterNode> ParseParameters(string parametersPart)
        {
            var parameters = new List<ParameterNode>();
            var i = 0;

            while (i < parametersPart.Length)
            {
                // Пропускаем пробелы и запятые
                while (i < parametersPart.Length && (char.IsWhiteSpace(parametersPart[i]) || parametersPart[i] == ','))
                {
                    i++;
                }

                if (i >= parametersPart.Length) break;

                // Находим имя параметра (до :)
                var colonIndex = parametersPart.IndexOf(':', i);
                if (colonIndex == -1) break;

                var name = parametersPart.Substring(i, colonIndex - i).Trim().ToLower();
                i = colonIndex + 1;

                // Пропускаем пробелы после :
                while (i < parametersPart.Length && char.IsWhiteSpace(parametersPart[i]))
                {
                    i++;
                }

                if (i >= parametersPart.Length) break;

                ParameterNode? parameter = null;

                // Парсим значение в зависимости от типа
                if (name == "index")
                {
                    var value = ParseNumber(parametersPart, ref i);
                    parameter = new IndexParameterNode { Name = name, Value = (long)value };
                }
                else if (name == "dimensions")
                {
                    var values = ParseArray(parametersPart, ref i);
                    parameter = new DimensionsParameterNode { Name = name, Values = values };
                }
                else if (name == "weight")
                {
                    var value = ParseFloat(parametersPart, ref i);
                    parameter = new WeightParameterNode { Name = name, Value = value };
                }
                else if (name == "data")
                {
                    var (type, value, hasColon) = ParseData(parametersPart, ref i);
                    var dataParam = new DataParameterNode { Name = name, Type = type, Value = value };
                    
                    // Сохраняем информацию о наличии двоеточия
                    dataParam.HasColon = hasColon;
                    
                    // Парсим вложенные типы из строки типа (например "binary:base64" -> ["binary", "base64"])
                    if (!string.IsNullOrEmpty(type))
                    {
                        var typeParts = type.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var typePart in typeParts)
                        {
                            if (!string.IsNullOrEmpty(typePart))
                            {
                                dataParam.Types.Add(typePart.ToLower());
                            }
                        }
                    }
                    
                    // Сохраняем исходную строку для ошибок (тип + значение)
                    if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(value))
                    {
                        dataParam.OriginalString = hasColon ? $"{type} \"{value}\"" : $"{type} {value}";
                    }
                    else
                    {
                        dataParam.OriginalString = !string.IsNullOrEmpty(type) ? type : "";
                    }
                    
                    parameter = dataParam;
                }
                else if (name == "from")
                {
                    var (entityType, index) = ParseEntityReference(parametersPart, ref i);
                    parameter = new FromParameterNode { Name = name, EntityType = entityType, Index = index };
                }
                else if (name == "to")
                {
                    var (entityType, index) = ParseEntityReference(parametersPart, ref i);
                    parameter = new ToParameterNode { Name = name, EntityType = entityType, Index = index };
                }
                else if (name == "vertices")
                {
                    var indices = ParseVertices(parametersPart, ref i);
                    parameter = new VerticesParameterNode { Name = name, Indices = indices };
                }

                if (parameter != null)
                {
                    parameters.Add(parameter);
                }

                // Ищем следующую запятую
                while (i < parametersPart.Length && parametersPart[i] != ',')
                {
                    i++;
                }
            }

            return parameters;
        }

        private long ParseNumber(string source, ref int index)
        {
            var start = index;
            while (index < source.Length && (char.IsDigit(source[index]) || source[index] == '-'))
            {
                index++;
            }
            var numberStr = source.Substring(start, index - start);
            return long.Parse(numberStr);
        }

        private float ParseFloat(string source, ref int index)
        {
            var start = index;
            while (index < source.Length && (char.IsDigit(source[index]) || source[index] == '.' || source[index] == '-' || source[index] == 'e' || source[index] == 'E' || source[index] == '+' || source[index] == '-'))
            {
                index++;
            }
            var numberStr = source.Substring(start, index - start);
            return float.Parse(numberStr, CultureInfo.InvariantCulture);
        }

        private List<float> ParseArray(string source, ref int index)
        {
            var values = new List<float>();

            // Пропускаем [
            while (index < source.Length && source[index] != '[')
            {
                index++;
            }
            if (index >= source.Length) return values;
            index++; // пропускаем [

            while (index < source.Length && source[index] != ']')
            {
                // Пропускаем пробелы и запятые
                while (index < source.Length && (char.IsWhiteSpace(source[index]) || source[index] == ','))
                {
                    index++;
                }

                if (index >= source.Length || source[index] == ']') break;

                var value = ParseFloat(source, ref index);
                values.Add(value);

                // Пропускаем пробелы и запятые
                while (index < source.Length && (char.IsWhiteSpace(source[index]) || source[index] == ','))
                {
                    index++;
                }
            }

            if (index < source.Length && source[index] == ']')
            {
                index++; // пропускаем ]
            }

            return values;
        }

        private (string type, string value, bool hasColon) ParseData(string source, ref int index)
        {
            // Формат: text: "V1" или binary: base64: "sdsfdsgfg==" или text "V1" (без двоеточия - недопустимо)
            var types = new List<string>();
            var sawColon = false;

            // Парсим цепочку типов: type1: type2: ... : "value"
            while (index < source.Length)
            {
                // Ищем следующий : или "
            var colonIndex = source.IndexOf(':', index);
                var quoteIndex = source.IndexOf('"', index);

                // Если нашли кавычку раньше двоеточия, значит это начало значения (формат без двоеточия)
                if (quoteIndex != -1 && (colonIndex == -1 || quoteIndex < colonIndex))
                {
                    // Если мы еще не видели двоеточие, то это формат `text "V1"` (недопустимо по требованиям)
                    if (!sawColon)
                    {
                        var typeBeforeQuote = source.Substring(index, quoteIndex - index).Trim();
                        if (!string.IsNullOrEmpty(typeBeforeQuote))
                        {
                            types.Add(typeBeforeQuote.ToLower());
                        }
                        // Сдвигаем индекс на кавычку, чтобы корректно распарсить значение и сохранить его для ошибки
                        index = quoteIndex;
                    }
                    break;
                }

                // Спец-кейс: `text V2` (без двоеточия и без кавычек) — тоже недопустимо, но значение не должно теряться
                if (quoteIndex == -1 && colonIndex == -1)
                {
                    // type = до пробела/запятой/;
                    var startType = index;
                    while (index < source.Length && !char.IsWhiteSpace(source[index]) && source[index] != ',' && source[index] != ';')
                    {
                        index++;
                    }
                    var typeToken = source.Substring(startType, index - startType).Trim();
                    if (!string.IsNullOrEmpty(typeToken))
                    {
                        types.Add(typeToken.ToLower());
                    }

                    // пропускаем пробелы
                    while (index < source.Length && char.IsWhiteSpace(source[index]))
                    {
                        index++;
                    }

                    // value = до , или ;
                    var startVal = index;
                    while (index < source.Length && source[index] != ',' && source[index] != ';')
                    {
                        index++;
                    }
                    var valueToken = source.Substring(startVal, index - startVal).Trim();

                    var mainTypeToken = types.Count > 0 ? types[types.Count - 1] : "";
                    if (types.Count > 0) types.RemoveAt(types.Count - 1);
                    var typeStringToken = types.Count > 0 ? string.Join(":", types) + ":" + mainTypeToken : mainTypeToken;

                    return (typeStringToken, valueToken, false);
                }

                // Если не нашли двоеточие, выходим
            if (colonIndex == -1)
                {
                    break;
                }

                sawColon = true; // Отмечаем, что двоеточие было

                // Извлекаем тип
                var type = source.Substring(index, colonIndex - index).Trim().ToLower();
                if (!string.IsNullOrEmpty(type))
                {
                    types.Add(type);
                }

                index = colonIndex + 1;

                // Пропускаем пробелы после :
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                {
                    index++;
                }
            }

            // Если типов нет, возвращаем пустую строку
            if (types.Count == 0)
            {
                return ("", "", false);
            }

            // Последний тип - основной
            var mainType = types[types.Count - 1];
            types.RemoveAt(types.Count - 1);

            // Пропускаем пробелы перед значением
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            // Парсим строку в кавычках
            string value = "";
            if (index < source.Length && source[index] == '"')
            {
                index++; // пропускаем открывающую кавычку
                var start = index;
                while (index < source.Length && source[index] != '"')
                {
                    if (source[index] == '\\' && index + 1 < source.Length)
                    {
                        index += 2; // пропускаем экранированный символ
                    }
                    else
                    {
                        index++;
                    }
                }
                value = source.Substring(start, index - start);
                if (index < source.Length && source[index] == '"')
                {
                    index++; // пропускаем закрывающую кавычку
                }
            }

            // Формируем строку типов для обратной совместимости
            // Если есть вложенные типы, объединяем их через :
            var typeString = types.Count > 0 
                ? string.Join(":", types) + ":" + mainType 
                : mainType;

            // sawColon=false означает формат `text "V1"` (без двоеточия после типа)
            return (typeString, value, sawColon);
        }

        private (string entityType, long index) ParseEntityReference(string source, ref int index)
        {
            // Формат: vertex: index: 1 или relation: index: 1
            // Пропускаем пробелы
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            if (index >= source.Length)
            {
                return ("", 0);
            }

            // Ищем первый тип сущности (до :)
            var colonIndex = source.IndexOf(':', index);
            if (colonIndex == -1)
            {
                return ("", 0);
            }

            var entityType = source.Substring(index, colonIndex - index).Trim().ToLower();
            index = colonIndex + 1;

            // Пропускаем пробелы
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            // Ищем "index:"
            var indexKeyword = "index:";
            if (index + indexKeyword.Length <= source.Length && 
                source.Substring(index, indexKeyword.Length).Equals(indexKeyword, StringComparison.OrdinalIgnoreCase))
            {
                index += indexKeyword.Length;
                
                // Пропускаем пробелы после "index:"
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                {
                    index++;
                }
            }

            // Проверяем, что есть что парсить
            if (index >= source.Length || (!char.IsDigit(source[index]) && source[index] != '-'))
            {
                return ("", 0);
            }

            // Парсим число
            var numberValue = ParseNumber(source, ref index);

            return (entityType, numberValue);
        }

        private List<long> ParseVertices(string source, ref int index)
        {
            // Формат: vertices: indices: [1, 2, 3]
            var indices = new List<long>();

            // Пропускаем пробелы
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            // Ищем "indices:"
            var indicesKeyword = "indices:";
            if (index + indicesKeyword.Length <= source.Length && 
                source.Substring(index, indicesKeyword.Length).Equals(indicesKeyword, StringComparison.OrdinalIgnoreCase))
            {
                index += indicesKeyword.Length;
                
                // Пропускаем пробелы после "indices:"
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                {
                    index++;
                }
            }

            // Пропускаем до [
            while (index < source.Length && source[index] != '[')
            {
                index++;
            }
            if (index >= source.Length) return indices;
            index++; // пропускаем [

            while (index < source.Length && source[index] != ']')
            {
                // Пропускаем пробелы и запятые
                while (index < source.Length && (char.IsWhiteSpace(source[index]) || source[index] == ','))
                {
                    index++;
                }

                if (index >= source.Length || source[index] == ']') break;

                var value = ParseNumber(source, ref index);
                indices.Add(value);

                // Пропускаем пробелы и запятые
                while (index < source.Length && (char.IsWhiteSpace(source[index]) || source[index] == ','))
                {
                    index++;
                }
            }

            if (index < source.Length && source[index] == ']')
            {
                index++; // пропускаем ]
            }

            return indices;
        }

        private List<ParameterNode> ParseCallParameters(string parametersPart)
        {
            // Формат: "origin", shape: index: 1
            var parameters = new List<ParameterNode>();
            var i = 0;

            // Пропускаем пробелы
            while (i < parametersPart.Length && char.IsWhiteSpace(parametersPart[i]))
            {
                i++;
            }

            // Парсим имя функции:
            // 1) "origin" — строка в кавычках
            // 2) Main — идентификатор без кавычек (для вызова процедур/функций пользователя)
            if (i < parametersPart.Length)
            {
                string? functionName = null;

                if (parametersPart[i] == '"')
                {
                    i++; // пропускаем открывающую кавычку
                    var start = i;
                    while (i < parametersPart.Length && parametersPart[i] != '"')
                    {
                        if (parametersPart[i] == '\\' && i + 1 < parametersPart.Length)
                        {
                            i += 2; // пропускаем экранированный символ
                        }
                        else
                        {
                            i++;
                        }
                    }
                    functionName = parametersPart.Substring(start, i - start);
                    if (i < parametersPart.Length && parametersPart[i] == '"')
                    {
                        i++; // пропускаем закрывающую кавычку
                    }
                }
                else
                {
                    // Идентификатор: до пробела/запятой
                    var start = i;
                    while (i < parametersPart.Length && !char.IsWhiteSpace(parametersPart[i]) && parametersPart[i] != ',')
                    {
                        i++;
                    }
                    functionName = parametersPart.Substring(start, i - start).Trim();
                }

                if (!string.IsNullOrWhiteSpace(functionName))
                {
                    parameters.Add(new FunctionNameParameterNode { Name = "function", FunctionName = functionName });
                }

                // Пропускаем пробелы и запятую после имени функции
                while (i < parametersPart.Length && (char.IsWhiteSpace(parametersPart[i]) || parametersPart[i] == ','))
                {
                    i++;
                }
            }

            // Парсим остальные параметры (entity references или memory addresses)
            // Формат: shape: index: 1 или [0]
            // Используем стандартный парсинг параметров, но с обработкой entity references и memory addresses
            var remainingPart = parametersPart.Substring(i).Trim();
            if (!string.IsNullOrEmpty(remainingPart))
            {
                // Парсим как обычные параметры, но с особым форматом
                var paramStart = 0;
                while (paramStart < remainingPart.Length)
                {
                    // Пропускаем пробелы и запятые
                    while (paramStart < remainingPart.Length && (char.IsWhiteSpace(remainingPart[paramStart]) || remainingPart[paramStart] == ','))
                    {
                        paramStart++;
                    }

                    if (paramStart >= remainingPart.Length) break;

                    // Проверяем, является ли параметр memory address [0]
                    if (paramStart < remainingPart.Length && remainingPart[paramStart] == '[')
                    {
                        // Парсим memory address
                        var memoryStart = paramStart;
                        paramStart++; // пропускаем [
                        
                        // Пропускаем пробелы
                        while (paramStart < remainingPart.Length && char.IsWhiteSpace(remainingPart[paramStart]))
                        {
                            paramStart++;
                        }

                        if (paramStart >= remainingPart.Length) break;

                        // Парсим число
                        var numberStart = paramStart;
                        while (paramStart < remainingPart.Length && char.IsDigit(remainingPart[paramStart]))
                        {
                            paramStart++;
                        }

                        if (paramStart > numberStart)
                        {
                            var addressStr = remainingPart.Substring(numberStart, paramStart - numberStart);
                            if (long.TryParse(addressStr, out var address))
                            {
                                // Пропускаем пробелы
                                while (paramStart < remainingPart.Length && char.IsWhiteSpace(remainingPart[paramStart]))
                                {
                                    paramStart++;
                                }

                                // Ищем закрывающую скобку ]
                                if (paramStart < remainingPart.Length && remainingPart[paramStart] == ']')
                                {
                                    paramStart++; // пропускаем ]
                                    
                                    // Добавляем memory address как параметр функции
                                    parameters.Add(new FunctionParameterNode 
                                    { 
                                        Name = "memory",
                                        ParameterName = "memory",
                                        EntityType = "memory", 
                                        Index = address 
                                    });
                                    
                                    // Продолжаем с следующего параметра
                                    continue;
                                }
                            }
                        }
                        // Если не удалось распарсить, возвращаемся к обычному парсингу
                        paramStart = memoryStart;
                    }

                    // Парсим имя параметра (может быть именованным или обычным)
                    // Формат: name: entityType: index: value или name: entityType: { ... }
                    var colonIndex = remainingPart.IndexOf(':', paramStart);
                    if (colonIndex == -1 || colonIndex == paramStart) break;

                    var paramName = remainingPart.Substring(paramStart, colonIndex - paramStart).Trim();
                    if (string.IsNullOrEmpty(paramName)) break;

                    var paramValueStart = colonIndex + 1;
                    
                    // Пропускаем пробелы после :
                    while (paramValueStart < remainingPart.Length && char.IsWhiteSpace(remainingPart[paramValueStart]))
                    {
                        paramValueStart++;
                    }

                    if (paramValueStart >= remainingPart.Length) break;

                    // Проверяем, является ли значение строковым литералом " ... "
                    if (paramValueStart < remainingPart.Length && remainingPart[paramValueStart] == '"')
                    {
                        var strStart = paramValueStart + 1;
                        var strEnd = strStart;
                        while (strEnd < remainingPart.Length && remainingPart[strEnd] != '"')
                        {
                            if (remainingPart[strEnd] == '\\' && strEnd + 1 < remainingPart.Length)
                                strEnd += 2;
                            else
                                strEnd++;
                        }
                        var strValue = strEnd > strStart ? remainingPart.Substring(strStart, strEnd - strStart) : "";
                        if (strEnd < remainingPart.Length) strEnd++;
                        parameters.Add(new StringParameterNode { Name = paramName, Value = strValue });
                        paramStart = strEnd;
                        continue;
                    }

                    // Проверяем, является ли значение вложенной структурой { ... }
                    if (paramValueStart < remainingPart.Length && remainingPart[paramValueStart] == '{')
                    {
                        // Парсим вложенную структуру
                        var (complexValue, consumed) = ParseComplexValue(remainingPart, paramValueStart);
                        if (complexValue != null)
                        {
                            parameters.Add(new ComplexValueParameterNode 
                            { 
                                Name = paramName,
                                ParameterName = paramName,
                                Value = complexValue 
                            });
                            
                            // Переходим к следующему параметру
                            paramStart = paramValueStart + consumed;
                            
                            // Ищем следующую запятую или конец
                            while (paramStart < remainingPart.Length && remainingPart[paramStart] != ',')
                            {
                                paramStart++;
                            }
                            continue;
                        }
                    }

                    // Парсим entity reference
                    // Формат может быть:
                    // 1. entityType: index: value (например, shape: index: 1)
                    // 2. entityType: { ... } (например, shape: { vertices: [...] })
                    // 3. index: value (старый формат для обратной совместимости)
                    var paramValuePart = remainingPart.Substring(paramValueStart);
                    var paramValueIndex = 0;
                    var entityType = string.Empty;
                    
                    // Пропускаем пробелы
                    while (paramValueIndex < paramValuePart.Length && char.IsWhiteSpace(paramValuePart[paramValueIndex]))
                    {
                        paramValueIndex++;
                    }
                    
                    // Пытаемся найти entityType в значении (например, "shape: index: 1" или "shape: { ... }")
                    var firstColonIndex = paramValuePart.IndexOf(':', paramValueIndex);
                    if (firstColonIndex > 0 && firstColonIndex < paramValuePart.Length - 1)
                    {
                        var potentialEntityType = paramValuePart.Substring(paramValueIndex, firstColonIndex - paramValueIndex).Trim();
                        var afterFirstColon = firstColonIndex + 1;
                        
                        // Пропускаем пробелы после первого :
                        while (afterFirstColon < paramValuePart.Length && char.IsWhiteSpace(paramValuePart[afterFirstColon]))
                        {
                            afterFirstColon++;
                        }
                        
                        if (afterFirstColon < paramValuePart.Length)
                        {
                            // Проверяем, является ли следующее слово "index"
                            if (afterFirstColon + 5 <= paramValuePart.Length &&
                                paramValuePart.Substring(afterFirstColon, 5).Equals("index", StringComparison.OrdinalIgnoreCase))
                            {
                                // Это формат entityType: index: value
                                entityType = potentialEntityType;
                                paramValueIndex = afterFirstColon + 5; // пропускаем "index"
                                
                                // Пропускаем пробелы после "index"
                                while (paramValueIndex < paramValuePart.Length && char.IsWhiteSpace(paramValuePart[paramValueIndex]))
                                {
                                    paramValueIndex++;
                                }
                                
                                // Пропускаем :
                                if (paramValueIndex < paramValuePart.Length && paramValuePart[paramValueIndex] == ':')
                                {
                                    paramValueIndex++;
                                }
                                
                                // Пропускаем пробелы после :
                                while (paramValueIndex < paramValuePart.Length && char.IsWhiteSpace(paramValuePart[paramValueIndex]))
                                {
                                    paramValueIndex++;
                                }
                            }
                            else if (paramValuePart[afterFirstColon] == '{')
                            {
                                // Это формат entityType: { ... }
                                entityType = potentialEntityType;
                                paramValueIndex = afterFirstColon;
                                
                                // Парсим вложенную структуру
                                var (complexValue, consumed) = ParseComplexValue(paramValuePart, paramValueIndex);
                                if (complexValue != null)
                                {
                                    // Создаем словарь с entityType как ключом
                                    var complexDict = new Dictionary<string, object> { { entityType, complexValue } };
                                    
                                    parameters.Add(new ComplexValueParameterNode 
                                    { 
                                        Name = paramName,
                                        ParameterName = paramName,
                                        Value = complexDict 
                                    });
                                    
                                    // Переходим к следующему параметру
                                    paramStart = paramValueStart + paramValueIndex + consumed;
                                    
                                    // Ищем следующую запятую или конец
                                    while (paramStart < remainingPart.Length && remainingPart[paramStart] != ',')
                                    {
                                        paramStart++;
                                    }
                                    continue;
                                }
                                else
                                {
                                    // Не удалось распарсить, используем paramName как entityType
                                    entityType = paramName;
                                    paramValueIndex = 0;
                                }
                            }
                            else
                            {
                                // Это не формат entityType: index: или entityType: {, возможно просто значение
                                // Используем paramName как entityType для обратной совместимости
                                entityType = paramName;
                                paramValueIndex = 0;
                            }
                        }
                        else
                        {
                            // Нет значения после :
                            entityType = paramName;
                            paramValueIndex = 0;
                        }
                    }
                    else
                    {
                        // Нет entityType в значении, проверяем, может быть сразу { ... }
                        if (paramValueIndex < paramValuePart.Length && paramValuePart[paramValueIndex] == '{')
                        {
                            // Парсим вложенную структуру
                            var (complexValue, consumed) = ParseComplexValue(paramValuePart, paramValueIndex);
                            if (complexValue != null)
                            {
                                parameters.Add(new ComplexValueParameterNode 
                                { 
                                    Name = paramName,
                                    ParameterName = paramName,
                                    Value = complexValue 
                                });
                                
                                // Переходим к следующему параметру
                                paramStart = paramValueStart + paramValueIndex + consumed;
                                
                                // Ищем следующую запятую или конец
                                while (paramStart < remainingPart.Length && remainingPart[paramStart] != ',')
                                {
                                    paramStart++;
                                }
                                continue;
                            }
                        }
                        
                        // Нет entityType в значении, используем paramName
                        entityType = paramName;
                    }

                    // Пропускаем пробелы
                    while (paramValueIndex < paramValuePart.Length && char.IsWhiteSpace(paramValuePart[paramValueIndex]))
                    {
                        paramValueIndex++;
                    }

                    // Опционально "index:"
                    var indexKeyword = "index:";
                    if (paramValueIndex + indexKeyword.Length <= paramValuePart.Length &&
                        paramValuePart.Substring(paramValueIndex, indexKeyword.Length)
                            .Equals(indexKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        paramValueIndex += indexKeyword.Length;

                        while (paramValueIndex < paramValuePart.Length && char.IsWhiteSpace(paramValuePart[paramValueIndex]))
                        {
                            paramValueIndex++;
                        }
                    }

                    if (paramValueIndex >= paramValuePart.Length ||
                        (!char.IsDigit(paramValuePart[paramValueIndex]) && paramValuePart[paramValueIndex] != '-'))
                    {
                        break;
                    }

                    var index = ParseNumber(paramValuePart, ref paramValueIndex);
                    
                    if (!string.IsNullOrEmpty(entityType))
                    {
                        parameters.Add(new FunctionParameterNode 
                        { 
                            Name = paramName, 
                            ParameterName = paramName,
                            EntityType = entityType, 
                            Index = index 
                        });
                    }

                    // Переходим к следующему параметру
                    paramStart = paramValueStart + paramValueIndex;
                    
                    // Ищем следующую запятую или конец
                    while (paramStart < remainingPart.Length && remainingPart[paramStart] != ',')
                    {
                        paramStart++;
                    }
                }
            }

            return parameters;
        }

        private List<ParameterNode> ParsePushParameters(string parametersPart)
        {
            var part = parametersPart.Trim();
            if (string.IsNullOrEmpty(part))
                return new List<ParameterNode>();

            // [0] — слот
            if (part.StartsWith("["))
                return ParseMemoryParameters(parametersPart);

            // string: "..."
            if (part.StartsWith("string:", StringComparison.OrdinalIgnoreCase))
            {
                var after = part.Substring(7).Trim();
                if (after.Length >= 2 && after.StartsWith("\"") && after.EndsWith("\""))
                {
                    var value = after.Substring(1, after.Length - 2).Replace("\\\"", "\"");
                    return new List<ParameterNode> { new StringParameterNode { Name = "string", Value = value } };
                }
            }

            // целое число (в т.ч. для arity)
            var numStart = 0;
            if (part.Length > 0 && part[0] == '-') numStart = 1;
            var allDigits = numStart < part.Length;
            for (var j = numStart; j < part.Length && allDigits; j++)
                allDigits = char.IsDigit(part[j]);
            if (allDigits && part.Length > 0 && long.TryParse(part, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var numVal))
                return new List<ParameterNode> { new IndexParameterNode { Name = "int", Value = numVal } };

            // тип: stream, file, ...
            return new List<ParameterNode> { new TypeLiteralParameterNode { TypeName = part } };
        }

        private List<ParameterNode> ParseMemoryParameters(string parametersPart)
        {
            // Формат: [0]
            var parameters = new List<ParameterNode>();
            var i = 0;

            // Пропускаем пробелы
            while (i < parametersPart.Length && char.IsWhiteSpace(parametersPart[i]))
            {
                i++;
            }

            if (i >= parametersPart.Length) return parameters;

            // Ищем открывающую скобку [
            if (parametersPart[i] == '[')
            {
                i++; // пропускаем [
                
                // Пропускаем пробелы
                while (i < parametersPart.Length && char.IsWhiteSpace(parametersPart[i]))
                {
                    i++;
                }

                if (i >= parametersPart.Length) return parameters;

                // Парсим число
                var start = i;
                while (i < parametersPart.Length && char.IsDigit(parametersPart[i]))
                {
                    i++;
                }

                if (i > start)
                {
                    var addressStr = parametersPart.Substring(start, i - start);
                    if (long.TryParse(addressStr, out var address))
                    {
                        // Пропускаем пробелы
                        while (i < parametersPart.Length && char.IsWhiteSpace(parametersPart[i]))
                        {
                            i++;
                        }

                        // Ищем закрывающую скобку ]
                        if (i < parametersPart.Length && parametersPart[i] == ']')
                        {
                            parameters.Add(new MemoryParameterNode { Name = "index", Index = address });
                        }
                    }
                }
            }

            return parameters;
        }

        private (Dictionary<string, object>?, int) ParseComplexValue(string source, int startIndex)
        {
            // Парсит вложенную структуру вида: { key: value, key2: value2 }
            var result = new Dictionary<string, object>();
            var i = startIndex;
            
            if (i >= source.Length || source[i] != '{')
            {
                return (null, 0);
            }
            
            i++; // пропускаем {
            var braceDepth = 1;
            var bracketDepth = 0;
            
            while (i < source.Length && braceDepth > 0)
            {
                if (source[i] == '{')
                {
                    braceDepth++;
                }
                else if (source[i] == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        i++; // пропускаем закрывающую }
                        break;
                    }
                }
                else if (source[i] == '[')
                {
                    bracketDepth++;
                }
                else if (source[i] == ']')
                {
                    bracketDepth--;
                }
                else if (source[i] == '"')
                {
                    // Пропускаем строку в кавычках
                    i++;
                    while (i < source.Length && source[i] != '"')
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            i += 2;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    if (i < source.Length) i++;
                    continue;
                }
                
                i++;
            }
            
            if (braceDepth != 0)
            {
                return (null, 0);
            }
            
            // Извлекаем содержимое между фигурными скобками
            var content = source.Substring(startIndex + 1, i - startIndex - 2).Trim();
            
            // Парсим содержимое
            var contentIndex = 0;
            while (contentIndex < content.Length)
            {
                // Пропускаем пробелы и запятые
                while (contentIndex < content.Length && (char.IsWhiteSpace(content[contentIndex]) || content[contentIndex] == ','))
                {
                    contentIndex++;
                }
                
                if (contentIndex >= content.Length) break;
                
                // Находим ключ (до :)
                var colonPos = content.IndexOf(':', contentIndex);
                if (colonPos == -1) break;
                
                var key = content.Substring(contentIndex, colonPos - contentIndex).Trim();
                contentIndex = colonPos + 1;
                
                // Пропускаем пробелы после :
                while (contentIndex < content.Length && char.IsWhiteSpace(content[contentIndex]))
                {
                    contentIndex++;
                }
                
                if (contentIndex >= content.Length) break;
                
                // Парсим значение
                object? value = null;
                
                if (content[contentIndex] == '[')
                {
                    // Массив
                    var arrayStart = contentIndex;
                    var arrayDepth = 0;
                    while (contentIndex < content.Length)
                    {
                        if (content[contentIndex] == '[')
                        {
                            arrayDepth++;
                        }
                        else if (content[contentIndex] == ']')
                        {
                            arrayDepth--;
                            if (arrayDepth == 0)
                            {
                                contentIndex++;
                                break;
                            }
                        }
                        else if (content[contentIndex] == '"')
                        {
                            contentIndex++;
                            while (contentIndex < content.Length && content[contentIndex] != '"')
                            {
                                if (content[contentIndex] == '\\' && contentIndex + 1 < content.Length)
                                {
                                    contentIndex += 2;
                                }
                                else
                                {
                                    contentIndex++;
                                }
                            }
                            if (contentIndex < content.Length) contentIndex++;
                            continue;
                        }
                        contentIndex++;
                    }
                    
                    var arrayContent = content.Substring(arrayStart + 1, contentIndex - arrayStart - 2).Trim();
                    var arrayItems = ParseArrayItems(arrayContent);
                    value = arrayItems;
                }
                else if (content[contentIndex] == '{')
                {
                    // Вложенный объект
                    var (nestedObj, consumed) = ParseComplexValue(content, contentIndex);
                    if (nestedObj != null)
                    {
                        value = nestedObj;
                        contentIndex += consumed;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    // Простое значение (число, строка, и т.д.)
                    var valueStart = contentIndex;
                    while (contentIndex < content.Length && 
                           content[contentIndex] != ',' && 
                           content[contentIndex] != '}' &&
                           content[contentIndex] != ']')
                    {
                        contentIndex++;
                    }
                    
                    var valueStr = content.Substring(valueStart, contentIndex - valueStart).Trim();
                    
                    // Пытаемся распарсить как число
                    if (long.TryParse(valueStr, out var longValue))
                    {
                        value = longValue;
                    }
                    else if (double.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
                    {
                        value = doubleValue;
                    }
                    else
                    {
                        value = valueStr;
                    }
                }
                
                if (value != null)
                {
                    result[key] = value;
                }
                
                // Пропускаем до следующего ключа или конца
                while (contentIndex < content.Length && content[contentIndex] != ',' && content[contentIndex] != '}')
                {
                    contentIndex++;
                }
            }
            
            return (result, i - startIndex);
        }

        private List<object> ParseArrayItems(string arrayContent)
        {
            var items = new List<object>();
            var i = 0;
            
            while (i < arrayContent.Length)
            {
                // Пропускаем пробелы и запятые
                while (i < arrayContent.Length && (char.IsWhiteSpace(arrayContent[i]) || arrayContent[i] == ','))
                {
                    i++;
                }
                
                if (i >= arrayContent.Length) break;
                
                object? item = null;
                
                if (arrayContent[i] == '{')
                {
                    // Объект в массиве
                    var (obj, consumed) = ParseComplexValue(arrayContent, i);
                    if (obj != null)
                    {
                        item = obj;
                        i += consumed;
                    }
                    else
                    {
                        break;
                    }
                }
                else if (arrayContent[i] == '[')
                {
                    // Вложенный массив
                    var arrayStart = i;
                    var depth = 0;
                    while (i < arrayContent.Length)
                    {
                        if (arrayContent[i] == '[')
                        {
                            depth++;
                        }
                        else if (arrayContent[i] == ']')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                i++;
                                break;
                            }
                        }
                        i++;
                    }
                    
                    var nestedArrayContent = arrayContent.Substring(arrayStart + 1, i - arrayStart - 2).Trim();
                    item = ParseArrayItems(nestedArrayContent);
                }
                else
                {
                    // Простое значение
                    var valueStart = i;
                    while (i < arrayContent.Length && 
                           arrayContent[i] != ',' && 
                           arrayContent[i] != ']' &&
                           arrayContent[i] != '}')
                    {
                        i++;
                    }
                    
                    var valueStr = arrayContent.Substring(valueStart, i - valueStart).Trim();
                    
                    if (long.TryParse(valueStr, out var longValue))
                    {
                        item = longValue;
                    }
                    else if (double.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
                    {
                        item = doubleValue;
                    }
                    else
                    {
                        item = valueStr;
                    }
                }
                
                if (item != null)
                {
                    items.Add(item);
                }
            }
            
            return items;
        }

        public ProgramStructure ParseProgram(string sourceCode)
        {
            var structure = new ProgramStructure();
            var lines = sourceCode.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            var i = 0;
            string? currentProcedure = null;
            string? currentFunction = null;
            var inProcedure = false;
            var inFunction = false;
            var inEntryPoint = false;
            var inAsmBlock = false;
            var asmBraceDepth = 0;
            var asmBuffer = new System.Text.StringBuilder();
            var blockBraceDepth = 0; // for procedure/function/entrypoint
            var highLevelLines = new List<string>();

            while (i < lines.Length)
            {
                var rawLine = lines[i];
                var line = rawLine.Trim();
                
                // Пропускаем пустые строки и комментарии
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                {
                    i++;
                    continue;
                }

                // Парсим директиву версии @AGI
                if (line.StartsWith("@AGI"))
                {
                    var versionPart = line.Substring(4).Trim();
                    structure.Version = versionPart;
                    i++;
                    continue;
                }

                // Парсим program
                if (line.StartsWith("program"))
                {
                    var programPart = line.Substring(7).Trim().TrimEnd(';');
                    structure.ProgramName = programPart;
                    i++;
                    continue;
                }

                // Парсим module
                if (line.StartsWith("module"))
                {
                    var modulePart = line.Substring(6).Trim().TrimEnd(';');
                    structure.Module = modulePart;
                    i++;
                    continue;
                }

                // Парсим procedure
                if (line.StartsWith("procedure"))
                {
                    var procPart = line.Substring(9).Trim();
                    var nameEnd = procPart.IndexOf(' ');
                    if (nameEnd == -1) nameEnd = procPart.IndexOf('{');
                    if (nameEnd == -1) nameEnd = procPart.Length;
                    
                    currentProcedure = procPart.Substring(0, nameEnd).Trim();
                    structure.Procedures[currentProcedure] = new List<string>();
                    inProcedure = true;
                    inAsmBlock = false;
                    asmBraceDepth = 0;
                    blockBraceDepth = rawLine.Contains("{") ? 1 : 0;
                    highLevelLines.Clear();
                    
                    i++;
                    continue;
                }

                // Парсим function
                if (line.StartsWith("function"))
                {
                    var funcPart = line.Substring(8).Trim();
                    var nameEnd = funcPart.IndexOf(' ');
                    if (nameEnd == -1) nameEnd = funcPart.IndexOf('{');
                    if (nameEnd == -1) nameEnd = funcPart.IndexOf('(');
                    if (nameEnd == -1) nameEnd = funcPart.Length;
                    
                    currentFunction = funcPart.Substring(0, nameEnd).Trim();
                    structure.Functions[currentFunction] = new List<string>();
                    inFunction = true;
                    inAsmBlock = false;
                    asmBraceDepth = 0;
                    blockBraceDepth = rawLine.Contains("{") ? 1 : 0;
                    highLevelLines.Clear();
                    
                    i++;
                    continue;
                }

                // Парсим entrypoint
                if (line.StartsWith("entrypoint"))
                {
                    structure.EntryPoint = new List<string>();
                    inEntryPoint = true;
                    inAsmBlock = false;
                    asmBraceDepth = 0;
                    blockBraceDepth = rawLine.Contains("{") ? 1 : 0;
                    highLevelLines.Clear();
                    
                    i++;
                    continue;
                }

                // Проверяем начало asm блока
                if (!inAsmBlock && line.Contains("asm") && line.Contains("{"))
                {
                    inAsmBlock = true;
                    asmBraceDepth = 1;
                    asmBuffer.Clear();

                    // Берем все после первой '{' на этой строке (возможны инструкции на той же строке)
                    var bracePos = rawLine.IndexOf('{');
                    if (bracePos >= 0 && bracePos + 1 < rawLine.Length)
                    {
                        asmBuffer.AppendLine(rawLine.Substring(bracePos + 1));
                    }
                    i++;
                    continue;
                }

                // High-level statements inside procedure/function/entrypoint (outside asm)
                if ((inProcedure || inFunction || inEntryPoint) && !inAsmBlock)
                {
                    // add current line to high-level buffer unless it is a lone brace
                    if (line is not "{" and not "}")
                    {
                        highLevelLines.Add(rawLine);
                    }

                    // update block brace depth (ignore braces in strings, stop at // comments)
                    var inStr = false;
                    var esc = false;
                    for (var j = 0; j < rawLine.Length; j++)
                    {
                        var ch = rawLine[j];
                        if (inStr)
                        {
                            if (esc) { esc = false; continue; }
                            if (ch == '\\') { esc = true; continue; }
                            if (ch == '"') inStr = false;
                            continue;
                        }
                        if (ch == '/' && j + 1 < rawLine.Length && rawLine[j + 1] == '/')
                        {
                            break;
                        }
                        if (ch == '"') { inStr = true; continue; }
                        if (ch == '{') blockBraceDepth++;
                        if (ch == '}') blockBraceDepth--;
                    }

                    if (blockBraceDepth <= 0)
                    {
                        var hlText = string.Join("\n", highLevelLines);
                        var compiledAsm = CompileHighLevelCode(hlText);

                        foreach (var instruction in compiledAsm)
                        {
                            if (inProcedure && currentProcedure != null)
                                structure.Procedures[currentProcedure].Add(instruction);
                            else if (inFunction && currentFunction != null)
                                structure.Functions[currentFunction].Add(instruction);
                            else if (inEntryPoint)
                                structure.EntryPoint!.Add(instruction);
                        }

                        highLevelLines.Clear();
                        blockBraceDepth = 0;

                        if (inProcedure) { inProcedure = false; currentProcedure = null; }
                        if (inFunction) { inFunction = false; currentFunction = null; }
                        if (inEntryPoint) { inEntryPoint = false; }
                    }

                    i++;
                    continue;
                }

                // Подсчитываем фигурные скобки в asm блоке
                if (inAsmBlock)
                {
                    // Накопление текста asm-блока построчно, но закрывающую '}' для asm не включаем.
                    // При этом учитываем вложенные { } (inline shape literals и т.п.) и строки.
                    var scan = rawLine;
                    var inString = false;
                    var escaped = false;
                    var lineEnd = scan.Length;

                    for (var j = 0; j < scan.Length; j++)
                    {
                        var ch = scan[j];
                        if (inString)
                        {
                            if (escaped)
                            {
                                escaped = false;
                                continue;
                            }
                            if (ch == '\\')
                            {
                                escaped = true;
                                continue;
                            }
                            if (ch == '"')
                            {
                                inString = false;
                            }
                            continue;
                        }

                        // вне строк — поддерживаем однострочные комментарии //
                        if (ch == '/' && j + 1 < scan.Length && scan[j + 1] == '/')
                        {
                            // игнорируем остаток строки
                            break;
                        }

                        if (ch == '"')
                        {
                            inString = true;
                            continue;
                        }

                        if (ch == '{')
                        {
                            asmBraceDepth++;
                            continue;
                        }

                        if (ch == '}')
                        {
                            asmBraceDepth--;
                            if (asmBraceDepth == 0)
                            {
                                // это закрывающая скобка asm-блока — не включаем ее
                                lineEnd = j;
                                break;
                            }
                            continue;
                        }
                    }

                    // Добавляем часть строки до закрывающей '}' asm-блока (или всю строку)
                    if (lineEnd > 0)
                    {
                        asmBuffer.AppendLine(scan.Substring(0, lineEnd));
                    }

                    // Если asm блок закрыт — сплитим на инструкции по top-level ';' и сохраняем
                    if (asmBraceDepth == 0)
                    {
                        inAsmBlock = false;

                        var asmText = asmBuffer.ToString();
                        var instructions = SplitAsmInstructions(asmText);

                        foreach (var instruction in instructions)
                        {
                            if (inProcedure && currentProcedure != null)
                            {
                                structure.Procedures[currentProcedure].Add(instruction);
                            }
                            else if (inFunction && currentFunction != null)
                            {
                                structure.Functions[currentFunction].Add(instruction);
                            }
                            else if (inEntryPoint)
                            {
                                structure.EntryPoint!.Add(instruction);
                            }
                        }

                        i++;
                        continue;
                    }
                }

                // Top-level high-level call like `Main;` (implicit entrypoint)
                if (!inProcedure && !inFunction && !inEntryPoint && !inAsmBlock)
                {
                    var stmt = line.Trim().TrimEnd(';');
                    if (System.Text.RegularExpressions.Regex.IsMatch(stmt, @"^[A-Za-z_]\w*$"))
                    {
                        structure.EntryPoint ??= new List<string>();
                        structure.EntryPoint.Add($"call {stmt}");
                        i++;
                        continue;
                    }
                }

                i++;
            }

            return structure;
        }

        private static List<string> SplitAsmInstructions(string asmText)
        {
            // Делит asm-текст на инструкции по ';', игнорируя ';' внутри строк, [ ], { } и однострочные комментарии //
            var result = new List<string>();
            var sb = new System.Text.StringBuilder();

            var braceDepth = 0;
            var bracketDepth = 0;
            var inString = false;
            var escaped = false;
            var opcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "addvertex",
                "addrelation",
                "addshape",
                "call",
                "pop",
                "push",
                "nop",
                "def",
                "defgen",
                "callobj",
                "awaitobj"
            };

            for (var i = 0; i < asmText.Length; i++)
            {
                var ch = asmText[i];

                if (inString)
                {
                    sb.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (ch == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                // комментарии //
                if (ch == '/' && i + 1 < asmText.Length && asmText[i + 1] == '/')
                {
                    // пропускаем до конца строки
                    while (i < asmText.Length && asmText[i] != '\n')
                    {
                        i++;
                    }
                    // добавим перевод строки как разделитель пробелов
                    sb.Append(' ');
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    sb.Append(ch);
                    continue;
                }

                if (ch == '{')
                {
                    braceDepth++;
                    sb.Append(ch);
                    continue;
                }
                if (ch == '}')
                {
                    if (braceDepth > 0) braceDepth--;
                    sb.Append(ch);
                    continue;
                }
                if (ch == '[')
                {
                    bracketDepth++;
                    sb.Append(ch);
                    continue;
                }
                if (ch == ']')
                {
                    if (bracketDepth > 0) bracketDepth--;
                    sb.Append(ch);
                    continue;
                }

                if (ch == ';' && braceDepth == 0 && bracketDepth == 0)
                {
                    var instr = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(instr))
                    {
                        result.Add(instr);
                    }
                    sb.Clear();
                    continue;
                }

                // Newline как разделитель инструкций (без ';') — но только если следующая строка начинается с opcode.
                // Это позволяет писать:
                //   call ..., shapeB: shape: { ... }
                //   pop [1];
                // и НЕ ломает переносы внутри параметров вроде:
                //   addvertex index: 1,
                //     dimensions: [...]
                if ((ch == '\r' || ch == '\n') && braceDepth == 0 && bracketDepth == 0)
                {
                    // Пропускаем CRLF
                    var lookahead = i + 1;
                    if (ch == '\r' && lookahead < asmText.Length && asmText[lookahead] == '\n')
                    {
                        lookahead++;
                    }

                    // Ищем следующий "токен" (пропуская пустые строки/пробелы/таб/комментарии)
                    while (lookahead < asmText.Length)
                    {
                        // пробелы/табы/переводы строк
                        while (lookahead < asmText.Length && (asmText[lookahead] == ' ' || asmText[lookahead] == '\t' || asmText[lookahead] == '\r' || asmText[lookahead] == '\n'))
                        {
                            lookahead++;
                        }
                        if (lookahead >= asmText.Length) break;

                        // комментарий //
                        if (asmText[lookahead] == '/' && lookahead + 1 < asmText.Length && asmText[lookahead + 1] == '/')
                        {
                            // пропускаем до конца строки
                            while (lookahead < asmText.Length && asmText[lookahead] != '\n')
                            {
                                lookahead++;
                            }
                            continue;
                        }

                        break;
                    }

                    // Читаем идентификатор opcode
                    var tokStart = lookahead;
                    if (tokStart < asmText.Length && (char.IsLetter(asmText[tokStart]) || asmText[tokStart] == '_'))
                    {
                        var tokEnd = tokStart + 1;
                        while (tokEnd < asmText.Length && (char.IsLetterOrDigit(asmText[tokEnd]) || asmText[tokEnd] == '_'))
                        {
                            tokEnd++;
                        }
                        var nextToken = asmText.Substring(tokStart, tokEnd - tokStart);

                        if (opcodes.Contains(nextToken))
                        {
                            var instr = sb.ToString().Trim();
                            if (!string.IsNullOrWhiteSpace(instr))
                            {
                                result.Add(instr);
                                sb.Clear();
                            }

                            // перемещаемся дальше — текущий newline не добавляем
                            continue;
                        }
                    }

                    // иначе newline не считается разделителем, нормализуем в пробел
                    sb.Append(' ');
                    continue;
                }

                // normalize tabs/newlines (которые не стали разделителями) to spaces to keep Parser.Parse happy
                if (ch == '\t')
                {
                    sb.Append(' ');
                }
                else if (ch == '\r' || ch == '\n')
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
            }

            var tail = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                result.Add(tail);
            }

            return result;
        }

        private string CollectHighLevelCode(string[] lines, ref int startIndex, bool inProcedure, bool inFunction, bool inEntryPoint)
        {
            var code = new System.Text.StringBuilder();
            var braceDepth = 0;
            var inString = false;
            var i = startIndex;

            while (i < lines.Length)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
                {
                    i++;
                    continue;
                }

                // Подсчитываем фигурные скобки
                foreach (var ch in line)
                {
                    if (inString)
                    {
                        if (ch == '"') inString = false;
                        continue;
                    }
                    if (ch == '"')
                    {
                        inString = true;
                        continue;
                    }
                    if (ch == '{') braceDepth++;
                    if (ch == '}')
                    {
                        braceDepth--;
                        if (braceDepth < 0)
                        {
                            // Закрывающая скобка блока - возвращаем собранный код
                            startIndex = i;
                            return code.ToString();
                        }
                    }
                }

                code.AppendLine(line);
                i++;
            }

            startIndex = i;
            return code.ToString();
        }

        private List<string> CompileHighLevelCode(string highLevelCode)
        {
            var asmInstructions = new List<string>();
            var lines = highLevelCode.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            // varName -> (kind,index) where kind in: vertex|relation|shape|memory|stream
            var vars = new Dictionary<string, (string Kind, int Index)>(StringComparer.Ordinal);
            var vertexCounter = 1;
            var relationCounter = 1;
            var shapeCounter = 1;
            var memorySlotCounter = 0;
            var inVarBlock = false;
            var varBlockLines = new List<string>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//") || line == "{" || line == "}")
                    continue;

                // Парсим var блок (var на отдельной строке или "var stream1 := stream<file>;" на одной строке)
                if (line == "var")
                {
                    inVarBlock = true;
                    varBlockLines.Clear();
                    continue;
                }
                if (line.StartsWith("var "))
                {
                    var rest = line.Substring(4).Trim().TrimEnd(';');
                    var singleStreamDecl = System.Text.RegularExpressions.Regex.IsMatch(rest, @"^(\w+)\s*:=\s*stream\s*<\s*\w+\s*>\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (singleStreamDecl)
                    {
                        inVarBlock = true;
                        varBlockLines.Clear();
                        varBlockLines.Add(rest);
                        continue;
                    }
                }

                if (inVarBlock)
                {
                    var declLine = line.TrimEnd(';');
                    var isEntityDecl = System.Text.RegularExpressions.Regex.IsMatch(
                        declLine,
                        @"^(\w+)\s*:\s*(vertex|relation|shape)\s*=\s*(.+)$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var isStreamDecl = System.Text.RegularExpressions.Regex.IsMatch(
                        declLine.Trim(),
                        @"^(\w+)\s*:=\s*stream\s*<\s*\w+\s*>\s*$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (isEntityDecl || isStreamDecl)
                    {
                        varBlockLines.Add(line);
                        continue;
                    }

                    inVarBlock = false;
                    var varAsm = CompileVarBlock(varBlockLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter);
                    asmInstructions.AddRange(varAsm);
                }

                // Декларация stream вне var-блока: stream1 := stream<file>;
                var streamDeclMatch = System.Text.RegularExpressions.Regex.Match(line.Trim().TrimEnd(';'), @"^(\w+)\s*:=\s*stream\s*<\s*(\w+)\s*>\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (streamDeclMatch.Success)
                {
                    var streamName = streamDeclMatch.Groups[1].Value;
                    var elementType = streamDeclMatch.Groups[2].Value.ToLowerInvariant();
                    var baseSlot = memorySlotCounter++;
                    var streamSlot = memorySlotCounter++;
                    vars[streamName] = ("stream", streamSlot);
                    asmInstructions.Add("push stream");
                    asmInstructions.Add("def");
                    asmInstructions.Add($"pop [{baseSlot}]");
                    asmInstructions.Add($"push [{baseSlot}]");
                    asmInstructions.Add($"push {elementType}");
                    asmInstructions.Add("push 1");
                    asmInstructions.Add("defgen");
                    asmInstructions.Add($"pop [{streamSlot}]");
                    continue;
                }

                // Декларации vertex/relation/shape вне var-блока
                var declLine2 = line.TrimEnd(';');
                var isDecl2 = System.Text.RegularExpressions.Regex.IsMatch(
                    declLine2,
                    @"^(\w+)\s*:\s*(vertex|relation|shape)\s*=\s*(.+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (isDecl2)
                {
                    var declAsm = CompileVarBlock(new List<string> { line }, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter);
                    asmInstructions.AddRange(declAsm);
                    continue;
                }

                // Вызов метода: stream1.open("stream1"); → push [obj], push string: "...", push 1, callobj "open"
                var methodMatch = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^(\w+)\.(\w+)\(([^)]*)\)\s*;?$");
                if (methodMatch.Success && vars.TryGetValue(methodMatch.Groups[1].Value, out var objVar))
                {
                    var method = methodMatch.Groups[2].Value;
                    var arg = methodMatch.Groups[3].Value.Trim();
                    asmInstructions.Add($"push [{objVar.Index}]");
                    if (arg.Length >= 2 && arg.StartsWith("\"") && arg.EndsWith("\""))
                        asmInstructions.Add($"push string: {arg}");
                    else
                        asmInstructions.Add($"push string: \"{arg.Replace("\"", "\\\"")}\"");
                    asmInstructions.Add("push 1");
                    asmInstructions.Add($"callobj \"{method}\"");
                    continue;
                }

                // Присваивания (включая await и compile); нормализуем var x := y в x = y
                if (line.Contains("=") && !line.Contains("=>"))
                {
                    var trimmedForAssign = line.Trim().TrimEnd(';');
                    var varAssignMatch = System.Text.RegularExpressions.Regex.Match(trimmedForAssign, @"^var\s+(\w+)\s*:=\s*(.+)$");
                    if (varAssignMatch.Success)
                        trimmedForAssign = varAssignMatch.Groups[1].Value + " = " + varAssignMatch.Groups[2].Value.Trim();
                    else
                    {
                        varAssignMatch = System.Text.RegularExpressions.Regex.Match(trimmedForAssign, @"^var\s+(\w+)\s*=\s*(.+)$");
                        if (varAssignMatch.Success)
                            trimmedForAssign = varAssignMatch.Groups[1].Value + " = " + varAssignMatch.Groups[2].Value.Trim();
                    }
                    var assignmentAsm = CompileAssignment(trimmedForAssign, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter);
                    asmInstructions.AddRange(assignmentAsm);
                }
                // Вызовы функций (print и т.д.)
                else if (line.Contains("(") && line.Contains(")"))
                {
                    var callAsm = CompileFunctionCall(line, vars);
                    asmInstructions.AddRange(callAsm);
                }
                // Вызовы процедур
                else if (!string.IsNullOrWhiteSpace(line) && !line.Contains("{") && !line.Contains("}"))
                {
                    var procName = line.Trim().TrimEnd(';');
                    asmInstructions.Add($"call {procName}");
                }
            }

            if (inVarBlock && varBlockLines.Count > 0)
            {
                var varAsm = CompileVarBlock(varBlockLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter);
                asmInstructions.AddRange(varAsm);
            }

            return asmInstructions;
        }

        private List<string> CompileVarBlock(List<string> varLines, Dictionary<string, (string Kind, int Index)> vars, ref int vertexCounter, ref int relationCounter, ref int shapeCounter, ref int memorySlotCounter)
        {
            var asm = new List<string>();

            foreach (var line in varLines)
            {
                var trimmed = line.Trim().TrimEnd(';');
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // Парсим stream: name := stream<file>;
                var streamMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\w+)\s*:=\s*stream\s*<\s*(\w+)\s*>\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (streamMatch.Success)
                {
                    var streamName = streamMatch.Groups[1].Value;
                    var elementType = streamMatch.Groups[2].Value.ToLowerInvariant();
                    var baseSlot = memorySlotCounter++;
                    var streamSlot = memorySlotCounter++;
                    vars[streamName] = ("stream", streamSlot);
                    asm.Add("push stream");
                    asm.Add("def");
                    asm.Add($"pop [{baseSlot}]");
                    asm.Add($"push [{baseSlot}]");
                    asm.Add($"push {elementType}");
                    asm.Add("push 1");
                    asm.Add("defgen");
                    asm.Add($"pop [{streamSlot}]");
                    continue;
                }

                // Парсим: name: type = { ... };
                var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\w+)\s*:\s*(\w+)\s*=\s*(.+)$");
                if (!match.Success) continue;

                var varName = match.Groups[1].Value;
                var varType = match.Groups[2].Value.ToLower();
                var initValue = match.Groups[3].Value.Trim();

                if (varType == "vertex")
                {
                    var index = vertexCounter++;
                    vars[varName] = ("vertex", index);
                    var vertexAsm = CompileVertexInit(index, initValue);
                    asm.AddRange(vertexAsm);
                }
                else if (varType == "relation")
                {
                    var index = relationCounter++;
                    vars[varName] = ("relation", index);
                    var relationAsm = CompileRelationInit(index, initValue, vars);
                    asm.AddRange(relationAsm);
                }
                else if (varType == "shape")
                {
                    var index = shapeCounter++;
                    vars[varName] = ("shape", index);
                    var shapeAsm = CompileShapeInit(index, initValue, vars, ref vertexCounter);
                    asm.AddRange(shapeAsm);
                }
            }

            return asm;
        }

        private List<string> CompileVertexInit(int index, string initValue)
        {
            var asm = new List<string>();
            var dims = ExtractDimensionsFromInit(initValue);
            var weight = ExtractWeightFromInit(initValue);
            var data = ExtractDataFromInit(initValue);

            var dimsStr = string.Join(", ", dims);
            var asmLine = $"addvertex index: {index}, dimensions: [{dimsStr}], weight: {weight.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            if (!string.IsNullOrEmpty(data))
            {
                if (data.StartsWith("BIN:"))
                {
                    var base64 = data.Substring(4).Trim();
                    asmLine += $", data: binary: base64: \"{base64}\"";
                }
                else
                {
                    asmLine += $", data: text: \"{data}\"";
                }
            }

            asm.Add(asmLine);
            return asm;
        }

        private List<string> CompileRelationInit(int index, string initValue, Dictionary<string, (string Kind, int Index)> vars)
        {
            var asm = new List<string>();
            
            // Парсим: {v1=>v2,W:0.6} или {r1=>r3,W:0.6}
            var match = System.Text.RegularExpressions.Regex.Match(initValue, @"(\w+)\s*=>\s*(\w+)");
            if (!match.Success) return asm;

            var fromVar = match.Groups[1].Value;
            var toVar = match.Groups[2].Value;
            var weight = ExtractWeightFromInit(initValue);

            if (!vars.ContainsKey(fromVar) || !vars.ContainsKey(toVar))
                return asm;

            var (fromKind, fromIdx) = vars[fromVar];
            var (toKind, toIdx) = vars[toVar];

            var fromType = fromKind is "relation" ? "relation" : "vertex";
            var toType = toKind is "relation" ? "relation" : "vertex";

            var asmLine = $"addrelation index: {index}, from: {fromType}: index: {fromIdx}, to: {toType}: index: {toIdx}, weight: {weight.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            asm.Add(asmLine);
            return asm;
        }

        private static string? ExtractBracketContent(string s, string key)
        {
            var keyPos = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (keyPos < 0) return null;
            var i = keyPos + key.Length;
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ':')) i++;
            if (i >= s.Length || s[i] != '[') return null;

            var start = i + 1;
            var depth = 1;
            var inString = false;
            var escaped = false;
            for (i = start; i < s.Length; i++)
            {
                var ch = s[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '[') { depth++; continue; }
                if (ch == ']')
                {
                    depth--;
                    if (depth == 0)
                        return s.Substring(start, i - start);
                }
            }
            return null;
        }

        private static List<string> SplitTopLevelComma(string s)
        {
            var res = new List<string>();
            var sb = new System.Text.StringBuilder();
            var brace = 0;
            var inString = false;
            var escaped = false;

            foreach (var ch in s)
            {
                if (inString)
                {
                    sb.Append(ch);
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == '"') inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    sb.Append(ch);
                    continue;
                }

                if (ch == '{') { brace++; sb.Append(ch); continue; }
                if (ch == '}') { brace--; sb.Append(ch); continue; }

                if (ch == ',' && brace == 0)
                {
                    var item = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(item)) res.Add(item);
                    sb.Clear();
                    continue;
                }
                sb.Append(ch);
            }

            var tail = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(tail)) res.Add(tail);
            return res;
        }

        private List<string> CompileShapeInit(int index, string initValue, Dictionary<string, (string Kind, int Index)> vars, ref int vertexCounter)
        {
            var asm = new List<string>();
            
            // Парсим: { [v1, v2, v3] } или { VERT:[{DIM:[...]}, {DIM:[...]}] }
            if (initValue.Contains("VERT:"))
            {
                // Формат с VERT: и DIM:
                var inside = ExtractBracketContent(initValue, "VERT");
                var vertices = inside == null ? new List<string>() : SplitTopLevelComma(inside);
                var indices = new List<int>();
                foreach (var v in vertices)
                {
                    var tempIndex = vertexCounter++;
                    var dims = ExtractDimensionsFromInit(v);
                    var dimsStr = string.Join(", ", dims);
                    var line = $"addvertex index: {tempIndex}, dimensions: [{dimsStr}]";
                    if (v.Contains("W:"))
                        line += $", weight: {ExtractWeightFromInit(v).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    asm.Add(line);
                    indices.Add(tempIndex);
                }
                if (indices.Count > 0)
                {
                    var indicesStr = string.Join(", ", indices);
                    asm.Add($"addshape index: {index}, vertices: indices: [{indicesStr}]");
                }
            }
            else
            {
                // Формат с переменными: [v1, v2, v3]
                var match = System.Text.RegularExpressions.Regex.Match(initValue, @"\[([^\]]+)\]");
                if (match.Success)
                {
                    var vnames = match.Groups[1].Value.Split(',').Select(v => v.Trim()).ToList();
                    var indices = new List<int>();
                    foreach (var v in vnames)
                    {
                        if (vars.ContainsKey(v) && vars[v].Kind == "vertex")
                        {
                            indices.Add(vars[v].Index);
                        }
                    }
                    if (indices.Count > 0)
                    {
                        var indicesStr = string.Join(", ", indices);
                        asm.Add($"addshape index: {index}, vertices: indices: [{indicesStr}]");
                    }
                }
            }

            return asm;
        }

        private List<string> CompileAssignment(string line, Dictionary<string, (string Kind, int Index)> vars, ref int shapeCounter, ref int vertexCounter, ref int memorySlotCounter)
        {
            var asm = new List<string>();
            var trimmed = line.Trim().TrimEnd(';');
            
            // Парсим: var = expression;
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\w+)\s*=\s*(.+)$");
            if (!match.Success) return asm;

            var varName = match.Groups[1].Value;
            var expression = match.Groups[2].Value.Trim();

            // var data = await stream1;
            var awaitMatch = System.Text.RegularExpressions.Regex.Match(expression, @"^await\s+(\w+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (awaitMatch.Success && vars.TryGetValue(awaitMatch.Groups[1].Value, out var streamVar) && streamVar.Kind == "stream")
            {
                var dataSlot = memorySlotCounter++;
                vars[varName] = ("memory", dataSlot);
                asm.Add($"push [{streamVar.Index}]");
                asm.Add("awaitobj");
                asm.Add($"pop [{dataSlot}]");
                return asm;
            }

            // var semantic_file_system = compile(data); → push [data], push 1, call compile, pop [result]
            var compileMatch = System.Text.RegularExpressions.Regex.Match(expression, @"^compile\s*\(\s*(\w+)\s*\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (compileMatch.Success && vars.TryGetValue(compileMatch.Groups[1].Value, out var dataVar) && (dataVar.Kind == "memory" || dataVar.Kind == "stream"))
            {
                var resultSlot = memorySlotCounter++;
                vars[varName] = ("memory", resultSlot);
                asm.Add($"push [{dataVar.Index}]");
                asm.Add("push 1");
                asm.Add("call compile");
                asm.Add($"pop [{resultSlot}]");
                return asm;
            }

            // Оператор ] для origin
            if (expression.Contains("]") && !expression.Contains("|"))
            {
                var shapeMatch = System.Text.RegularExpressions.Regex.Match(expression, @"\]\s+(\w+)");
                if (shapeMatch.Success)
                {
                    var shapeVar = shapeMatch.Groups[1].Value;
                    if (vars.ContainsKey(shapeVar) && vars[shapeVar].Kind == "shape")
                    {
                        var shapeIdx = vars[shapeVar].Index;
                        asm.Add($"call \"origin\", shape: index: {shapeIdx}");
                        asm.Add($"pop [0]");
                        vars[varName] = ("memory", 0);
                    }
                }
            }
            // Оператор | для intersection
            else if (expression.Contains("|"))
            {
                var parts = expression.Split('|');
                if (parts.Length == 2)
                {
                    var shapeA = parts[0].Trim();
                    var shapeB = parts[1].Trim();
                    
                    int? shapeAIdx = null, shapeBIdx = null;
                    
                    if (vars.ContainsKey(shapeA) && vars[shapeA].Kind == "shape")
                    {
                        shapeAIdx = vars[shapeA].Index;
                    }
                    
                    if (vars.ContainsKey(shapeB) && vars[shapeB].Kind == "shape")
                    {
                        shapeBIdx = vars[shapeB].Index;
                    }
                    else
                    {
                        // shapeB может быть литералом, создаем временный shape
                        var tempShapeIdx = shapeCounter++;
                        var shapeBAsm = CompileShapeInit(tempShapeIdx, shapeB, vars, ref vertexCounter);
                        asm.AddRange(shapeBAsm);
                        shapeBIdx = tempShapeIdx;
                    }

                    if (shapeAIdx.HasValue && shapeBIdx.HasValue)
                    {
                        asm.Add($"call \"intersect\", shapeA: shape: index: {shapeAIdx.Value}, shapeB: shape: index: {shapeBIdx.Value}");
                        asm.Add($"pop [1]");
                        vars[varName] = ("memory", 1);
                    }
                }
            }

            return asm;
        }

        private List<string> CompileFunctionCall(string line, Dictionary<string, (string Kind, int Index)> vars)
        {
            var asm = new List<string>();
            var trimmed = line.Trim().TrimEnd(';');
            
            // Парсим: print(var);
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"(\w+)\s*\(([^)]+)\)");
            if (match.Success)
            {
                var funcName = match.Groups[1].Value;
                var arg = match.Groups[2].Value.Trim();
                
                if (funcName == "print")
                {
                    if (vars.ContainsKey(arg) && vars[arg].Kind == "memory")
                    {
                        var memIdx = vars[arg].Index;
                        asm.Add($"push [{memIdx}]");
                        asm.Add("push 1");
                        asm.Add("call print");
                    }
                }
            }

            return asm;
        }

        private List<long> ExtractDimensionsFromInit(string initValue)
        {
            var dims = new List<long>();
            var match = System.Text.RegularExpressions.Regex.Match(initValue, @"DIM:\s*\[([^\]]+)\]");
            if (match.Success)
            {
                var dimsStr = match.Groups[1].Value;
                var parts = dimsStr.Split(',');
                foreach (var part in parts)
                {
                    if (long.TryParse(part.Trim(), out var dim))
                    {
                        dims.Add(dim);
                    }
                }
            }
            return dims.Count > 0 ? dims : new List<long> { 1, 0, 0, 0 };
        }

        private double ExtractWeightFromInit(string initValue)
        {
            var match = System.Text.RegularExpressions.Regex.Match(initValue, @"W:\s*([0-9.]+)");
            if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var weight))
            {
                return weight;
            }
            return 0.5;
        }

        private string? ExtractDataFromInit(string initValue)
        {
            // Парсим DATA:"text" или DATA:BIN:"base64"
            var textMatch = System.Text.RegularExpressions.Regex.Match(initValue, @"DATA:\s*""([^""]+)""");
            if (textMatch.Success)
            {
                return textMatch.Groups[1].Value;
            }
            
            var binMatch = System.Text.RegularExpressions.Regex.Match(initValue, @"DATA:\s*BIN:\s*""([^""]+)""");
            if (binMatch.Success)
            {
                return "BIN:" + binMatch.Groups[1].Value;
            }

            return null;
        }

        private List<string> ExtractVerticesFromShape(string initValue)
        {
            var vertices = new List<string>();
            // Парсим VERT:[{DIM:[...]}, {DIM:[...]}]
            var match = System.Text.RegularExpressions.Regex.Match(initValue, @"VERT:\s*\[([^\]]+)\]");
            if (match.Success)
            {
                var vertsStr = match.Groups[1].Value;
                // Разделяем по }, { или просто }
                var parts = System.Text.RegularExpressions.Regex.Split(vertsStr, @"\}\s*,\s*\{");
                foreach (var part in parts)
                {
                    var clean = part.Trim().TrimStart('{').TrimEnd('}');
                    vertices.Add(clean);
                }
            }
            return vertices;
        }
    }
}
