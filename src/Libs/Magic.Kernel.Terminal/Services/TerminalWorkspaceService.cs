using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Kernel.Core;
using Magic.Kernel.Terminal.Models;

namespace Magic.Kernel.Terminal.Services;

public sealed class TerminalWorkspaceService
{
    private static readonly string[] SensitiveMarkers = ["password", "secret", "token", "key", "credential", "jwt", "auth"];

    private static readonly JsonSerializerOptions IndentedWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactWriteOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public List<ExecutionUnitItem> LoadExecutionUnits(string spaceConfigPath)
    {
        var root = LoadJsonObject(spaceConfigPath);
        var list = new List<ExecutionUnitItem>();
        var units = root["executionUnits"] as JsonArray;
        if (units is null)
        {
            return list;
        }

        foreach (var item in units)
        {
            if (item is not JsonObject node)
            {
                continue;
            }

            var path = node["path"]?.GetValue<string>() ?? string.Empty;
            var instanceCount = node["instanceCount"]?.GetValue<int>() ?? 1;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            list.Add(new ExecutionUnitItem
            {
                Path = path,
                InstanceCount = instanceCount > 0 ? instanceCount : 1
            });
        }

        return list.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void SaveExecutionUnits(string spaceConfigPath, IEnumerable<ExecutionUnitItem> units)
    {
        var root = LoadJsonObject(spaceConfigPath);
        var array = new JsonArray();
        foreach (var unit in units.Where(x => !string.IsNullOrWhiteSpace(x.Path)))
        {
            array.Add(new JsonObject
            {
                ["path"] = unit.Path.Trim(),
                ["instanceCount"] = unit.InstanceCount > 0 ? unit.InstanceCount : 1
            });
        }

        root["executionUnits"] = array;
        SaveJsonObject(spaceConfigPath, root);
    }

    public List<VaultItem> LoadVaultItems(string? vaultPath = null)
    {
        var resolvedPath = ResolveVaultPath(vaultPath);
        var root = LoadJsonArray(resolvedPath);
        var result = new List<VaultItem>();
        foreach (var node in root)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var store = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (item["store"] is JsonObject storeNode)
            {
                foreach (var p in storeNode)
                {
                    store[p.Key] = JsonNodeToObject(p.Value);
                }
            }

            result.Add(new VaultItem
            {
                Program = item["program"]?.GetValue<string>() ?? string.Empty,
                System = item["system"]?.GetValue<string>() ?? string.Empty,
                Module = item["module"]?.GetValue<string>() ?? string.Empty,
                InstanceIndex = item["instanceIndex"]?.GetValue<int>() ?? 0,
                Store = store
            });
        }

        return result;
    }

    public void SaveVaultItems(IEnumerable<VaultItem> items, string? vaultPath = null)
    {
        var resolvedPath = ResolveVaultPath(vaultPath);
        var root = new JsonArray();
        foreach (var item in items)
        {
            var storeObj = new JsonObject();
            foreach (var kv in item.Store)
            {
                storeObj[kv.Key] = ObjectToJsonNode(kv.Value);
            }

            root.Add(new JsonObject
            {
                ["program"] = item.Program,
                ["system"] = item.System,
                ["module"] = item.Module,
                ["instanceIndex"] = item.InstanceIndex,
                ["store"] = storeObj
            });
        }

        var dir = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(resolvedPath, root.ToJsonString(IndentedWriteOptions));
    }

    /// <summary>Всегда полные значения в памяти; маска только в UI до «Показать».</summary>
    public List<VaultStoreRow> ToRows(VaultItem item)
    {
        return item.Store
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                var sensitive = IsSensitiveKey(x.Key);
                var value = StoreValueToEditString(x.Value);
                return new VaultStoreRow
                {
                    Key = x.Key,
                    Value = value,
                    IsSensitive = sensitive,
                    SensitiveRevealed = false
                };
            })
            .ToList();
    }

    public void ApplyRows(VaultItem item, IEnumerable<VaultStoreRow> rows)
    {
        item.Store.Clear();
        foreach (var row in rows.Where(x => !string.IsNullOrWhiteSpace(x.Key)))
        {
            item.Store[row.Key.Trim()] = row.Value;
        }
    }

    private static string ResolveVaultPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(path);
        }

        return SpaceEnvironment.GetFilePath("vault.json");
    }

    private static JsonObject LoadJsonObject(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private static JsonArray LoadJsonArray(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonArray();
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonArray();
        }

        return JsonNode.Parse(text) as JsonArray ?? new JsonArray();
    }

    private static void SaveJsonObject(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, root.ToJsonString(IndentedWriteOptions));
    }

    private static bool IsSensitiveKey(string key)
        => SensitiveMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static object? JsonNodeToObject(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            return obj;
        }

        if (node is JsonArray arr)
        {
            return arr;
        }

        if (node is not JsonValue jv)
        {
            return node.ToJsonString();
        }

        var token = jv.ToJsonString();
        using var doc = JsonDocument.Parse(token);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => doc.RootElement.TryGetInt64(out var l) ? l : doc.RootElement.GetDouble(),
            JsonValueKind.String => doc.RootElement.GetString() ?? "",
            _ => token
        };
    }

    private static JsonNode? ObjectToJsonNode(object? value)
    {
        switch (value)
        {
            case null:
                return JsonNode.Parse("null");
            case JsonObject o:
                return o.DeepClone();
            case JsonArray a:
                return a.DeepClone();
            case string s:
                try
                {
                    return JsonNode.Parse(s);
                }
                catch (JsonException)
                {
                    return JsonValue.Create(s);
                }
            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case double d:
                return JsonValue.Create(d);
            case float f:
                return JsonValue.Create(f);
            case decimal m:
                return JsonValue.Create(m);
            default:
                return JsonValue.Create(value.ToString() ?? "");
        }
    }

    private static string StoreValueToEditString(object? v)
    {
        if (v is null)
        {
            return "";
        }

        if (v is JsonObject or JsonArray)
        {
            return ((JsonNode)v).ToJsonString(CompactWriteOptions);
        }

        if (v is bool b)
        {
            return b ? "true" : "false";
        }

        if (v is IFormattable fmt && v is not string)
        {
            return fmt.ToString(null, CultureInfo.InvariantCulture);
        }

        return v.ToString() ?? "";
    }
}
