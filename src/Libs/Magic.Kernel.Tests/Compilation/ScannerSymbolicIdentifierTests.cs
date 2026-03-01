using FluentAssertions;
using Magic.Kernel.Compilation;
using Xunit;

namespace Magic.Kernel.Tests.Compilation
{
    public class ScannerSymbolicIdentifierTests
    {
        [Fact]
        public void Scan_WithMessagesDiamond_ShouldTreatAsSingleIdentifier()
        {
            var scanner = new Scanner("Messages<> : table");
            var t1 = scanner.Scan();
            var t2 = scanner.Scan();
            var t3 = scanner.Scan();

            t1.Kind.Should().Be(TokenKind.Identifier);
            t1.Value.Should().Be("Messages<>");
            t2.Kind.Should().Be(TokenKind.Colon);
            t3.Kind.Should().Be(TokenKind.Identifier);
            t3.Value.Should().Be("table");
        }

        [Fact]
        public void Scan_WithDbDoubleGreater_ShouldSplitIntoDbSymbolAndGenericClose()
        {
            var scanner = new Scanner("database<postgres, Db>>;");

            scanner.Scan().Value.Should().Be("database");
            scanner.Scan().Kind.Should().Be(TokenKind.LessThan);
            scanner.Scan().Value.Should().Be("postgres");
            scanner.Scan().Kind.Should().Be(TokenKind.Comma);

            var dbSymbol = scanner.Scan();
            dbSymbol.Kind.Should().Be(TokenKind.Identifier);
            dbSymbol.Value.Should().Be("Db>");

            scanner.Scan().Kind.Should().Be(TokenKind.GreaterThan);
            scanner.Scan().Kind.Should().Be(TokenKind.Semicolon);
        }
    }
}
