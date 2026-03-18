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
        public async Task VaultRead_ShouldNotBeHandledBySystemFunctions()
        {
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

            var handled = await sys.ExecuteAsync(callInfo);

            handled.Should().BeFalse();
            stack.Should().BeEmpty();
            fakeVault.LastKey.Should().BeNull();
        }
    }
}

