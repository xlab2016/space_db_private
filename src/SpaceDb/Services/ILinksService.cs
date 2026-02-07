namespace SpaceDb.Services
{
    /// <summary>
    /// Service interface for managing links using Platform.Data.Doublets
    /// Provides an alternative graph storage mechanism to the traditional RocksDB/Qdrant approach
    /// </summary>
    public interface ILinksService
    {
        /// <summary>
        /// Create a new link (source -> target relationship)
        /// </summary>
        /// <param name="source">Source link ID</param>
        /// <param name="target">Target link ID</param>
        /// <returns>ID of the created link</returns>
        Task<ulong> CreateLinkAsync(ulong source, ulong target);

        /// <summary>
        /// Get link by ID
        /// </summary>
        /// <param name="linkId">Link ID</param>
        /// <returns>Link data (source, target) or null if not found</returns>
        Task<(ulong source, ulong target)?> GetLinkAsync(ulong linkId);

        /// <summary>
        /// Update existing link
        /// </summary>
        /// <param name="linkId">Link ID to update</param>
        /// <param name="newSource">New source link ID</param>
        /// <param name="newTarget">New target link ID</param>
        /// <returns>Updated link ID</returns>
        Task<ulong> UpdateLinkAsync(ulong linkId, ulong newSource, ulong newTarget);

        /// <summary>
        /// Delete link by ID
        /// </summary>
        /// <param name="linkId">Link ID to delete</param>
        Task DeleteLinkAsync(ulong linkId);

        /// <summary>
        /// Search for links by source
        /// </summary>
        /// <param name="source">Source link ID</param>
        /// <returns>Collection of link IDs that have the specified source</returns>
        Task<IEnumerable<ulong>> SearchBySourceAsync(ulong source);

        /// <summary>
        /// Search for links by target
        /// </summary>
        /// <param name="target">Target link ID</param>
        /// <returns>Collection of link IDs that have the specified target</returns>
        Task<IEnumerable<ulong>> SearchByTargetAsync(ulong target);

        /// <summary>
        /// Search for links by both source and target
        /// </summary>
        /// <param name="source">Source link ID</param>
        /// <param name="target">Target link ID</param>
        /// <returns>Collection of link IDs matching both criteria</returns>
        Task<IEnumerable<ulong>> SearchBySourceAndTargetAsync(ulong source, ulong target);

        /// <summary>
        /// Count total number of links in the storage
        /// </summary>
        /// <returns>Total link count</returns>
        Task<ulong> CountLinksAsync();

        /// <summary>
        /// Store resource-block-fragment hierarchy using links
        /// </summary>
        /// <param name="resourceId">Resource identifier</param>
        /// <param name="blockIds">Collection of block identifiers</param>
        /// <param name="fragmentIdsByBlock">Fragments grouped by block</param>
        /// <returns>Root link ID for this resource hierarchy</returns>
        Task<ulong> StoreResourceHierarchyAsync(
            long resourceId,
            IEnumerable<long> blockIds,
            Dictionary<long, IEnumerable<long>> fragmentIdsByBlock);

        /// <summary>
        /// Retrieve resource hierarchy from links
        /// </summary>
        /// <param name="resourceLinkId">Root resource link ID</param>
        /// <returns>Resource hierarchy data</returns>
        Task<ResourceHierarchy?> GetResourceHierarchyAsync(ulong resourceLinkId);

        /// <summary>
        /// Add a token to vocabulary in the graph (Vocabulary => Token*)
        /// </summary>
        /// <param name="vocabularyId">Vocabulary identifier</param>
        /// <param name="tokenId">Token identifier</param>
        /// <returns>Link ID representing the Vocabulary->Token relationship</returns>
        Task<ulong> AddTokenToVocabularyAsync(ulong vocabularyId, ulong tokenId);
    }

    /// <summary>
    /// Represents a resource hierarchy stored in links
    /// </summary>
    public class ResourceHierarchy
    {
        public long ResourceId { get; set; }
        public List<BlockHierarchy> Blocks { get; set; } = new();
    }

    /// <summary>
    /// Represents a block with its fragments
    /// </summary>
    public class BlockHierarchy
    {
        public long BlockId { get; set; }
        public List<long> FragmentIds { get; set; } = new();
    }
}
