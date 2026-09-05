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
