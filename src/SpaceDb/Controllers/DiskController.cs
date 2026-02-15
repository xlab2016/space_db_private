using Magic.Kernel.Devices;
using Magic.Kernel.Space;
using Microsoft.AspNetCore.Mvc;
using SpaceDb.Services;

namespace SpaceDb.Controllers
{
    /// <summary>
    /// API для доступа к Space Disk (RocksDbSpaceDisk) и управлению: вершины, связи, фигуры, конфигурация, служебные операции.
    /// </summary>
    [Route("/api/v1/disk")]
    [ApiController]
    public class DiskController : ControllerBase
    {
        private readonly RocksDbSpaceDisk _disk;
        private readonly IRocksDbService _rocksDb;

        public DiskController(RocksDbSpaceDisk disk, IRocksDbService rocksDb)
        {
            _disk = disk;
            _rocksDb = rocksDb;
        }

        /// <summary>Информация о диске: имя, индекс, текущие счётчики конфигурации.</summary>
        [HttpGet]
        [ProducesResponseType(typeof(DiskInfoResponse), StatusCodes.Status200OK)]
        public IActionResult GetInfo()
        {
            var cfg = _disk.Configuration;
            return Ok(new DiskInfoResponse
            {
                Name = _disk.Name,
                Index = _disk.Index,
                SpaceName = cfg?.SpaceName,
                VertexSequenceIndex = cfg?.VertexSequenceIndex,
                RelationSequenceIndex = cfg?.RelationSequenceIndex,
                ShapeSequenceIndex = cfg?.ShapeSequenceIndex
            });
        }

        /// <summary>Текущая конфигурация диска (только последовательности).</summary>
        [HttpGet("configuration")]
        [ProducesResponseType(typeof(DiskConfigurationDto), StatusCodes.Status200OK)]
        public IActionResult GetConfiguration()
        {
            var c = _disk.Configuration;
            return Ok(new DiskConfigurationDto
            {
                SpaceName = c?.SpaceName,
                VertexSequenceIndex = c?.VertexSequenceIndex,
                RelationSequenceIndex = c?.RelationSequenceIndex,
                ShapeSequenceIndex = c?.ShapeSequenceIndex
            });
        }

        /// <summary>Обновить конфигурацию (счётчики последовательностей).</summary>
        [HttpPut("configuration")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public IActionResult UpdateConfiguration([FromBody] DiskConfigurationDto dto)
        {
            if (_disk.Configuration == null)
                _disk.Configuration = new SpaceDiskConfiguration();

            if (dto.SpaceName != null)
                _disk.Configuration.SpaceName = dto.SpaceName;
            _disk.Configuration.VertexSequenceIndex = dto.VertexSequenceIndex ?? _disk.Configuration.VertexSequenceIndex;
            _disk.Configuration.RelationSequenceIndex = dto.RelationSequenceIndex ?? _disk.Configuration.RelationSequenceIndex;
            _disk.Configuration.ShapeSequenceIndex = dto.ShapeSequenceIndex ?? _disk.Configuration.ShapeSequenceIndex;
            return NoContent();
        }

        // --- Vertices ---
        [HttpPost("vertices")]
        [ProducesResponseType(typeof(VertexResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> AddVertex([FromBody] Vertex vertex, [FromQuery] string? spaceName)
        {
            var result = await _disk.AddVertex(vertex, spaceName);
            return ToActionResult(result, () => Ok(new VertexResult { Result = result, Vertex = vertex }));
        }

        [HttpGet("vertices")]
        [ProducesResponseType(typeof(Vertex), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetVertex([FromQuery] long? index, [FromQuery] string? name, [FromQuery] string? spaceName)
        {
            var v = await _disk.GetVertex(index, name, spaceName);
            return v != null ? Ok(v) : NotFound();
        }

        // --- Relations ---
        [HttpPost("relations")]
        [ProducesResponseType(typeof(RelationResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> AddRelation([FromBody] Relation relation, [FromQuery] string? spaceName)
        {
            var result = await _disk.AddRelation(relation, spaceName);
            return ToActionResult(result, () => Ok(new RelationResult { Result = result, Relation = relation }));
        }

        [HttpGet("relations")]
        [ProducesResponseType(typeof(Relation), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetRelation([FromQuery] long? index, [FromQuery] string? name, [FromQuery] string? spaceName)
        {
            var r = await _disk.GetRelation(index, name, spaceName);
            return r != null ? Ok(r) : NotFound();
        }

        // --- Shapes ---
        [HttpPost("shapes")]
        [ProducesResponseType(typeof(ShapeResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> AddShape([FromBody] Shape shape, [FromQuery] string? spaceName)
        {
            var result = await _disk.AddShape(shape, spaceName);
            return ToActionResult(result, () => Ok(new ShapeResult { Result = result, Shape = shape }));
        }

        [HttpGet("shapes")]
        [ProducesResponseType(typeof(Shape), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetShape([FromQuery] long? index, [FromQuery] string? name, [FromQuery] string? spaceName)
        {
            var s = await _disk.GetShape(index, name, spaceName);
            return s != null ? Ok(s) : NotFound();
        }

        // --- Management (RocksDB) ---
        /// <summary>Количество ключей в RocksDB.</summary>
        [HttpGet("stats")]
        [ProducesResponseType(typeof(DiskStatsResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStats()
        {
            var count = await _rocksDb.GetCountAsync();
            return Ok(new DiskStatsResponse { KeyCount = count });
        }

        /// <summary>Запустить компакцию RocksDB.</summary>
        [HttpPost("compact")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Compact()
        {
            var ok = await _rocksDb.CompactAsync();
            return ok ? Ok(new { compacted = true }) : StatusCode(500, new { compacted = false });
        }

        private static IActionResult ToActionResult(SpaceOperationResult result, Func<IActionResult> onSuccess)
        {
            if (result == SpaceOperationResult.Success) return onSuccess();
            if (result == SpaceOperationResult.InvalidArguments) return new BadRequestObjectResult(new { result, message = "InvalidArguments" });
            if (result == SpaceOperationResult.NameLengthExceeded256) return new BadRequestObjectResult(new { result, message = "NameLengthExceeded256" });
            return new ObjectResult(new { result }) { StatusCode = 500 };
        }

        public class DiskInfoResponse
        {
            public string? Name { get; set; }
            public long? Index { get; set; }
            /// <summary>Namespace for keys: {spaceName}:vertices:index:... Set from program as module_name + "|" + program_name.</summary>
            public string? SpaceName { get; set; }
            public long? VertexSequenceIndex { get; set; }
            public long? RelationSequenceIndex { get; set; }
            public long? ShapeSequenceIndex { get; set; }
        }

        public class DiskConfigurationDto
        {
            public string? SpaceName { get; set; }
            public long? VertexSequenceIndex { get; set; }
            public long? RelationSequenceIndex { get; set; }
            public long? ShapeSequenceIndex { get; set; }
        }

        public class DiskStatsResponse
        {
            public long KeyCount { get; set; }
        }

        public class VertexResult
        {
            public SpaceOperationResult Result { get; set; }
            public Vertex? Vertex { get; set; }
        }

        public class RelationResult
        {
            public SpaceOperationResult Result { get; set; }
            public Relation? Relation { get; set; }
        }

        public class ShapeResult
        {
            public SpaceOperationResult Result { get; set; }
            public Shape? Shape { get; set; }
        }
    }
}
