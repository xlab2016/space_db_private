using SpaceDb.Models;
using SpaceDb.Services.Parsers;
using System.Text.Json;
using AI;

namespace SpaceDb.Services
{
    /// <summary>
    /// Service for parsing content and storing as hierarchical graph structure
    /// Resource (dimension=0) -> Fragments (dimension=1)
    /// </summary>
    public class ContentParserService : IContentParserService
    {
        private readonly ISpaceDbService _spaceDbService;
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly ILogger<ContentParserService> _logger;
        private readonly IWorkflowLogService? _workflowLogService;
        private readonly Dictionary<string, PayloadParserBase> _parsers;
        private readonly string _embeddingType;

        public ContentParserService(
            ISpaceDbService spaceDbService,
            IEmbeddingProvider embeddingProvider,
            ILogger<ContentParserService> logger,
            IEnumerable<PayloadParserBase> parsers,
            string embeddingType = "default",
            IWorkflowLogService? workflowLogService = null)
        {
            _spaceDbService = spaceDbService ?? throw new ArgumentNullException(nameof(spaceDbService));
            _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _workflowLogService = workflowLogService;
            _embeddingType = embeddingType;

            // Register parsers by content type
            _parsers = parsers.ToDictionary(p => p.ContentType, p => p);

            _logger.LogInformation("ContentParserService initialized with {Count} parsers: {Types}",
                _parsers.Count, string.Join(", ", _parsers.Keys));
        }

        public async Task<ContentParseResult?> ParseAndStoreAsync(
            string payload,
            string resourceId,
            string contentType = "auto",
            long? singularityId = null,
            int? userId = null,
            Dictionary<string, object>? metadata = null)
        {
            long? workflowId = null;

            try
            {
                // Start workflow logging
                if (_workflowLogService != null)
                {
                    workflowId = await _workflowLogService.StartWorkflowAsync(
                        "ContentParsing",
                        $"Parsing content for resource '{resourceId}' (type: {contentType}, size: {payload.Length} chars)");
                }

                _logger.LogInformation("Parsing and storing content for resource {ResourceId}, type: {ContentType}",
                    resourceId, contentType);

                // Determine parser
                var parser = GetParser(payload, contentType);
                if (parser == null)
                {
                    _logger.LogError("No suitable parser found for content type: {ContentType}", contentType);
                    if (workflowId.HasValue && _workflowLogService != null)
                    {
                        await _workflowLogService.FailWorkflowAsync(
                            workflowId.Value,
                            $"No suitable parser found for content type: {contentType}");
                    }
                    return null;
                }

                if (workflowId.HasValue && _workflowLogService != null)
                {
                    await _workflowLogService.LogInfoAsync(
                        workflowId.Value,
                        $"Using parser: {parser.ContentType}");
                }

                // Parse payload into blocks and fragments
                var parsedResource = await parser.ParseAsync(payload, resourceId, metadata);

                if (parsedResource.Blocks.Count == 0)
                {
                    _logger.LogWarning("No blocks parsed from resource {ResourceId}", resourceId);
                    if (workflowId.HasValue && _workflowLogService != null)
                    {
                        await _workflowLogService.LogWarningAsync(
                            workflowId.Value,
                            $"No blocks parsed from resource '{resourceId}'");
                    }
                    return null;
                }

                var totalFragments = parsedResource.Blocks.Sum(b => b.Fragments.Count);

                _logger.LogInformation("Parsed {BlockCount} blocks with {FragmentCount} total fragments from resource {ResourceId}",
                    parsedResource.Blocks.Count, totalFragments, resourceId);

                if (workflowId.HasValue && _workflowLogService != null)
                {
                    await _workflowLogService.LogInfoAsync(
                        workflowId.Value,
                        $"Parsed {parsedResource.Blocks.Count} blocks with {totalFragments} total fragments");
                }

                // Create resource point (dimension=0, layer=0)
                var resourceMetadata = new Dictionary<string, object>
                {
                    ["resource_id"] = resourceId,
                    ["resource_type"] = parsedResource.ResourceType,
                    ["block_count"] = parsedResource.Blocks.Count,
                    ["fragment_count"] = totalFragments,
                    ["parsed_at"] = DateTime.UtcNow
                };

                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        resourceMetadata[kvp.Key] = kvp.Value;
                    }
                }

                var resourcePayload = $"Resource: {resourceId} ({parsedResource.ResourceType}) with {parsedResource.Blocks.Count} blocks and {totalFragments} fragments";

                var resourcePoint = new Point
                {
                    Layer = 0,
                    Dimension = 0,  // Resources at dimension 0
                    Weight = 1.0,
                    SingularityId = singularityId,
                    UserId = userId,
                    Payload = resourcePayload
                };

                if (workflowId.HasValue && _workflowLogService != null)
                {
                    await _workflowLogService.LogInfoAsync(
                        workflowId.Value,
                        "Creating resource point (dimension=0)");
                }

                var resourcePointId = await _spaceDbService.AddPointAsync((long?)null, resourcePoint);

                if (!resourcePointId.HasValue)
                {
                    _logger.LogError("Failed to create resource point for {ResourceId}", resourceId);
                    if (workflowId.HasValue && _workflowLogService != null)
                    {
                        await _workflowLogService.FailWorkflowAsync(
                            workflowId.Value,
                            $"Failed to create resource point for '{resourceId}'");
                    }
                    return null;
                }

                _logger.LogInformation("Created resource point {PointId} for {ResourceId}",
                    resourcePointId, resourceId);

                if (workflowId.HasValue && _workflowLogService != null)
                {
                    await _workflowLogService.LogInfoAsync(
                        workflowId.Value,
                        $"Created resource point {resourcePointId.Value}");
                }

                // Prepare result
                var result = new ContentParseResult
                {
                    ResourcePointId = resourcePointId.Value,
                    ParserType = parser.ContentType
                };

                // Process each block
                if (workflowId.HasValue && _workflowLogService != null)
                {
                    await _workflowLogService.LogInfoAsync(
                        workflowId.Value,
                        $"Processing {parsedResource.Blocks.Count} blocks with embeddings");
                }

                foreach (var block in parsedResource.Blocks)
                {
                    // Step 1: Create block embedding from concatenated block content
                    _logger.LogInformation("Creating block embedding for block {Order} with {FragmentCount} fragments",
                        block.Order, block.Fragments.Count);

                    if (workflowId.HasValue && _workflowLogService != null)
                    {
                        await _workflowLogService.LogInfoAsync(
                            workflowId.Value,
                            $"Processing block {block.Order + 1}/{parsedResource.Blocks.Count} ({block.Fragments.Count} fragments)");
                    }

                    var blockEmbedding = await _embeddingProvider.CreateEmbeddingAsync(
                        _embeddingType,
                        block.Content,
                        label: null,
                        returnVectors: true);

                    // Step 2: Create block point (dimension=1) with embedding
                    var blockMetadata = new Dictionary<string, object>
                    {
                        ["resource_id"] = resourceId,
                        ["block_order"] = block.Order,
                        ["fragment_count"] = block.Fragments.Count,
                        ["block_size"] = block.Content.Length
                    };

                    if (block.Metadata != null)
                    {
                        foreach (var kvp in block.Metadata)
                        {
                            blockMetadata[$"block_{kvp.Key}"] = kvp.Value;
                        }
                    }

                    var blockPoint = new Point
                    {
                        Layer = 0,
                        Dimension = 1,  // Blocks at dimension 1
                        Weight = 1.0 / (block.Order + 1),
                        SingularityId = singularityId,
                        UserId = userId,
                        Payload = block.Content
                    };

                    // Add block point with embedding and create segment to resource
                    var blockPointId = await _spaceDbService.AddPointAsync(
                        resourcePointId.Value,
                        blockPoint,
                        blockEmbedding);

                    if (!blockPointId.HasValue)
                    {
                        _logger.LogWarning("Failed to create block point for block {Order}", block.Order);
                        continue;
                    }

                    result.BlockPointIds.Add(blockPointId.Value);
                    _logger.LogInformation("Created block point {PointId} for block {Order}",
                        blockPointId, block.Order);

                    // Step 3: Batch create embeddings for all fragments in this block
                    var fragmentTexts = block.Fragments.Select(f => f.Content).ToList();

                    _logger.LogInformation("Creating embeddings for {Count} fragments in block {BlockOrder}",
                        fragmentTexts.Count, block.Order);

                    var fragmentEmbeddings = await _embeddingProvider.CreateEmbeddingsAsync(
                        _embeddingType,
                        fragmentTexts,
                        labels: null,
                        returnVectors: true);

                    if (fragmentEmbeddings.Count != fragmentTexts.Count)
                    {
                        _logger.LogError("Embedding count mismatch for block {BlockOrder}: expected {Expected}, got {Actual}",
                            block.Order, fragmentTexts.Count, fragmentEmbeddings.Count);
                        continue;
                    }

                    // Step 4: Create fragment points with context-dependent embeddings
                    for (int i = 0; i < block.Fragments.Count; i++)
                    {
                        var fragment = block.Fragments[i];
                        var fragmentEmbedding = fragmentEmbeddings[i];

                        // Create context-dependent embedding by adding block embedding to fragment embedding
                        var contextDependentEmbedding = CreateContextDependentEmbedding(
                            blockEmbedding,
                            fragmentEmbedding);

                        var fragmentMetadataDict = new Dictionary<string, object>
                        {
                            ["resource_id"] = resourceId,
                            ["block_order"] = block.Order,
                            ["fragment_type"] = fragment.Type,
                            ["fragment_order"] = fragment.Order,
                            ["parent_key"] = fragment.ParentKey ?? resourceId
                        };

                        if (fragment.Metadata != null)
                        {
                            foreach (var kvp in fragment.Metadata)
                            {
                                fragmentMetadataDict[$"fragment_{kvp.Key}"] = kvp.Value;
                            }
                        }

                        var fragmentPoint = new Point
                        {
                            Layer = 0,
                            Dimension = 1,  // Fragments at dimension 1
                            Weight = 1.0 / (fragment.Order + 1),
                            SingularityId = singularityId,
                            UserId = userId,
                            Payload = fragment.Content
                        };

                        // Add fragment point with context-dependent embedding and create segment to block
                        var fragmentPointId = await _spaceDbService.AddPointAsync(
                            blockPointId.Value,
                            fragmentPoint,
                            contextDependentEmbedding);

                        if (fragmentPointId.HasValue)
                        {
                            result.FragmentPointIds.Add(fragmentPointId.Value);
                            _logger.LogDebug("Created fragment point {PointId} for fragment {Order} in block {BlockOrder}",
                                fragmentPointId, fragment.Order, block.Order);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to create fragment point for fragment {Order} in block {BlockOrder}",
                                fragment.Order, block.Order);
                        }
                    }
                }

                _logger.LogInformation(
                    "Successfully stored resource {ResourceId} with {BlockCount} blocks and {FragmentCount} fragments. " +
                    "Resource point: {ResourcePointId}",
                    resourceId, result.TotalBlocks, result.TotalFragments, resourcePointId);

                if (workflowId.HasValue && _workflowLogService != null)
                {
                    await _workflowLogService.CompleteWorkflowAsync(
                        workflowId.Value,
                        $"Successfully created {result.TotalBlocks} blocks and {result.TotalFragments} fragments for resource '{resourceId}' (ResourcePointId: {resourcePointId})");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing and storing content for resource {ResourceId}", resourceId);

                if (workflowId.HasValue && _workflowLogService != null)
                {
                    await _workflowLogService.FailWorkflowAsync(
                        workflowId.Value,
                        $"Error parsing content for resource '{resourceId}'",
                        ex);
                }

                return null;
            }
        }

        public IEnumerable<string> GetAvailableParserTypes()
        {
            return _parsers.Keys;
        }

        /// <summary>
        /// Create context-dependent embedding by combining block and fragment embeddings
        /// Uses element-wise addition to preserve both block context and fragment semantics
        /// </summary>
        private AIEmbedding CreateContextDependentEmbedding(AIEmbedding blockEmbedding, AIEmbedding fragmentEmbedding)
        {
            if (blockEmbedding.Vector == null || fragmentEmbedding.Vector == null)
            {
                _logger.LogWarning("Cannot create context-dependent embedding: one or both embeddings have null vectors");
                return fragmentEmbedding;
            }

            if (blockEmbedding.Vector.Count != fragmentEmbedding.Vector.Count)
            {
                _logger.LogWarning("Vector dimension mismatch: block={BlockDim}, fragment={FragmentDim}. Using fragment embedding only.",
                    blockEmbedding.Vector.Count, fragmentEmbedding.Vector.Count);
                return fragmentEmbedding;
            }

            // Create combined embedding by element-wise addition
            var combinedVector = new List<float>(blockEmbedding.Vector.Count);
            for (int i = 0; i < blockEmbedding.Vector.Count; i++)
            {
                combinedVector.Add(blockEmbedding.Vector[i] + fragmentEmbedding.Vector[i]);
            }

            // Normalize the combined vector to maintain similar magnitude
            var magnitude = 0.0f;
            for (int i = 0; i < combinedVector.Count; i++)
            {
                magnitude += combinedVector[i] * combinedVector[i];
            }
            magnitude = (float)Math.Sqrt(magnitude);

            if (magnitude > 0)
            {
                for (int i = 0; i < combinedVector.Count; i++)
                {
                    combinedVector[i] /= magnitude;
                }
            }

            return new AIEmbedding
            {
                Id = fragmentEmbedding.Id,
                Label = fragmentEmbedding.Label,
                Vector = combinedVector
            };
        }

        private PayloadParserBase? GetParser(string payload, string contentType)
        {
            // Auto-detect content type
            if (contentType == "auto")
            {
                foreach (var parser in _parsers.Values)
                {
                    if (parser.CanParse(payload))
                    {
                        _logger.LogInformation("Auto-detected content type: {Type}", parser.ContentType);
                        return parser;
                    }
                }
                return null;
            }

            // Use specified parser
            if (_parsers.TryGetValue(contentType, out var selectedParser))
            {
                if (selectedParser.CanParse(payload))
                {
                    return selectedParser;
                }
                else
                {
                    _logger.LogWarning("Parser {Type} cannot parse this payload", contentType);
                }
            }

            return null;
        }
    }
}
