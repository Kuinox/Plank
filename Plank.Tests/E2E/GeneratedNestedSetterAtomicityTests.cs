using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNestedSetterAtomicityTests
{
    [Test]
    public async Task FailedGroupAssignmentPreservesPreviousValue()
    {
        using var stream = new MemoryStream();
        using (var writer = AtomicSetterRow.CreateRowWriter(stream, maxParallelism: 1))
        {
            var row = writer.GetRow();
            row.Group = new AtomicSetterGroup { First = 11, Second = 12 };
            try
            {
                row.Group = new AtomicSetterGroup { First = 21, Second = -1 };
                throw new InvalidOperationException("Expected the second getter to throw.");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
            writer.Complete();
        }

        using var reader = AtomicSetterRow.CreateRowReader(new MemoryStream(stream.ToArray()));
        await Assert.That(reader.MoveNext()).IsTrue();
        var group = reader.Current.Group;
        await Assert.That(group.First).IsEqualTo(11);
        await Assert.That(group.Second).IsEqualTo(12);
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class AtomicSetterRow
{
    public AtomicSetterGroup Group { get; set; } = new();
}

internal sealed class AtomicSetterGroup
{
    public int First { get; set; }
    public int Second
    {
        get => field >= 0 ? field : throw new ArgumentOutOfRangeException(nameof(Second));
        set;
    }
}
