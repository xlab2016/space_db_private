using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Magic.Kernel;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;
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
    }
}

