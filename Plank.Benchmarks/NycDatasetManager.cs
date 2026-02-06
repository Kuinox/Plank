using ParquetSharp;

namespace Plank.Benchmarks;

public sealed class NycDatasetManager
{
    static readonly HttpClient HttpClient = new();

    public async Task<string[]> EnsureFilesAsync(string dataDirectory, int fileCount, CancellationToken cancellationToken)
    {
        if (fileCount <= 0 || fileCount > NycDatasetCatalog.YellowTaxiParquetUrls.Length)
            throw new ArgumentOutOfRangeException(nameof(fileCount), $"fileCount must be between 1 and {NycDatasetCatalog.YellowTaxiParquetUrls.Length}.");

        Directory.CreateDirectory(dataDirectory);
        var files = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            var url = NycDatasetCatalog.YellowTaxiParquetUrls[i];
            var fileName = Path.GetFileName(url);
            var filePath = Path.Combine(dataDirectory, fileName);
            if (!File.Exists(filePath))
                await DownloadFileAsync(url, filePath, cancellationToken).ConfigureAwait(false);
            files[i] = filePath;
        }

        return files;
    }

    public BenchmarkDataContext LoadContext(string[] files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var valuesByFile = new int[files.Length][];
        var totalRows = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var values = LoadVendorIds(files[i]);
            valuesByFile[i] = values;
            totalRows = checked(totalRows + values.Length);
        }

        return new BenchmarkDataContext
        {
            SourceFiles = files,
            VendorIdsByFile = valuesByFile,
            TotalRows = totalRows
        };
    }

    static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        await using var response = await HttpClient.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);
        await response.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    static int[] LoadVendorIds(string filePath)
    {
        using var fileReader = new ParquetFileReader(filePath);
        var rowCount = checked((int)fileReader.FileMetaData.NumRows);
        var values = new int[rowCount];
        var offset = 0;
        for (var i = 0; i < fileReader.FileMetaData.NumRowGroups; i++)
        {
            using var rowGroupReader = fileReader.RowGroup(i);
            var groupRowCount = checked((int)rowGroupReader.MetaData.NumRows);
            var groupValues = ReadVendorIds(rowGroupReader, groupRowCount);
            groupValues.CopyTo(values, offset);
            offset += groupValues.Length;
        }

        return values;
    }

    static int[] ReadVendorIds(RowGroupReader rowGroupReader, int rowCount)
    {
        try
        {
            using var longReader = rowGroupReader.Column(0).LogicalReader<long?>();
            var source = longReader.ReadAll(rowCount);
            var destination = new int[source.Length];
            for (var i = 0; i < source.Length; i++)
                destination[i] = source[i].HasValue ? checked((int)source[i]!.Value) : 0;
            return destination;
        }
        catch (Exception)
        {
            using var intReader = rowGroupReader.Column(0).LogicalReader<int?>();
            var source = intReader.ReadAll(rowCount);
            var destination = new int[source.Length];
            for (var i = 0; i < source.Length; i++)
                destination[i] = source[i] ?? 0;
            return destination;
        }
    }
}
