using Platform.Data;
using Platform.Data.Doublets;
using Platform.Data.Doublets.Memory.United.Generic;
using Platform.Memory;

namespace SpaceDb.Services
{
    /// <summary>
    /// Implementation of links storage using Platform.Data.Doublets
    /// Provides an alternative graph storage mechanism for resource-block-fragment hierarchies
    /// </summary>
    public class LinksService : ILinksService, IDisposable
    {
        private readonly ILinks<ulong> _links;
        private readonly ILogger<LinksService> _logger;
        private readonly string _dbPath;
        private bool _disposed = false;

        // Constants for special link types (using well-known IDs)
        private const ulong ResourceType = 1;
        private const ulong BlockType = 2;
        private const ulong FragmentType = 3;
        private const ulong ContainsRelation = 4;
        private const ulong VocabularyType = 5;
        private const ulong TokenType = 6;

        public LinksService(string dbPath, ILogger<LinksService> logger)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Create directory if it doesn't exist
            Directory.CreateDirectory(_dbPath);

            var linksFilePath = Path.Combine(_dbPath, "db.links");

            try
            {
                // Initialize Platform.Data.Doublets memory-mapped links storage
                var memory = new FileMappedResizableDirectMemory(linksFilePath);
                _links = new UnitedMemoryLinks<ulong>(memory);

                _logger.LogInformation("LinksService initialized at path: {DbPath}", _dbPath);

                // Initialize well-known link types if they don't exist
                InitializeWellKnownTypes();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize LinksService at path: {DbPath}", _dbPath);
                throw;
            }
        }

        /// <summary>
        /// Initialize well-known link types used for hierarchical structures
        /// </summary>
        private void InitializeWellKnownTypes()
        {
            try
            {
                // Ensure well-known types exist
                // ResourceType = 1, BlockType = 2, FragmentType = 3, ContainsRelation = 4
                // These are created as self-referencing links (type -> type)

                var constants = _links.Constants;
                var any = constants.Any;

                // Check if types are already initialized
                if (_links.Count() < 6)
                {
                    // Create type markers as self-referencing links
                    EnsureLinkExists(ResourceType, ResourceType, ResourceType);
                    EnsureLinkExists(BlockType, BlockType, BlockType);
                    EnsureLinkExists(FragmentType, FragmentType, FragmentType);
                    EnsureLinkExists(ContainsRelation, ContainsRelation, ContainsRelation);
                    EnsureLinkExists(VocabularyType, VocabularyType, VocabularyType);
                    EnsureLinkExists(TokenType, TokenType, TokenType);

                    _logger.LogInformation("Initialized well-known link types");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not initialize well-known types, they may already exist");
            }
        }

        /// <summary>
        /// Ensure a link exists with the given ID and structure
        /// </summary>
        private void EnsureLinkExists(ulong id, ulong source, ulong target)
        {
            try
            {
                var existing = _links.GetLink(id);
                if (existing[_links.Constants.IndexPart] == 0)
                {
                    // Link doesn't exist, create and update it
                    var created = _links.Create();
                    if (created != id)
                    {
                        // If ID mismatch, update to match
                        _links.Update(created, source, target);
                    }
                }
            }
            catch
            {
                // If GetLink throws, just log warning
                _logger.LogWarning("Could not ensure link {Id} exists", id);
            }
        }

        public Task<ulong> CreateLinkAsync(ulong source, ulong target)
        {
            try
            {
                var linkId = _links.Create();
                _links.Update(linkId, source, target);
                _logger.LogDebug("Created link {LinkId}: {Source} -> {Target}", linkId, source, target);
                return Task.FromResult(linkId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create link: {Source} -> {Target}", source, target);
                throw;
            }
        }

        public Task<(ulong source, ulong target)?> GetLinkAsync(ulong linkId)
        {
            try
            {
                var link = _links.GetLink(linkId);
                var constants = _links.Constants;

                if (link[constants.IndexPart] == 0)
                {
                    return Task.FromResult<(ulong, ulong)?>(null);
                }

                var source = link[constants.SourcePart];
                var target = link[constants.TargetPart];

                _logger.LogDebug("Retrieved link {LinkId}: {Source} -> {Target}", linkId, source, target);
                return Task.FromResult<(ulong, ulong)?>((source, target));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get link: {LinkId}", linkId);
                return Task.FromResult<(ulong, ulong)?>(null);
            }
        }

        public Task<ulong> UpdateLinkAsync(ulong linkId, ulong newSource, ulong newTarget)
        {
            try
            {
                _links.Update(linkId, newSource, newTarget);
                _logger.LogDebug("Updated link {LinkId} to {Source} -> {Target}", linkId, newSource, newTarget);
                return Task.FromResult(linkId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update link {LinkId}", linkId);
                throw;
            }
        }

        public Task DeleteLinkAsync(ulong linkId)
        {
            try
            {
                _links.Delete(linkId);
                _logger.LogDebug("Deleted link {LinkId}", linkId);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete link {LinkId}", linkId);
                throw;
            }
        }

        public Task<IEnumerable<ulong>> SearchBySourceAsync(ulong source)
        {
            try
            {
                var results = new List<ulong>();
                var constants = _links.Constants;
                var any = constants.Any;

                _links.Each(link =>
                {
                    if (link[constants.SourcePart] == source)
                    {
                        results.Add(link[constants.IndexPart]);
                    }
                    return constants.Continue;
                }, new Link<ulong>(any, source, any));

                _logger.LogDebug("Found {Count} links with source {Source}", results.Count, source);
                return Task.FromResult<IEnumerable<ulong>>(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search by source: {Source}", source);
                return Task.FromResult<IEnumerable<ulong>>(Array.Empty<ulong>());
            }
        }

        public Task<IEnumerable<ulong>> SearchByTargetAsync(ulong target)
        {
            try
            {
                var results = new List<ulong>();
                var constants = _links.Constants;
                var any = constants.Any;

                _links.Each(link =>
                {
                    if (link[constants.TargetPart] == target)
                    {
                        results.Add(link[constants.IndexPart]);
                    }
                    return constants.Continue;
                }, new Link<ulong>(any, any, target));

                _logger.LogDebug("Found {Count} links with target {Target}", results.Count, target);
                return Task.FromResult<IEnumerable<ulong>>(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search by target: {Target}", target);
                return Task.FromResult<IEnumerable<ulong>>(Array.Empty<ulong>());
            }
        }

        public Task<IEnumerable<ulong>> SearchBySourceAndTargetAsync(ulong source, ulong target)
        {
            try
            {
                var results = new List<ulong>();
                var constants = _links.Constants;

                _links.Each(link =>
                {
                    if (link[constants.SourcePart] == source && link[constants.TargetPart] == target)
                    {
                        results.Add(link[constants.IndexPart]);
                    }
                    return constants.Continue;
                }, new Link<ulong>(constants.Any, source, target));

                _logger.LogDebug("Found {Count} links with source {Source} and target {Target}",
                    results.Count, source, target);
                return Task.FromResult<IEnumerable<ulong>>(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search by source and target: {Source}, {Target}", source, target);
                return Task.FromResult<IEnumerable<ulong>>(Array.Empty<ulong>());
            }
        }

        public Task<ulong> CountLinksAsync()
        {
            try
            {
                var count = _links.Count();
                _logger.LogDebug("Total link count: {Count}", count);
                return Task.FromResult(count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to count links");
                return Task.FromResult<ulong>(0);
            }
        }

        public async Task<ulong> StoreResourceHierarchyAsync(
            long resourceId,
            IEnumerable<long> blockIds,
            Dictionary<long, IEnumerable<long>> fragmentIdsByBlock)
        {
            try
            {
                _logger.LogInformation("Storing resource hierarchy for resource {ResourceId}", resourceId);

                // Create resource node: (ResourceType, resourceId)
                // We encode the actual ID in a link that points to ResourceType
                var resourceIdLink = await CreateLinkAsync(ResourceType, (ulong)resourceId);
                var resourceNode = await CreateLinkAsync(ResourceType, resourceIdLink);

                _logger.LogDebug("Created resource node {ResourceNode} for ID {ResourceId}",
                    resourceNode, resourceId);

                // Process each block
                foreach (var blockId in blockIds)
                {
                    // Create block node: (BlockType, blockId)
                    var blockIdLink = await CreateLinkAsync(BlockType, (ulong)blockId);
                    var blockNode = await CreateLinkAsync(BlockType, blockIdLink);

                    // Link resource to block: (resourceNode, ContainsRelation, blockNode)
                    var resourceToBlock = await CreateLinkAsync(resourceNode, blockNode);
                    var resourceBlockRelation = await CreateLinkAsync(resourceToBlock, ContainsRelation);

                    _logger.LogDebug("Created block node {BlockNode} for ID {BlockId}, linked to resource",
                        blockNode, blockId);

                    // Process fragments for this block
                    if (fragmentIdsByBlock.TryGetValue(blockId, out var fragmentIds))
                    {
                        foreach (var fragmentId in fragmentIds)
                        {
                            // Create fragment node: (FragmentType, fragmentId)
                            var fragmentIdLink = await CreateLinkAsync(FragmentType, (ulong)fragmentId);
                            var fragmentNode = await CreateLinkAsync(FragmentType, fragmentIdLink);

                            // Link block to fragment: (blockNode, ContainsRelation, fragmentNode)
                            var blockToFragment = await CreateLinkAsync(blockNode, fragmentNode);
                            var blockFragmentRelation = await CreateLinkAsync(blockToFragment, ContainsRelation);

                            _logger.LogDebug("Created fragment node {FragmentNode} for ID {FragmentId}, linked to block",
                                fragmentNode, fragmentId);
                        }
                    }
                }

                _logger.LogInformation("Successfully stored resource hierarchy {ResourceNode} with {BlockCount} blocks",
                    resourceNode, blockIds.Count());

                return resourceNode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store resource hierarchy for resource {ResourceId}", resourceId);
                throw;
            }
        }

        public async Task<ResourceHierarchy?> GetResourceHierarchyAsync(ulong resourceLinkId)
        {
            try
            {
                _logger.LogInformation("Retrieving resource hierarchy for link {ResourceLinkId}", resourceLinkId);

                // Get the resource link
                var resourceLink = await GetLinkAsync(resourceLinkId);
                if (!resourceLink.HasValue)
                {
                    _logger.LogWarning("Resource link {ResourceLinkId} not found", resourceLinkId);
                    return null;
                }

                // Extract resource ID from the structure: (ResourceType, resourceIdLink)
                var resourceIdLink = await GetLinkAsync(resourceLink.Value.target);
                if (!resourceIdLink.HasValue)
                {
                    _logger.LogWarning("Resource ID link not found for {ResourceLinkId}", resourceLinkId);
                    return null;
                }

                var hierarchy = new ResourceHierarchy
                {
                    ResourceId = (long)resourceIdLink.Value.target
                };

                // Find all blocks connected to this resource
                var resourceLinks = await SearchBySourceAsync(resourceLinkId);

                foreach (var linkId in resourceLinks)
                {
                    var link = await GetLinkAsync(linkId);
                    if (!link.HasValue) continue;

                    // Check if this is a block relation
                    var relationLink = await SearchBySourceAndTargetAsync(linkId, ContainsRelation);
                    if (!relationLink.Any()) continue;

                    var blockNode = link.Value.target;
                    var blockLink = await GetLinkAsync(blockNode);
                    if (!blockLink.HasValue) continue;

                    var blockIdLink = await GetLinkAsync(blockLink.Value.target);
                    if (!blockIdLink.HasValue) continue;

                    var blockHierarchy = new BlockHierarchy
                    {
                        BlockId = (long)blockIdLink.Value.target
                    };

                    // Find all fragments connected to this block
                    var blockLinks = await SearchBySourceAsync(blockNode);

                    foreach (var blockLinkId in blockLinks)
                    {
                        var blLink = await GetLinkAsync(blockLinkId);
                        if (!blLink.HasValue) continue;

                        // Check if this is a fragment relation
                        var fragRelationLink = await SearchBySourceAndTargetAsync(blockLinkId, ContainsRelation);
                        if (!fragRelationLink.Any()) continue;

                        var fragmentNode = blLink.Value.target;
                        var fragmentLink = await GetLinkAsync(fragmentNode);
                        if (!fragmentLink.HasValue) continue;

                        var fragmentIdLink = await GetLinkAsync(fragmentLink.Value.target);
                        if (!fragmentIdLink.HasValue) continue;

                        blockHierarchy.FragmentIds.Add((long)fragmentIdLink.Value.target);
                    }

                    hierarchy.Blocks.Add(blockHierarchy);
                }

                _logger.LogInformation("Retrieved resource hierarchy: Resource {ResourceId} with {BlockCount} blocks",
                    hierarchy.ResourceId, hierarchy.Blocks.Count);

                return hierarchy;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve resource hierarchy for link {ResourceLinkId}", resourceLinkId);
                return null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        // Platform.Data.Doublets ILinks implements IDisposable
                        (_links as IDisposable)?.Dispose();
                        _logger.LogInformation("LinksService disposed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error disposing LinksService");
                    }
                }

                _disposed = true;
            }
        }

        public async Task<ulong> AddTokenToVocabularyAsync(ulong vocabularyId, ulong tokenId)
        {
            try
            {
                _logger.LogInformation("Adding token {TokenId} to vocabulary {VocabularyId}", tokenId, vocabularyId);

                // Create vocabulary node: (VocabularyType, vocabularyId)
                var vocabularyIdLink = await CreateLinkAsync(VocabularyType, vocabularyId);
                var vocabularyNode = await CreateLinkAsync(VocabularyType, vocabularyIdLink);

                // Create token node: (TokenType, tokenId)
                var tokenIdLink = await CreateLinkAsync(TokenType, tokenId);
                var tokenNode = await CreateLinkAsync(TokenType, tokenIdLink);

                // Link vocabulary to token: (vocabularyNode, ContainsRelation, tokenNode)
                var vocabularyToToken = await CreateLinkAsync(vocabularyNode, tokenNode);
                var vocabularyTokenRelation = await CreateLinkAsync(vocabularyToToken, ContainsRelation);

                _logger.LogInformation("Successfully added token {TokenId} to vocabulary {VocabularyId} with link {LinkId}",
                    tokenId, vocabularyId, vocabularyTokenRelation);

                return vocabularyTokenRelation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add token {TokenId} to vocabulary {VocabularyId}", tokenId, vocabularyId);
                throw;
            }
        }

        ~LinksService()
        {
            Dispose(false);
        }
    }
}
