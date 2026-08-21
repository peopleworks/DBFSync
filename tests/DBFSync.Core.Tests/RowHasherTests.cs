using PeopleWorks.DBFSync;

namespace DBFSync.Core.Tests;

public sealed class RowHasherTests
{
    private static readonly DbfColumn[] Columns =
    [
        new("codigo", "codigo", DbfColumnKind.Integer, 10, 10, 0, typeof(long)),
        new("nombre", "nombre", DbfColumnKind.Text, 40, 0, 0, typeof(string)),
        new("balance", "balance", DbfColumnKind.Decimal, 12, 12, 2, typeof(decimal))
    ];

    [Fact]
    public void SameValuesProduceSameHash()
    {
        using var hasher = new RowHasher();

        byte[] first = hasher.Compute(Columns, [10L, "CLIENTE", 125.50m]);
        byte[] second = hasher.Compute(Columns, [10L, "CLIENTE", 125.50m]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ChangedColumnProducesDifferentHashForSameRecordNumber()
    {
        const long sameRecordNumber = 42;
        using var hasher = new RowHasher();

        byte[] before = hasher.Compute(Columns, [10L, "CLIENTE", 125.50m]);
        byte[] after = hasher.Compute(Columns, [10L, "CLIENTE", 126.00m]);
        var firstRow = new DbfRow(sameRecordNumber, before, []);
        var changedRow = new DbfRow(sameRecordNumber, after, []);

        Assert.Equal(firstRow.RecordNumber, changedRow.RecordNumber);
        Assert.NotEqual(firstRow.Hash, changedRow.Hash);
    }
}

