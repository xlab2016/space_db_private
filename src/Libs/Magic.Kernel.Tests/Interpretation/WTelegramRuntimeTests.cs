using FluentAssertions;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Devices.Streams;
using Xunit;

namespace Magic.Kernel.Tests.Interpretation
{
    public class WTelegramRuntimeTests
    {
        [Fact]
        public void DefGen_WithMessengerTelegramClient_ShouldAttachWTelegramStreamDevice()
        {
            var stream = Hal.Def("stream", executableUnit: null).Should().BeAssignableTo<IDefType>().Subject;

            var result = Hal.DefGen(stream, new object?[] { "messenger", "telegram", "client" })
                .Should()
                .BeAssignableTo<IDefType>()
                .Subject;

            result.Generalizations.Should().ContainSingle(x => x is WTelegramStreamDevice);
        }

        [Fact]
        public void DefGen_WithNetworkFileTelegramClient_ShouldAttachWTelegramNetworkFileStreamDevice()
        {
            var stream = Hal.Def("stream", executableUnit: null).Should().BeAssignableTo<IDefType>().Subject;

            var result = Hal.DefGen(stream, new object?[] { "network", "file", "telegram", "client" })
                .Should()
                .BeAssignableTo<IDefType>()
                .Subject;

            result.Generalizations.Should().ContainSingle(x => x is WTelegramNetworkFileStreamDevice);
        }
    }
}
