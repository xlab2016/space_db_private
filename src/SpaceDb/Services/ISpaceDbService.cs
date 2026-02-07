using AI;
using SpaceDb.Models;

namespace SpaceDb.Services;

/// <summary>
/// Result of adding token to vocabulary
/// </summary>
public class AddTokenToVocabularyResult
{
    /// <summary>
    /// Vocabulary identifier (point ID)
    /// </summary>
    public long VocabularyId { get; set; }

    /// <summary>
    /// Token identifier (point ID)
    /// </summary>
    public long TokenId { get; set; }

    /// <summary>
    /// Segment ID representing the Vocabulary->Token relationship
    /// </summary>
    public long SegmentId { get; set; }
}

public interface ISpaceDbService
{
    Task<long?> AddPointAsync(long? fromId, Point point);
    Task<long?> AddPointAsync(long? fromId, Point point, AIEmbedding embedding);
    /// <summary>
    /// Add a point with a custom key
    /// </summary>
    /// <param name="key">Custom key for storing the point in RocksDB</param>
    /// <param name="point">Point to add</param>
    /// <returns>Point ID</returns>
    Task<long?> AddPointAsync(string key, Point point);
    Task<bool> UpdatePointAsync(Point point);
    Task<bool> UpdatePointAsync(Point point, AIEmbedding embedding);
    Task<bool> DeletePointAsync(long pointId);
    
    Task<long?> AddSegmentAsync(long? fromId, long? toId);
    Task<bool> DeleteSegmentAsync(long? fromId, long? toId);
    
    Task<IEnumerable<object>> SearchAsync(
        string query,
        long? singularityId = null,
        int? dimension = null,
        int? layer = null,
        uint limit = 10,
        float scoreThreshold = 0.0f);
    
    Task<IEnumerable<object>> SearchWithEmbeddingAsync(
        AIEmbedding queryEmbedding,
        long? singularityId = null,
        int? dimension = null,
        int? layer = null,
        uint limit = 10,
        float scoreThreshold = 0.0f);
    
    /// <summary>
    /// Add a token to vocabulary in the graph (Vocabulary => Token*)
    /// Finds or creates vocabulary point for the singularity, creates token point if needed, then creates a segment relationship
    /// </summary>
    /// <param name="token">Token object to add</param>
    /// <param name="singularityId">Singularity namespace (required, Vocabulary is unique per singularity)</param>
    /// <param name="userId">User ID</param>
    /// <returns>Result with vocabulary ID, token ID, and segment ID</returns>
    Task<AddTokenToVocabularyResult?> AddTokenToVocabularyAsync(
        SpaceCompiler.Models.Token token,
        long? singularityId = null,
        int? userId = null);
    
    /// <summary>
    /// Find or create vocabulary point for a singularity
    /// Vocabulary is unique per singularity (dimension=2, layer=2)
    /// </summary>
    /// <param name="singularityId">Singularity namespace</param>
    /// <param name="userId">User ID</param>
    /// <returns>Vocabulary point ID</returns>
    Task<long?> FindOrCreateVocabularyAsync(long? singularityId, int? userId = null);
    
    /// <summary>
    /// Check if a point exists by ID
    /// </summary>
    /// <param name="pointId">Point ID to check</param>
    /// <returns>True if point exists</returns>
    Task<bool> PointExistsAsync(long pointId);
}

