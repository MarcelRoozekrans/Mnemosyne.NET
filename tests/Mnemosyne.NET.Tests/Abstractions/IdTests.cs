using Xunit;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.Tests.Abstractions;

public class IdTests
{
    // Guid.CompareTo orders by .NET field layout, not by RFC 9562 byte order, so a version 7 GUID's
    // timestamp prefix only sorts correctly when compared as big-endian bytes.
    private static byte[] Timestamp(Guid g) => g.ToByteArray(bigEndian: true)[..6];

    private static int ByteOrder(Guid x, Guid y) =>
        x.ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(y.ToByteArray(bigEndian: true));

    [Fact]
    public void ReasonletId_New_IsVersion7AndAscending()
    {
        var a = ReasonletId.New();
        var b = ReasonletId.New();
        while (Timestamp(a.Value).SequenceEqual(Timestamp(b.Value)))
        {
            b = ReasonletId.New();
        }
        Assert.Equal(7, a.Value.Version);
        Assert.True(ByteOrder(a.Value, b.Value) < 0);
    }

    [Fact]
    public void ReasonletId_Default_IsDefault()
    {
        Assert.True(default(ReasonletId).IsDefault);
        Assert.False(ReasonletId.New().IsDefault);
    }

    [Fact]
    public void ExecutionId_New_IsVersion7AndAscending()
    {
        var a = ExecutionId.New();
        var b = ExecutionId.New();
        while (Timestamp(a.Value).SequenceEqual(Timestamp(b.Value)))
        {
            b = ExecutionId.New();
        }
        Assert.Equal(7, a.Value.Version);
        Assert.True(ByteOrder(a.Value, b.Value) < 0);
    }

    [Fact]
    public void ExecutionId_Default_IsDefault()
    {
        Assert.True(default(ExecutionId).IsDefault);
        Assert.False(ExecutionId.New().IsDefault);
    }

    [Fact]
    public void ReasonletId_ToString_IsTheBareGuid()
    {
        var id = ReasonletId.New();
        Assert.Equal(id.Value.ToString(), id.ToString());
        Assert.Equal(id.Value.ToString(), $"{id}");
        Assert.Equal(Guid.Empty.ToString(), default(ReasonletId).ToString());
    }

    [Fact]
    public void ExecutionId_ToString_IsTheBareGuid()
    {
        var id = ExecutionId.New();
        Assert.Equal(id.Value.ToString(), id.ToString());
        Assert.Equal(id.Value.ToString(), $"{id}");
        Assert.Equal(Guid.Empty.ToString(), default(ExecutionId).ToString());
    }
}
