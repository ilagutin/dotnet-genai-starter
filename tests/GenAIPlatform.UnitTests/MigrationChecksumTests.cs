using GenAIPlatform.Infrastructure.Migrations;

namespace GenAIPlatform.UnitTests;

public sealed class MigrationChecksumTests
{
    [Fact]
    public void Compute_IgnoresLineEndingStyle()
    {
        const string LineFeedSql = "CREATE TABLE a (id uuid);\nCREATE TABLE b (id uuid);\n";
        const string CarriageReturnLineFeedSql =
            "CREATE TABLE a (id uuid);\r\nCREATE TABLE b (id uuid);\r\n";
        const string CarriageReturnSql = "CREATE TABLE a (id uuid);\rCREATE TABLE b (id uuid);\r";

        Assert.Equal(
            MigrationChecksum.Compute(LineFeedSql),
            MigrationChecksum.Compute(CarriageReturnLineFeedSql));
        Assert.Equal(
            MigrationChecksum.Compute(LineFeedSql),
            MigrationChecksum.Compute(CarriageReturnSql));
    }

    [Fact]
    public void Compute_IgnoresALeadingByteOrderMark()
    {
        const string Sql = "SELECT 1;\n";

        Assert.Equal(
            MigrationChecksum.Compute(Sql),
            MigrationChecksum.Compute((char)0xFEFF + Sql));
    }

    [Fact]
    public void Compute_ReturnsLowercaseHexAndDiffersForChangedSql()
    {
        var checksum = MigrationChecksum.Compute("SELECT 1;\n");

        Assert.Equal(64, checksum.Length);
        Assert.All(checksum, static character =>
            Assert.True(char.IsAsciiDigit(character) || (character is >= 'a' and <= 'f')));
        Assert.NotEqual(checksum, MigrationChecksum.Compute("SELECT 2;\n"));
    }

    [Fact]
    public void Canonicalize_NormalizesEveryLineEndingToLineFeed()
    {
        Assert.Equal("a\nb\nc\n", MigrationChecksum.Canonicalize("a\r\nb\rc\n"));
    }
}
