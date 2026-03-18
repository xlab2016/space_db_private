using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;
using Magic.Kernel.Types;
using Xunit;

namespace Magic.Kernel.Tests.Interpretation
{
    public class SystemFunctionsConvertTests
    {
        [Fact]
        public async Task Convert_Base64_FromByteArray_ShouldPushBase64String()
        {
            // Arrange
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            var data = new byte[] { 1, 2, 3, 4, 5 };
            var expected = Convert.ToBase64String(data);

            var callInfo = new CallInfo
            {
                FunctionName = "convert",
                Parameters = new Dictionary<string, object>
                {
                    ["0"] = data,
                    ["1"] = "base64"
                }
            };

            // Act
            var handled = await sys.ExecuteAsync(callInfo);

            // Assert
            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().BeOfType<string>().Which.Should().Be(expected);
        }

        [Fact]
        public async Task Convert_Base64_FromString_ShouldUseUtf8Bytes()
        {
            // Arrange
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            const string text = "hello";
            var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

            var callInfo = new CallInfo
            {
                FunctionName = "convert",
                Parameters = new Dictionary<string, object>
                {
                    ["0"] = text,
                    ["1"] = "base64"
                }
            };

            // Act
            var handled = await sys.ExecuteAsync(callInfo);

            // Assert
            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().BeOfType<string>().Which.Should().Be(expected);
        }

        [Fact]
        public async Task Convert_WithUnknownType_ShouldReturnOriginalValue()
        {
            // Arrange
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            const int value = 42;

            var callInfo = new CallInfo
            {
                FunctionName = "convert",
                Parameters = new Dictionary<string, object>
                {
                    ["0"] = value,
                    ["1"] = "unknown_type"
                }
            };

            // Act
            var handled = await sys.ExecuteAsync(callInfo);

            // Assert
            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().Be(value);
        }

        [Fact]
        public async Task OpJson_Set_WithByteArray_ShouldStoreBase64String()
        {
            // Arrange
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            var sourceAddr = new MemoryAddress { Index = 0 };
            memory[0] = "{}";

            var data = new byte[] { 10, 20, 30 };
            var expectedBase64 = Convert.ToBase64String(data);

            var callInfo = new CallInfo
            {
                FunctionName = "opjson",
                Parameters = new Dictionary<string, object>
                {
                    ["source"] = sourceAddr,
                    ["operation"] = "set",
                    ["path"] = "bin",
                    ["data"] = data
                }
            };

            // Act
            var handled = await sys.ExecuteAsync(callInfo);

            // Assert
            handled.Should().BeTrue();
            memory.Should().ContainKey(0);
            memory[0].Should().BeOfType<Dictionary<string, object>>();
            var root = (Dictionary<string, object>)memory[0];
            root.Should().ContainKey("bin");
            root["bin"].Should().BeOfType<string>().Which.Should().Be(expectedBase64);
        }

        [Fact]
        public async Task Unit_Mb_ReturnsBytes()
        {
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            var callInfo = new CallInfo
            {
                FunctionName = "unit",
                Parameters = new Dictionary<string, object> { ["0"] = 20, ["1"] = "mb" }
            };

            var handled = await sys.ExecuteAsync(callInfo);

            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().Be(20L * 1024 * 1024);
        }

        [Fact]
        public async Task Unit_Kb_ReturnsBytes()
        {
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            var callInfo = new CallInfo
            {
                FunctionName = "unit",
                Parameters = new Dictionary<string, object> { ["0"] = 5, ["1"] = "kb" }
            };

            var handled = await sys.ExecuteAsync(callInfo);

            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().Be(5L * 1024);
        }

        [Fact]
        public async Task Unit_InverseMb_ReturnsMegabytes()
        {
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            var callInfo = new CallInfo
            {
                FunctionName = "unit",
                Parameters = new Dictionary<string, object> { ["0"] = 20L * 1024 * 1024, ["1"] = "1/mb" }
            };

            var handled = await sys.ExecuteAsync(callInfo);

            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().Be(20L);
        }

        [Fact]
        public async Task Unit_InverseMb_WithFloatDecimalType_ReturnsFloatDecimal()
        {
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            var callInfo = new CallInfo
            {
                FunctionName = "unit",
                Parameters = new Dictionary<string, object>
                {
                    ["0"] = 1572864L,
                    ["1"] = "1/mb",
                    ["2"] = "float<decimal>"
                }
            };

            var handled = await sys.ExecuteAsync(callInfo);

            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().BeOfType<FloatDecimal>()
                .Which.ToString().Should().Be("1.5");
        }

        [Fact]
        public async Task Convert_FloatDecimal_FromString_ShouldPushFloatDecimal()
        {
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            var callInfo = new CallInfo
            {
                FunctionName = "convert",
                Parameters = new Dictionary<string, object>
                {
                    ["0"] = "20.125",
                    ["1"] = "float<decimal>"
                }
            };

            var handled = await sys.ExecuteAsync(callInfo);

            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().BeOfType<FloatDecimal>()
                .Which.ToString().Should().Be("20.125");
        }

        [Fact]
        public async Task Unit_UnknownUnit_Throws()
        {
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            var sys = new SystemFunctions(cfg, stack, memory);

            var callInfo = new CallInfo
            {
                FunctionName = "unit",
                Parameters = new Dictionary<string, object> { ["0"] = 1, ["1"] = "pb" }
            };

            await sys.Invoking(s => s.ExecuteAsync(callInfo))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*unknown unit*");
        }

        [Fact]
        public async Task Format_SubstitutesPlaceholders_PushesString()
        {
            var stack = new List<object>();
            var memory = new Dictionary<long, object>();
            var cfg = new KernelConfiguration();
            memory[0] = 42L;
            memory[1] = "mb";
            var sys = new SystemFunctions(cfg, stack, memory);

            var callInfo = new CallInfo
            {
                FunctionName = "format",
                Parameters = new Dictionary<string, object>
                {
                    ["0"] = "Size {0} exceeded {1}",
                    ["1"] = new MemoryAddress { Index = 0 },
                    ["2"] = new MemoryAddress { Index = 1 }
                }
            };

            var handled = await sys.ExecuteAsync(callInfo);

            handled.Should().BeTrue();
            stack.Should().HaveCount(1);
            stack[0].Should().Be("Size 42 exceeded mb");
        }
    }
}

