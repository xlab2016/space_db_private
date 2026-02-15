using Magic.Kernel.Devices;
using Magic.Kernel.Devices.SSC;
using System.Text;

namespace SpaceDb.Services
{
    /// <summary>ISSCompiler implementation as driver for Space Disk: reads stream, compiles (e.g. parse), optionally persists via disk.</summary>
    public class SpaceDbSSCCompiler : ISSCompiler
    {
        private readonly ISpaceDisk _disk;

        public SpaceDbSSCCompiler(ISpaceDisk disk)
        {
            _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        }

        public async Task CompileAsync(IStreamDevice device)
        {
            if (device == null)
                return;

            IStreamChunk chunk;
            var result = await device.ReadChunkAsync(out chunk).ConfigureAwait(false);
            if (result != Magic.Kernel.Devices.DeviceOperationResult.Success || chunk?.Data == null || chunk.Data.Length == 0)
                return;

            var content = Encoding.UTF8.GetString(chunk.Data);
            // Placeholder: stream content is read; later parse and persist via _disk (AddVertex/AddShape etc.)
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
