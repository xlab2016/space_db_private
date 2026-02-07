using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpaceCompiler.Models;
using SpaceDb.Services;

namespace SpaceDb.Controllers;

/// <summary>
/// Database Controller for adding Token to Vocabulary in the graph
/// Creates relationship: Vocabulary => Token*
/// </summary>
[Route("/api/v1/db")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class DbController : ControllerBase
{
    private readonly ISpaceDbService _spaceDbService;
    private readonly ILogger<DbController> _logger;

    public DbController(
        ISpaceDbService spaceDbService,
        ILogger<DbController> logger)
    {
        _spaceDbService = spaceDbService ?? throw new ArgumentNullException(nameof(spaceDbService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Add a token to vocabulary in the graph (Vocabulary => Token*)
    /// </summary>
    /// <remarks>
    /// Creates vocabulary and token points if they don't exist, then creates a segment relationship.
    /// If vocabularyId is not provided, a new vocabulary will be created.
    /// </remarks>
    [HttpPost("vocabulary/tokens")]
    [ProducesResponseType(typeof(ApiResponse<AddTokenToVocabularyResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddTokenToVocabulary([FromBody] AddTokenToVocabularyRequest request)
    {
        try
        {
            if (request.Token == null)
            {
                return BadRequest(new ApiResponse<AddTokenToVocabularyResult>(
                    null,
                    "Token is required"));
            }

            if (!request.SingularityId.HasValue)
            {
                return BadRequest(new ApiResponse<AddTokenToVocabularyResult>(
                    null,
                    "SingularityId is required"));
            }

            _logger.LogInformation("Adding token '{Content}' to vocabulary for singularity {SingularityId}",
                request.Token.Content, request.SingularityId);

            var result = await _spaceDbService.AddTokenToVocabularyAsync(
                request.Token,
                request.SingularityId,
                request.UserId);

            if (result != null)
            {
                return Ok(new ApiResponse<AddTokenToVocabularyResult>(
                    result,
                    $"Successfully added token '{request.Token.Content}' to vocabulary {result.VocabularyId} with segment {result.SegmentId}"));
            }
            else
            {
                return BadRequest(new ApiResponse<AddTokenToVocabularyResult>(
                    null,
                    $"Failed to add token '{request.Token.Content}' to vocabulary"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding token to vocabulary");
            return StatusCode(500, new ApiResponse<AddTokenToVocabularyResult>(
                null,
                "Internal server error"));
        }
    }

    /// <summary>
    /// Add multiple tokens to vocabulary in batch
    /// </summary>
    [HttpPost("vocabulary/tokens/batch")]
    [ProducesResponseType(typeof(ApiResponse<List<AddTokenToVocabularyResult>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddTokensToVocabularyBatch([FromBody] AddTokensBatchRequest request)
    {
        try
        {
            if (request.Tokens == null || request.Tokens.Count == 0)
            {
                return BadRequest(new ApiResponse<List<AddTokenToVocabularyResult>>(
                    null,
                    "At least one token is required"));
            }

            if (!request.SingularityId.HasValue)
            {
                return BadRequest(new ApiResponse<List<AddTokenToVocabularyResult>>(
                    null,
                    "SingularityId is required"));
            }

            _logger.LogInformation("Adding {Count} tokens to vocabulary for singularity {SingularityId}",
                request.Tokens.Count, request.SingularityId);

            var results = new List<AddTokenToVocabularyResult>();

            foreach (var token in request.Tokens)
            {
                try
                {
                    var result = await _spaceDbService.AddTokenToVocabularyAsync(
                        token,
                        request.SingularityId,
                        request.UserId);
                    
                    if (result != null)
                    {
                        results.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add token '{Content}' to vocabulary", token?.Content ?? "unknown");
                }
            }

            return Ok(new ApiResponse<List<AddTokenToVocabularyResult>>(
                results,
                $"Successfully added {results.Count}/{request.Tokens.Count} tokens to vocabulary"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in batch adding tokens to vocabulary");
            return StatusCode(500, new ApiResponse<List<AddTokenToVocabularyResult>>(
                null,
                "Internal server error"));
        }
    }
}

/// <summary>
/// Request for adding token to vocabulary
/// </summary>
public class AddTokenToVocabularyRequest
{
    /// <summary>
    /// Token object to add
    /// </summary>
    public Token Token { get; set; } = null!;

    /// <summary>
    /// Singularity namespace (required, Vocabulary is unique per singularity)
    /// </summary>
    public long? SingularityId { get; set; }

    /// <summary>
    /// User ID
    /// </summary>
    public int? UserId { get; set; }
}

/// <summary>
/// Request for batch adding tokens to vocabulary
/// </summary>
public class AddTokensBatchRequest
{
    /// <summary>
    /// Collection of tokens to add
    /// </summary>
    public List<Token> Tokens { get; set; } = new();

    /// <summary>
    /// Singularity namespace (required, Vocabulary is unique per singularity)
    /// </summary>
    public long? SingularityId { get; set; }

    /// <summary>
    /// User ID
    /// </summary>
    public int? UserId { get; set; }
}

