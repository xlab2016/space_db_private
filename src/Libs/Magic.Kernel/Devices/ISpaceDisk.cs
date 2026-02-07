using Magic.Kernel.Space;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface ISpaceDisk : IDevice, ICapableDevice
    {
        public SpaceDiskConfiguration Configuration { get; set; }

        public Task<SpaceOperationResult> AddVertex(Vertex vertex);
        public Task<SpaceOperationResult> AddRelation(Relation relation);
        public Task<SpaceOperationResult> AddShape(Shape shape);
        public Task<Vertex?> GetVertex(long? index, string? name);
        public Task<Relation?> GetRelation(long? index, string? name);
        public Task<Shape?> GetShape(long? index, string? name);
    }
}
