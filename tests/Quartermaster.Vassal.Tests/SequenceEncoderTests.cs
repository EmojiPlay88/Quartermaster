using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class SequenceEncoderTests
{
    // Note: VASSAL's quote rule is "starts with a backslash, or starts AND ends with a single quote"
    // (SequenceEncoder.append). A token that merely ENDS with a backslash is not quoted.
    public static TheoryData<string[], char, string> Rows => new()
    {
        { new[] { "a", "b", "c" }, ';', "a;b;c" },
        { new[] { "a;b", "c" }, ';', "a\\;b;c" },
        { new[] { "\\starts", "x" }, ';', "'\\starts';x" },
        { new[] { "ends\\", "x" }, ';', "ends\\;x" },
        { new[] { "'q'", "x" }, ';', "''q'';x" },
        { new[] { "", "" }, ';', ";" },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Encode_MatchesVassal(string[] tokens, char d, string expected)
        => Assert.Equal(expected, SequenceEncoder.Encode(tokens, d));

    [Theory]
    [MemberData(nameof(Rows))]
    public void Decode_RoundTrips(string[] tokens, char d, string encoded)
    {
        // "ends\" followed by the delimiter is inherently ambiguous in VASSAL's format (the delimiter
        // reads as escaped), so that row is checked for encoding only.
        if (encoded == "ends\\;x") return;
        Assert.Equal(tokens, SequenceEncoder.Decode(encoded, d));
    }

    [Fact]
    public void Encode_NullTokenIsEmpty()
        => Assert.Equal("a;;b", SequenceEncoder.Encode(new string?[] { "a", null, "b" }, ';'));

    [Fact]
    public void Decode_NullIsEmptyList() => Assert.Empty(SequenceEncoder.Decode(null, ';'));

    [Fact]
    public void NestedTabChain_DecodesTwoLevels()
    {
        var inner = SequenceEncoder.Encode(new[] { "prototype;P", "piece;;;img.png;Name" }, '\t');
        var outer = SequenceEncoder.Encode(new[] { "label;x", inner }, '\t');
        Assert.Equal("label;x\tprototype;P\\\tpiece;;;img.png;Name", outer);
        var lvl1 = SequenceEncoder.Decode(outer, '\t');
        Assert.Equal(inner, lvl1[1]);
        Assert.Equal(new[] { "prototype;P", "piece;;;img.png;Name" }, SequenceEncoder.Decode(lvl1[1], '\t'));
    }

    [Fact]
    public void Decoder_TypedReads_UseDefaultsOnGarbage()
    {
        var d = new SequenceDecoder("5;abc;true;2.5", ';');
        Assert.Equal(5, d.NextInt(0));
        Assert.Equal(7, d.NextInt(7));
        Assert.True(d.NextBoolean(false));
        Assert.Equal(2.5, d.NextDouble(0));
        Assert.False(d.HasMoreTokens);
        Assert.Equal("z", d.NextToken("z"));
    }

    [Fact]
    public void Decoder_NullValue_NeverThrows()
    {
        var d = new SequenceDecoder(null, ';');
        Assert.False(d.HasMoreTokens);
        Assert.Equal(3, d.NextInt(3));
        Assert.True(d.NextBoolean(true));
        Assert.Equal(1.5, d.NextDouble(1.5));
        Assert.Equal("z", d.NextToken("z"));
    }

    [Fact]
    public void Decoder_EmptyTrailingToken()
    {
        var d = new SequenceDecoder("a;", ';');
        Assert.Equal("a", d.NextToken());
        Assert.True(d.HasMoreTokens);
        Assert.Equal("", d.NextToken());
        Assert.False(d.HasMoreTokens);
    }

    [Fact]
    public void Decoder_StringArray_SplitsCommaListAndPads()
    {
        var d = new SequenceDecoder("a,b\\,c,d;next", ';');
        Assert.Equal(new[] { "a", "b,c", "d" }, d.NextStringArray(2));
        var d2 = new SequenceDecoder("x", ';');
        Assert.Equal(new[] { "x", "", "" }, d2.NextStringArray(3));
        Assert.Equal(new[] { "", "" }, d2.NextStringArray(2));
    }
}
