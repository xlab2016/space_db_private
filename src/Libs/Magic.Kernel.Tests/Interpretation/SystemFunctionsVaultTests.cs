using FluentAssertions;
using Magic.Kernel;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;
using Xunit;

namespace Magic.Kernel.Tests.Interpretation
{
    public class SystemFunctionsVaultTests
    {
        private sealed class FakeVaultReader : IVaultReader
        {
            public string? LastKey { get; private set; }
            public string? ValueToReturn { get; set; }

            public string? Read(string key)
            {
                LastKey = key;
                return ValueToReturn;
            }
        }

        [Fact]
        public async Task VaultRead_ShouldPushValueFromVaultReader()
        {
            // Arrange
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var fakeVault = new FakeVaultReader { ValueToReturn = "secret-value" };
            var sys = new SystemFunctions(cfg, stack, memory, fakeVault);

            var callInfo = new CallInfo
            {
                FunctionName = "vault_read",
                Parameters = new Dictionary<string, object>
                {
                    ["0"] = "TOKEN_KEY"
                }
            };

            // Act
            var handled = await sys.ExecuteAsync(callInfo);

            // Assert
            handled.Should().BeTrue();
            fakeVault.LastKey.Should().Be("TOKEN_KEY");
            stack.Should().HaveCount(1);
            stack[0].Should().Be("secret-value");
        }

        [Fact]
        public async Task VaultRead_WhenReaderReturnsNull_ShouldPushEmptyString()
        {
            // Arrange
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var fakeVault = new FakeVaultReader { ValueToReturn = null };
            var sys = new SystemFunctions(cfg, stack, memory, fakeVault);

            var callInfo = new CallInfo
            {
                FunctionName = "vault_read",
                Parameters = new Dictionary<string, object>
                {
                    ["0"] = "MISSING"
                }
            };

            // Act
            var handled = await sys.ExecuteAsync(callInfo);

            // Assert
            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().Be(string.Empty);
        }

        [Fact]
        public async Task VaultRead_WithoutKeyParam_ShouldThrow()
        {
            // Arrange
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var fakeVault = new FakeVaultReader();
            var sys = new SystemFunctions(cfg, stack, memory, fakeVault);

            var callInfo = new CallInfo
            {
                FunctionName = "vault_read",
                Parameters = new Dictionary<string, object>()
            };

            // Act
            var act = () => sys.ExecuteAsync(callInfo);

            // Assert
            await Assert.ThrowsAsync<InvalidOperationException>(act);
            stack.Should().BeEmpty();
        }
    }
}

