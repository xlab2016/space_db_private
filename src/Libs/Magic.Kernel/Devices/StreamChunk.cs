using System;

namespace Magic.Kernel.Devices
{
    public class StreamChunk : IStreamChunk
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }
}
