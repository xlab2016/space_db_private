using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client.Grpc;
using SpaceDb.Models;
using AI;

namespace SpaceDb.Services;

public class SpaceDbService : ISpaceDbService
{
    private readonly IRocksDbService _rocksDbService;
    private readonly IQdrantService _qdrantService;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ILogger<SpaceDbService> _logger;
    private readonly QdrantConfig _config;
    private long _pointCounter = 0;
    private long _segmentCounter = 0;

    public SpaceDbService(
        IRocksDbService rocksDbService,
        IQdrantService qdrantService,
        IEmbeddingProvider embeddingProvider,
        ILogger<SpaceDbService> logger,
        IOptions<QdrantConfig> config)
    {
        _rocksDbService = rocksDbService ?? throw new ArgumentNullException(nameof(rocksDbService));
        _qdrantService = qdrantService ?? throw new ArgumentNullException(nameof(qdrantService));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<long?> AddPointAsync(long? fromId, Point point)
    {
        try
        {
            // Generate new ID if not provided
            if (point.Id == 0)
            {
                point.Id = Interlocked.Increment(ref _pointCounter);
            }

            // Save point to RocksDB without payload
            var pointWithoutPayload = new Point
            {
                Id = point.Id,
                Layer = point.Layer,
                Dimension = point.Dimension,
                Weight = point.Weight,
                SingularityId = point.SingularityId,
                UserId = point.UserId,
                Payload = null // Don't save payload in RocksDB
            };

            var pointKey = $"point:{point.Id}";
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var saved = await _rocksDbService.PutJsonAsync(pointKey, pointWithoutPayload, jsonOptions);

            if (!saved)
            {
                _logger.LogError("Failed to save point {PointId} to RocksDB", point.Id);
                return null;
            }

            // If payload exists, create embedding and save to Qdrant
            if (!string.IsNullOrEmpty(point.Payload))
            {
                try
                {
                    // Create embedding from payload
                    var embedding = await _embeddingProvider.CreateEmbeddingAsync(
                        _config.EmbeddingType, 
                        point.Payload, 
                        returnVectors: true);

                    if (embedding.Vector == null || embedding.Vector.Count == 0)
                    {
                        _logger.LogWarning("Failed to create embedding for point {PointId}, skipping Qdrant", point.Id);
                        return point.Id;
                    }

                    await SavePointToQdrantAsync(point, embedding, fromId);
                    _logger.LogInformation("Successfully added point {PointId} to RocksDB and Qdrant", point.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving point {PointId} to Qdrant", point.Id);
                    // Continue even if Qdrant fails
                }
            }
            else
            {
                _logger.LogInformation("Point {PointId} saved to RocksDB only (no payload)", point.Id);
            }

            // If fromId is provided, create a segment
            if (fromId.HasValue)
            {
                await AddSegmentAsync(fromId.Value, point.Id);
            }

            return point.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding point");
            return null;
        }
    }

    public async Task<long?> AddPointAsync(string key, Point point)
    {
        try
        {
            // Generate new ID if not provided
            if (point.Id == 0)
            {
                point.Id = Interlocked.Increment(ref _pointCounter);
            }

            // Save point to RocksDB without payload
            var pointWithoutPayload = new Point
            {
                Id = point.Id,
                Layer = point.Layer,
                Dimension = point.Dimension,
                Weight = point.Weight,
                SingularityId = point.SingularityId,
                UserId = point.UserId,
                Payload = null // Don't save payload in RocksDB
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var saved = await _rocksDbService.PutJsonAsync($"point:{key}", pointWithoutPayload, jsonOptions);

            if (!saved)
            {
                _logger.LogError("Failed to save point {PointId} to RocksDB with key {Key}", point.Id, key);
                return null;
            }

            // If payload exists, create embedding and save to Qdrant
            if (!string.IsNullOrEmpty(point.Payload))
            {
                try
                {
                    // Create embedding from payload
                    var embedding = await _embeddingProvider.CreateEmbeddingAsync(
                        _config.EmbeddingType, 
                        point.Payload, 
                        returnVectors: true);

                    if (embedding.Vector == null || embedding.Vector.Count == 0)
                    {
                        _logger.LogWarning("Failed to create embedding for point {PointId}, skipping Qdrant", point.Id);
                        return point.Id;
                    }

                    await SavePointToQdrantAsync(point, embedding, null);
                    _logger.LogInformation("Successfully added point {PointId} to RocksDB (key: {Key}) and Qdrant", point.Id, key);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving point {PointId} to Qdrant", point.Id);
                    // Continue even if Qdrant fails
                }
            }
            else
            {
                _logger.LogInformation("Point {PointId} saved to RocksDB only (key: {Key}, no payload)", point.Id, key);
            }

            return point.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding point with key {Key}", key);
            return null;
        }
    }

    public async Task<long?> AddPointAsync(long? fromId, Point point, AIEmbedding embedding)
    {
        try
        {
            // Generate new ID if not provided
            if (point.Id == 0)
            {
                point.Id = Interlocked.Increment(ref _pointCounter);
            }

            // Save point to RocksDB without payload
            var pointWithoutPayload = new Point
            {
                Id = point.Id,
                Layer = point.Layer,
                Dimension = point.Dimension,
                Weight = point.Weight,
                SingularityId = point.SingularityId,
                UserId = point.UserId,
                Payload = null // Don't save payload in RocksDB
            };

            var pointKey = $"point:{point.Id}";
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var saved = await _rocksDbService.PutJsonAsync(pointKey, pointWithoutPayload, jsonOptions);

            if (!saved)
            {
                _logger.LogError("Failed to save point {PointId} to RocksDB", point.Id);
                return null;
            }

            // Save to Qdrant using provided embedding
            try
            {
                if (embedding.Vector == null || embedding.Vector.Count == 0)
                {
                    _logger.LogWarning("Empty embedding for point {PointId}, skipping Qdrant", point.Id);
                    return point.Id;
                }

                await SavePointToQdrantAsync(point, embedding, fromId);
                _logger.LogInformation("Successfully added point {PointId} to RocksDB and Qdrant with provided embedding", point.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving point {PointId} to Qdrant", point.Id);
                // Continue even if Qdrant fails
            }

            // If fromId is provided, create a segment
            if (fromId.HasValue)
            {
                await AddSegmentAsync(fromId.Value, point.Id);
            }

            return point.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding point with embedding");
            return null;
        }
    }

    private async Task SavePointToQdrantAsync(Point point, AIEmbedding embedding, long? fromId)
    {
        // Ensure collection exists
        var collectionExists = await _qdrantService.CollectionExistsAsync(_config.CollectionName);
        if (!collectionExists)
        {
            var created = await _qdrantService.CreateCollectionAsync(_config.CollectionName, _config.VectorSize);
            if (!created)
            {
                _logger.LogError("Failed to create collection {CollectionName}", _config.CollectionName);
            }
        }

        // Prepare payload for Qdrant
        var payload = new Dictionary<string, object>
        {
            { "layer", point.Layer },
            { "dimension", point.Dimension },
            { "weight", point.Weight }
        };

        if (point.SingularityId.HasValue)
        {
            payload["singularityId"] = point.SingularityId.Value;
        }

        if (point.UserId.HasValue)
        {
            payload["userId"] = point.UserId.Value;
        }

        if (fromId.HasValue)
        {
            payload["fromId"] = fromId.Value;
        }

        // Convert payload
        var qdrantPayload = new Dictionary<string, Value>();
        foreach (var kvp in payload)
        {
            qdrantPayload[kvp.Key] = ConvertToValue(kvp.Value);
        }

        // Create Qdrant point with proper PointId
        var vectors = new Dictionary<string, float[]>
        {
            { "", embedding.Vector.ToArray() }
        };
        
        var pointId = new PointId { Num = (ulong)point.Id };
        
        var qdrantPoint = new PointStruct
        {
            Id = pointId,
            Vectors = vectors
        };
        
        // Set payload field by field
        foreach (var kvp in qdrantPayload)
        {
            qdrantPoint.Payload[kvp.Key] = kvp.Value;
        }

        // Upsert to Qdrant
        await _qdrantService.UpsertPointStructsAsync(_config.CollectionName, new[] { qdrantPoint });
    }

    public async Task<long?> AddSegmentAsync(long? fromId, long? toId)
    {
        try
        {
            if (!fromId.HasValue || !toId.HasValue)
            {
                _logger.LogWarning("Segment requires both fromId and toId");
                return null;
            }

            var segmentId = Interlocked.Increment(ref _segmentCounter);
            
            var segment = new Segment
            {
                Id = segmentId,
                FromId = fromId,
                ToId = toId,
                Layer = 0,
                Dimension = 0,
                Weight = 0,
                SingularityId = null
            };

            // Save inbound segment: segment:in:{fromId}:{toId}
            var inSegmentKey = $"segment:in:{fromId}:{toId}";
            // Save outbound segment: segment:out:{toId}:{fromId}
            var outSegmentKey = $"segment:out:{toId}:{fromId}";
            
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            
            // Save both directions
            var inSaved = await _rocksDbService.PutJsonAsync(inSegmentKey, segment, jsonOptions);
            var outSaved = await _rocksDbService.PutJsonAsync(outSegmentKey, segment, jsonOptions);

            if (!inSaved || !outSaved)
            {
                _logger.LogError("Failed to save segment {SegmentId} to RocksDB", segmentId);
                return null;
            }

            _logger.LogInformation("Successfully added segment {SegmentId} from {FromId} to {ToId}", segmentId, fromId, toId);
            return segmentId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding segment");
            return null;
        }
    }

    public async Task<IEnumerable<object>> SearchAsync(
        string query,
        long? singularityId = null,
        int? dimension = null,
        int? layer = null,
        uint limit = 10,
        float scoreThreshold = 0.0f)
    {
        try
        {
            if (string.IsNullOrEmpty(query))
            {
                _logger.LogWarning("Query text is empty");
                return Enumerable.Empty<object>();
            }

            var embedding = await _embeddingProvider.CreateEmbeddingAsync(_config.EmbeddingType, query, returnVectors: true);
            
            if (embedding.Vector == null || embedding.Vector.Count == 0)
            {
                _logger.LogError("Failed to create embedding for search query");
                return Enumerable.Empty<object>();
            }

            var vector = embedding.Vector.ToArray();
            var filter = BuildFilter(singularityId, dimension, layer);

            return await _qdrantService.SearchWithFilterAsync(_config.CollectionName, vector, filter, limit, scoreThreshold);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during search");
            return Enumerable.Empty<object>();
        }
    }

    public async Task<IEnumerable<object>> SearchWithEmbeddingAsync(
        AIEmbedding queryEmbedding,
        long? singularityId = null,
        int? dimension = null,
        int? layer = null,
        uint limit = 10,
        float scoreThreshold = 0.0f)
    {
        try
        {
            if (queryEmbedding.Vector == null || queryEmbedding.Vector.Count == 0)
            {
                _logger.LogWarning("Empty or invalid embedding provided for search");
                return Enumerable.Empty<object>();
            }

            var vector = queryEmbedding.Vector.ToArray();
            var filter = BuildFilter(singularityId, dimension, layer);

            return await _qdrantService.SearchWithFilterAsync(_config.CollectionName, vector, filter, limit, scoreThreshold);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during search with embedding");
            return Enumerable.Empty<object>();
        }
    }

    public async Task<bool> UpdatePointAsync(Point point)
    {
        try
        {
            if (point.Id == 0)
            {
                _logger.LogWarning("Cannot update point with ID 0");
                return false;
            }

            // Update in RocksDB
            var pointWithoutPayload = new Point
            {
                Id = point.Id,
                Layer = point.Layer,
                Dimension = point.Dimension,
                Weight = point.Weight,
                SingularityId = point.SingularityId,
                UserId = point.UserId,
                Payload = null
            };

            var pointKey = $"point:{point.Id}";
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var saved = await _rocksDbService.PutJsonAsync(pointKey, pointWithoutPayload, jsonOptions);

            if (!saved)
            {
                _logger.LogError("Failed to update point {PointId} in RocksDB", point.Id);
                return false;
            }

            // If payload exists, create embedding and update in Qdrant
            if (!string.IsNullOrEmpty(point.Payload))
            {
                try
                {
                    var embedding = await _embeddingProvider.CreateEmbeddingAsync(
                        _config.EmbeddingType,
                        point.Payload,
                        returnVectors: true);

                    if (embedding.Vector == null || embedding.Vector.Count == 0)
                    {
                        _logger.LogWarning("Failed to create embedding for point {PointId}", point.Id);
                        return true; // RocksDB updated successfully
                    }

                    await SavePointToQdrantAsync(point, embedding, null);
                    _logger.LogInformation("Successfully updated point {PointId} in RocksDB and Qdrant", point.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating point {PointId} in Qdrant", point.Id);
                }
            }
            else
            {
                // Delete from Qdrant if no payload
                await _qdrantService.DeletePointsAsync(_config.CollectionName, new[] { point.Id.ToString() });
                _logger.LogInformation("Point {PointId} updated in RocksDB only (no payload, deleted from Qdrant)", point.Id);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating point");
            return false;
        }
    }

    public async Task<bool> UpdatePointAsync(Point point, AIEmbedding embedding)
    {
        try
        {
            if (point.Id == 0)
            {
                _logger.LogWarning("Cannot update point with ID 0");
                return false;
            }

            // Update in RocksDB
            var pointWithoutPayload = new Point
            {
                Id = point.Id,
                Layer = point.Layer,
                Dimension = point.Dimension,
                Weight = point.Weight,
                SingularityId = point.SingularityId,
                UserId = point.UserId,
                Payload = null
            };

            var pointKey = $"point:{point.Id}";
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var saved = await _rocksDbService.PutJsonAsync(pointKey, pointWithoutPayload, jsonOptions);

            if (!saved)
            {
                _logger.LogError("Failed to update point {PointId} in RocksDB", point.Id);
                return false;
            }

            // Update in Qdrant with provided embedding
            try
            {
                if (embedding.Vector == null || embedding.Vector.Count == 0)
                {
                    _logger.LogWarning("Empty embedding for point {PointId}", point.Id);
                    return true;
                }

                await SavePointToQdrantAsync(point, embedding, null);
                _logger.LogInformation("Successfully updated point {PointId} in RocksDB and Qdrant with provided embedding", point.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating point {PointId} in Qdrant", point.Id);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating point with embedding");
            return false;
        }
    }

    public async Task<bool> DeletePointAsync(long pointId)
    {
        try
        {
            var pointKey = $"point:{pointId}";
            
            // Delete from RocksDB
            var deleted = await _rocksDbService.DeleteAsync(pointKey);
            
            if (deleted)
            {
                // Delete from Qdrant
                await _qdrantService.DeletePointsAsync(_config.CollectionName, new[] { pointId.ToString() });
                _logger.LogInformation("Successfully deleted point {PointId} from RocksDB and Qdrant", pointId);
            }
            else
            {
                _logger.LogWarning("Point {PointId} not found in RocksDB", pointId);
            }
            
            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting point {PointId}", pointId);
            return false;
        }
    }

    public async Task<bool> DeleteSegmentAsync(long? fromId, long? toId)
    {
        try
        {
            if (!fromId.HasValue || !toId.HasValue)
            {
                _logger.LogWarning("Both fromId and toId are required to delete segment");
                return false;
            }

            // Delete inbound segment
            var inSegmentKey = $"segment:in:{fromId}:{toId}";
            var outSegmentKey = $"segment:out:{toId}:{fromId}";
            
            var inDeleted = await _rocksDbService.DeleteAsync(inSegmentKey);
            var outDeleted = await _rocksDbService.DeleteAsync(outSegmentKey);

            if (inDeleted && outDeleted)
            {
                _logger.LogInformation("Successfully deleted segment from {FromId} to {ToId}", fromId, toId);
                return true;
            }
            else
            {
                _logger.LogWarning("Segment from {FromId} to {ToId} not found", fromId, toId);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting segment");
            return false;
        }
    }

    private Dictionary<string, object>? BuildFilter(long? singularityId, int? dimension, int? layer)
    {
        if (!singularityId.HasValue && !dimension.HasValue && !layer.HasValue)
        {
            return null;
        }

        var filter = new Dictionary<string, object>();
        
        if (singularityId.HasValue)
        {
            filter["singularityId"] = singularityId.Value;
        }
        
        if (dimension.HasValue)
        {
            filter["dimension"] = dimension.Value;
        }
        
        if (layer.HasValue)
        {
            filter["layer"] = layer.Value;
        }

        return filter;
    }

    public async Task<bool> PointExistsAsync(long pointId)
    {
        try
        {
            var pointKey = $"point:{pointId}";
            return await _rocksDbService.ExistsAsync(pointKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if point {PointId} exists", pointId);
            return false;
        }
    }

    public async Task<long?> FindOrCreateVocabularyAsync(long? singularityId, int? userId = null)
    {
        try
        {
            if (!singularityId.HasValue)
            {
                _logger.LogWarning("SingularityId is required to find or create vocabulary");
                return null;
            }

            // Search for existing vocabulary point by key "vocabulary"
            var vocabularyKey = "vocabulary";
            var vocabularyPoint = await _rocksDbService.GetJsonAsync<Point>($"point:{vocabularyKey}");

            if (vocabularyPoint != null)
            {
                _logger.LogInformation("Found existing vocabulary {VocabularyId} for singularity {SingularityId}",
                    vocabularyPoint.Id, singularityId);
                return vocabularyPoint.Id;
            }

            // Create new vocabulary point with key "Vocabulary"
            _logger.LogInformation("Creating new vocabulary for singularity {SingularityId}", singularityId);
            var newVocabularyPoint = new Point
            {
                Id = 0, // Will be auto-generated
                Layer = 2, // Vocabulary is at concept layer
                Dimension = 2, // Vocabulary dimension
                Weight = 1.0,
                SingularityId = singularityId,
                UserId = userId,
                Payload = "Vocabulary" // Mark as Vocabulary
            };

            var createdVocabularyId = await AddPointAsync(vocabularyKey, newVocabularyPoint);
            if (createdVocabularyId.HasValue)
            {
                _logger.LogInformation("Created vocabulary {VocabularyId} for singularity {SingularityId}",
                    createdVocabularyId.Value, singularityId);
            }

            return createdVocabularyId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding or creating vocabulary for singularity {SingularityId}", singularityId);
            return null;
        }
    }

    public async Task<AddTokenToVocabularyResult?> AddTokenToVocabularyAsync(
        SpaceCompiler.Models.Token token,
        long? singularityId = null,
        int? userId = null)
    {
        try
        {
            _logger.LogInformation("Adding token '{Content}' to vocabulary for singularity {SingularityId}",
                token.Content, singularityId);

            if (!singularityId.HasValue)
            {
                _logger.LogError("SingularityId is required to add token to vocabulary");
                return null;
            }

            // Find or create vocabulary for this singularity
            var vocabularyId = await FindOrCreateVocabularyAsync(singularityId, userId);
            if (!vocabularyId.HasValue)
            {
                _logger.LogError("Failed to find or create vocabulary for singularity {SingularityId}", singularityId);
                return null;
            }

            // Search for existing token by key vocabulary:{token.Content.ToLower()}
            var tokenKey = $"vocabulary:{token.Content.ToLower()}";
            var existingTokenPoint = await _rocksDbService.GetJsonAsync<Point>($"point:{tokenKey}");

            long tokenPointId;
            if (existingTokenPoint != null)
            {
                _logger.LogInformation("Found existing token '{Content}' with ID {TokenId}",
                    token.Content, existingTokenPoint.Id);
                tokenPointId = existingTokenPoint.Id;
            }
            else
            {
                // Create new token point
                _logger.LogInformation("Creating new token point for '{Content}'", token.Content);
                
                // Serialize token to JSON for payload
                var tokenJson = System.Text.Json.JsonSerializer.Serialize(token);
                
                var tokenPoint = new Point
                {
                    Id = 0, // Will be auto-generated
                    Layer = 1, // Token is at extracted layer
                    Dimension = 2, // Token dimension
                    Weight = 0.5,
                    SingularityId = singularityId,
                    UserId = userId,
                    Payload = tokenJson
                };

                var createdTokenId = await AddPointAsync(tokenKey, tokenPoint);
                if (!createdTokenId.HasValue)
                {
                    _logger.LogError("Failed to create token point");
                    return null;
                }
                tokenPointId = createdTokenId.Value;
            }

            // Create a segment from vocabulary to token (Vocabulary => Token*)
            // Check if segment already exists
            var segmentKey = $"segment:in:{vocabularyId.Value}:{tokenPointId}";
            var existingSegment = await _rocksDbService.ExistsAsync(segmentKey);
            
            long? segmentId;
            if (existingSegment)
            {
                _logger.LogInformation("Segment from vocabulary {VocabularyId} to token {TokenId} already exists",
                    vocabularyId.Value, tokenPointId);
                // Get existing segment ID
                var segment = await _rocksDbService.GetJsonAsync<Segment>(segmentKey);
                segmentId = segment?.Id;
            }
            else
            {
                segmentId = await AddSegmentAsync(vocabularyId.Value, tokenPointId);
            }

            if (segmentId.HasValue)
            {
                _logger.LogInformation("Successfully added token '{Content}' (ID: {TokenId}) to vocabulary {VocabularyId} with segment {SegmentId}",
                    token.Content, tokenPointId, vocabularyId.Value, segmentId);

                return new AddTokenToVocabularyResult
                {
                    VocabularyId = vocabularyId.Value,
                    TokenId = tokenPointId,
                    SegmentId = segmentId.Value
                };
            }
            else
            {
                _logger.LogWarning("Failed to create segment from vocabulary {VocabularyId} to token {TokenId}",
                    vocabularyId.Value, tokenPointId);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding token to vocabulary");
            return null;
        }
    }

    private Value ConvertToValue(object? value)
    {
        return value switch
        {
            string s => new Value { StringValue = s },
            int i => new Value { IntegerValue = i },
            long l => new Value { IntegerValue = l },
            float f => new Value { DoubleValue = f },
            double d => new Value { DoubleValue = d },
            bool b => new Value { BoolValue = b },
            null => new Value { NullValue = new NullValue() },
            _ => new Value { StringValue = value.ToString() ?? string.Empty }
        };
    }
}

