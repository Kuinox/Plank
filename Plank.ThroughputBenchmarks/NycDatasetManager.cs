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
        var tripsByFile = new NycTripData[files.Length];
        var totalRows = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var trip = LoadTripData(files[i]);
            tripsByFile[i] = trip;
            totalRows = checked(totalRows + trip.RowCount);
        }

        return new BenchmarkDataContext
        {
            SourceFiles = files,
            TripsByFile = tripsByFile,
            TotalRows = totalRows
        };
    }

    public string[] ResolveExistingFiles(string dataDirectory, int fileCount)
    {
        if (fileCount <= 0 || fileCount > NycDatasetCatalog.YellowTaxiParquetUrls.Length)
            throw new ArgumentOutOfRangeException(nameof(fileCount), $"fileCount must be between 1 and {NycDatasetCatalog.YellowTaxiParquetUrls.Length}.");

        var files = new string[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            var fileName = Path.GetFileName(NycDatasetCatalog.YellowTaxiParquetUrls[i]);
            var filePath = Path.Combine(dataDirectory, fileName);
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Benchmark data file was not found: '{filePath}'. Run Plank.Benchmarks program entry once to download files.");
            files[i] = filePath;
        }

        return files;
    }

    static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        await using var response = await HttpClient.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);
        await response.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    static NycTripData LoadTripData(string filePath)
    {
        using var fileReader = new ParquetFileReader(filePath);
        var rowCount = checked((int)fileReader.FileMetaData.NumRows);
        var vendorId = new int?[rowCount];
        var pickupDateTime = new DateTime?[rowCount];
        var dropoffDateTime = new DateTime?[rowCount];
        var passengerCount = new long?[rowCount];
        var tripDistance = new double?[rowCount];
        var ratecodeId = new long?[rowCount];
        var storeAndFwdFlag = new string?[rowCount];
        var puLocationId = new int?[rowCount];
        var doLocationId = new int?[rowCount];
        var paymentType = new long?[rowCount];
        var fareAmount = new double?[rowCount];
        var extra = new double?[rowCount];
        var mtaTax = new double?[rowCount];
        var tipAmount = new double?[rowCount];
        var tollsAmount = new double?[rowCount];
        var improvementSurcharge = new double?[rowCount];
        var totalAmount = new double?[rowCount];
        var congestionSurcharge = new double?[rowCount];
        var airportFee = new double?[rowCount];

        var offset = 0;
        for (var i = 0; i < fileReader.FileMetaData.NumRowGroups; i++)
        {
            using var rowGroupReader = fileReader.RowGroup(i);
            var groupRowCount = checked((int)rowGroupReader.MetaData.NumRows);
            Copy(ReadInt32(rowGroupReader, 0, groupRowCount), vendorId, offset);
            Copy(ReadDateTime(rowGroupReader, 1, groupRowCount), pickupDateTime, offset);
            Copy(ReadDateTime(rowGroupReader, 2, groupRowCount), dropoffDateTime, offset);
            Copy(ReadInt64(rowGroupReader, 3, groupRowCount), passengerCount, offset);
            Copy(ReadDouble(rowGroupReader, 4, groupRowCount), tripDistance, offset);
            Copy(ReadInt64(rowGroupReader, 5, groupRowCount), ratecodeId, offset);
            Copy(ReadString(rowGroupReader, 6, groupRowCount), storeAndFwdFlag, offset);
            Copy(ReadInt32(rowGroupReader, 7, groupRowCount), puLocationId, offset);
            Copy(ReadInt32(rowGroupReader, 8, groupRowCount), doLocationId, offset);
            Copy(ReadInt64(rowGroupReader, 9, groupRowCount), paymentType, offset);
            Copy(ReadDouble(rowGroupReader, 10, groupRowCount), fareAmount, offset);
            Copy(ReadDouble(rowGroupReader, 11, groupRowCount), extra, offset);
            Copy(ReadDouble(rowGroupReader, 12, groupRowCount), mtaTax, offset);
            Copy(ReadDouble(rowGroupReader, 13, groupRowCount), tipAmount, offset);
            Copy(ReadDouble(rowGroupReader, 14, groupRowCount), tollsAmount, offset);
            Copy(ReadDouble(rowGroupReader, 15, groupRowCount), improvementSurcharge, offset);
            Copy(ReadDouble(rowGroupReader, 16, groupRowCount), totalAmount, offset);
            Copy(ReadDouble(rowGroupReader, 17, groupRowCount), congestionSurcharge, offset);
            Copy(ReadDouble(rowGroupReader, 18, groupRowCount), airportFee, offset);
            offset += groupRowCount;
        }

        return new NycTripData
        {
            VendorId = vendorId,
            PickupDateTime = pickupDateTime,
            DropoffDateTime = dropoffDateTime,
            PassengerCount = passengerCount,
            TripDistance = tripDistance,
            RatecodeId = ratecodeId,
            StoreAndFwdFlag = storeAndFwdFlag,
            PuLocationId = puLocationId,
            DoLocationId = doLocationId,
            PaymentType = paymentType,
            FareAmount = fareAmount,
            Extra = extra,
            MtaTax = mtaTax,
            TipAmount = tipAmount,
            TollsAmount = tollsAmount,
            ImprovementSurcharge = improvementSurcharge,
            TotalAmount = totalAmount,
            CongestionSurcharge = congestionSurcharge,
            AirportFee = airportFee
        };
    }

    static int?[] ReadInt32(RowGroupReader rowGroupReader, int columnIndex, int rowCount)
    {
        using var reader = rowGroupReader.Column(columnIndex).LogicalReader<int?>();
        return reader.ReadAll(rowCount);
    }

    static long?[] ReadInt64(RowGroupReader rowGroupReader, int columnIndex, int rowCount)
    {
        using var reader = rowGroupReader.Column(columnIndex).LogicalReader<long?>();
        return reader.ReadAll(rowCount);
    }

    static double?[] ReadDouble(RowGroupReader rowGroupReader, int columnIndex, int rowCount)
    {
        using var reader = rowGroupReader.Column(columnIndex).LogicalReader<double?>();
        return reader.ReadAll(rowCount);
    }

    static DateTime?[] ReadDateTime(RowGroupReader rowGroupReader, int columnIndex, int rowCount)
    {
        using var reader = rowGroupReader.Column(columnIndex).LogicalReader<DateTime?>();
        return reader.ReadAll(rowCount);
    }

    static string?[] ReadString(RowGroupReader rowGroupReader, int columnIndex, int rowCount)
    {
        using var reader = rowGroupReader.Column(columnIndex).LogicalReader<string?>();
        return reader.ReadAll(rowCount);
    }

    static void Copy<T>(T[] source, T[] destination, int offset)
        => Array.Copy(source, 0, destination, offset, source.Length);
}
