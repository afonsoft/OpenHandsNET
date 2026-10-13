using OpenHandsNET.Infrastructure.Persistence;
using Shouldly;

namespace OpenHandsNET.UnitTests.Persistence;

/// <summary>SPEC-10 RF-004: cursor opaco base64 `{ticks}:{id}`.</summary>
public sealed class CursorPaginatorTests
{
    [Fact]
    public void Encode_Decode_RoundTrips()
    {
        var cursor = CursorPaginator.Encode(638000000000000123L, "01J5XYZ");
        CursorPaginator.TryDecode(cursor, out var ticks, out var id).ShouldBeTrue();
        ticks.ShouldBe(638000000000000123L);
        id.ShouldBe("01J5XYZ");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!!")]
    [InlineData("YWJj")] // "abc" — sem separador
    public void TryDecode_RejectsMalformed(string? cursor)
    {
        CursorPaginator.TryDecode(cursor, out _, out _).ShouldBeFalse();
    }
}
