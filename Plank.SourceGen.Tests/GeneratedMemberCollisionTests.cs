using Microsoft.CodeAnalysis;

namespace Plank.SourceGen.Tests;

internal sealed class GeneratedMemberCollisionTests
{
    [Test]
    public async Task SchemaPropertiesMayUseGeneratedApiMemberNames()
    {
        const string source = """
            using Plank.Schema;

            namespace Regression;

            [ParquetSchema]
            partial class GeneratedMemberCollisionSchema
            {
                public int Schema { get; set; }

                public int Writer { get; set; }

                public int Reader { get; set; }
                public int RowCursor { get; set; }
                public int NextRow { get; set; }
                public int Refresh { get; set; }
                public int Buffers { get; set; }

                static void UseGeneratedApi(System.IO.Stream stream, Plank.Writing.RowGroupWriter rowGroupWriter)
                {
                    _ = Schema1;
                    Writer1 writer = CreateRowWriter(rowGroupWriter);
                    using Reader1 reader = CreateReader(stream);
                    using var pipeline = CreateRowWriter(stream);
                    RowCursor1 cursor = pipeline.CreateCursor();
                    cursor.NextRow1();
                    cursor.RowCursor = 1;
                    cursor.NextRow = 2;
                    cursor.Refresh = 3;
                    cursor.Buffers = 4;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(
            new GeneratorTestHarness.SourceFile("GeneratedMemberCollisionSchema.cs", source));
        var errors = result.CompilationDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        await Assert.That(errors).IsEmpty();
    }
}

internal sealed class GeneratedMemberNameMatrixTests
{
    static readonly string[] ApiNames =
    [
        "Schema", "Writer", "Reader", "DatasetWriter", "Route", "Row", "ReadRow", "Projection",
        "RowCursor", "RowReader", "ReadRowGroup", "ReadRowGroupCollection", "SchemaWriter", "RowGroup", "PipelineWriter",
        "BufferSlot", "CreateDatasetWriter", "CreateRowWriter", "CreateRowReader", "CreateReader", "CreateWriter"
    ];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GeneratedApisAvoidAllSchemaMemberNames(bool nested)
    {
        var properties = string.Join("\n", ApiNames.Select(name => $"public int {name} {{ get; set; }}\npublic int {name}1 {{ get; set; }}"));
        var source = $$"""
            using Plank.Schema;
            namespace Regression;
            [ParquetSchema(AllowAllocatingValues = true)]
            partial class CollisionSchema
            {
                {{properties}}
                {{(nested ? "public int[] Values { get; set; } = [];" : "")}}
                static void Use(System.IO.Stream stream)
                {
                    _ = Schema2;
                    using var writer = CreateRowWriter2(stream);
                    Row2 row = writer.GetRow();
                    row.Row = 7;
                    using RowReader2 reader = CreateRowReader2(stream, Projection2.Row);
                }
            }
            """;
        await AssertCompiles(source).ConfigureAwait(false);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SchemaTypeMayUseAnyGeneratedApiName(bool nested)
    {
        foreach (var name in ApiNames)
        {
            await AssertCompiles($$"""
                using Plank.Schema;
                namespace Regression;
                [ParquetSchema(AllowAllocatingValues = true)]
                partial class {{name}}
                {
                    public int Value { get; set; }
                    {{(nested ? "public int[] Values { get; set; } = [];" : "")}}
                }
                """).ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ColumnSelectorsBackingFieldsAndSettersAvoidPropertyNames(bool nested)
    {
        var source = $$"""
            using Plank.Schema;
            namespace Regression;
            [ParquetSchema(AllowAllocatingValues = true)]
            partial class CollisionSchema
            {
                public int All { get; set; }
                public int None { get; set; }
                public int Columns { get; set; }
                public int _columns { get; set; }
                public int _index { get; set; }
                public int _core { get; set; }
                public int _ownerSlot { get; set; }
                public int Write { get; set; }
                public int Value { get; set; }
                public int _Value { get; set; }
                public int _rowGroupWriter { get; set; }
                public int s_rowApiColumns { get; set; }
                public int s_ValueRowApiColumn { get; set; }
                public int SetPayload { get; set; }
                public System.ReadOnlyMemory<byte> Payload { get; set; }
                {{(nested ? "public int[] Values { get; set; } = [];" : "")}}
                static void Use(System.IO.Stream stream)
                {
                    var projection = Projection.All | Projection.All1 | Projection.None | Projection.None1;
                    using var reader = CreateRowReader(stream, projection);
                    using var writer = CreateRowWriter(stream);
                    var row = writer.GetRow();
                    row._index = 3;
                    row._core = 4;
                    row.SetPayload = 5;
                    {{(nested ? "" : "row.SetPayload1(default(System.Buffers.IMemoryOwner<byte>)!);")}}
                }
            }
            """;
        await AssertCompiles(source).ConfigureAwait(false);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CursorMembersAvoidCopiedProperties(bool nested)
    {
        await AssertCompiles($$"""
            using Plank.Schema;
            namespace Regression;
            [ParquetSchema(AllowAllocatingValues = true)]
            partial class CollisionSchema
            {
                public int RowCursor { get; set; }
                public int NextRow { get; set; }
                public int NextRow1 { get; set; }
                public int Buffers { get; set; }
                public int Refresh { get; set; }
                public int _buffers { get; set; }
                public int _writer { get; set; }
                public int _index { get; set; }
                public int _ownerSlot { get; set; }
                public int _bufferGeneration { get; set; }
                {{(nested ? "public int[] Values { get; set; } = [];" : "")}}
                static void Use(System.IO.Stream stream)
                {
                    using var writer = CreateRowWriter(stream);
                    RowCursor1 cursor = writer.CreateCursor();
                    cursor.NextRow2();
                    cursor.RowCursor = 1;
                    cursor.NextRow = 2;
                    cursor.Buffers = 3;
                    cursor._buffers = 4;
                    cursor._writer = 5;
                    cursor._index = 6;
                    cursor._ownerSlot = 7;
                    cursor._bufferGeneration = 8;
                }
            }
            """).ConfigureAwait(false);
    }

    [Test]
    public async Task UnrelatedSchemaMembersDoNotRenameNestedPublicApis()
    {
        await AssertCompiles("""
            using Plank.Schema;
            namespace Regression;
            [ParquetSchema]
            partial class All
            {
                public int Value { get; set; }
                public System.ReadOnlyMemory<byte> Payload { get; set; }
                public void Write() { }
                public void SetPayload() { }
                static void Use(System.IO.Stream stream, System.Buffers.IMemoryOwner<byte> owner)
                {
                    _ = Projection.All;
                    using var writer = CreateWriter(stream);
                    var group = writer.StartRowGroup();
                    group.Write(group.Value);
                    using var rows = CreateRowWriter(stream);
                    rows.GetRow().SetPayload(owner);
                }
            }
            """).ConfigureAwait(false);
    }

    [Test]
    public async Task NestedMaterializersAvoidGeneratedTypesAndUserMembers()
    {
        await AssertCompiles("""
            using Plank.Schema;
            namespace Regression;
            [ParquetSchema(AllowAllocatingValues = true)]
            partial class CollisionSchema
            {
                public int[] Row { get; set; } = [];
                public int ReadRow1 { get; set; }
                public int ProjectRow_Element { get; set; }
                public int GeneratedNestedToUnixMicroseconds { get; set; }
                public System.DateTime[] Times { get; set; } = [];
                public PayloadRecord Data { get; set; } = new();
                public int ReadData_PayloadBinary { get; set; }
                public int s_Data_PayloadRowApiColumn { get; set; }
                public int get_Schema() => 0;
                public int get_Schema1() => 0;
                static void Use(System.IO.Stream stream)
                {
                    _ = Schema2;
                    using var writer = CreateRowWriter(stream);
                    var row = writer.GetRow();
                    row.Row = [1, 2, 3];
                    using var reader = CreateRowReader(stream);
                    _ = reader.Current.Row;
                }
            }
            sealed class PayloadRecord
            {
                public byte[] Payload { get; set; } = [];
            }
            """).ConfigureAwait(false);
    }

    static async Task AssertCompiles(string source)
    {
        var result = GeneratorTestHarness.Run(new GeneratorTestHarness.SourceFile("CollisionSchema.cs", source));
        var errors = result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString()).ToArray();
        await Assert.That(result.GeneratorExceptions).IsEmpty();
        await Assert.That(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(result.GeneratedSources).HasSingleItem();
        await Assert.That(errors).IsEmpty();
    }
}
