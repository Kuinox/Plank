using System.Buffers.Binary;
using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

internal sealed class LegacyStatisticsTests
{
    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(4, false)]
    [Arguments(5, true)]
    [Arguments(6, false)]
    public void LegacyStatisticsDoNotExcludeMatchingValues(int sample, bool hasBounds)
    {
        // Fixtures created with harness/legacy_bounds.py, independently verified with PyArrow.
        // Old binary (PARQUET-251), legacy unsigned (PARQUET-686), old and fixed binary decimal,
        // signed legacy, modern unsigned with conflicting legacy fields, and unknown writer binary.
        // Deprecated min/max are never exposed as bounds, even where they happen to be correct;
        // only sample 5 carries min_value/max_value.
        var bytes = Convert.FromBase64String(sample switch
        {
            0 => "UEFSMRUAFRQVFCwVBBUAFQYVBhwYAQEYAf8WAAAAAAEAAAABAQAAAP8VAhksSAZzY2hlbWEVAgAVDCUAGAV2YWx1ZQAWBBkcGRwmCBwVDBklAAYZGAV2YWx1ZRUAFgQWShZKJgg8GAEBGAH/FgAAAAAWShYEACgYcGFycXVldC1tciB2ZXJzaW9uIDEuNy4wAGgAAABQQVIx",
            1 => "UEFSMRUAFRAVECwVBBUAFQYVBhwYBAEAAAAYBP////8WAAAAAAEAAAD/////FQIZLEgGc2NoZW1hFQIAFQIlABgFdmFsdWUlGgAWBBkcGRwmCBwVAhklAAYZGAV2YWx1ZRUAFgQWUhZSJgg8GAQBAAAAGAT/////FgAAAAAWUhYEACgYcGFycXVldC1tciB2ZXJzaW9uIDEuOS4wAHAAAABQQVIx",
            2 => "UEFSMRUAFRQVFCwVBBUAFQYVBhwYAQEYAQAWAAAAAAEAAAD/AQAAAAEVAhksSAZzY2hlbWEVAgAVDCUAGAV2YWx1ZSUKFQAVCAAWBBkcGRwmCBwVDBklAAYZGAV2YWx1ZRUAFgQWShZKJgg8GAEBGAEAFgAAAAAWShYEACgYcGFycXVldC1tciB2ZXJzaW9uIDEuNy4wAG4AAABQQVIx",
            3 => "UEFSMRUAFRQVFCwVBBUAFQYVBhwYAQEYAf8WAAAAAAEAAAD/AQAAAAEVAhksSAZzY2hlbWEVAgAVDCUAGAV2YWx1ZSUKFQAVCAAWBBkcGRwmCBwVDBklAAYZGAV2YWx1ZRUAFgQWShZKJgg8GAEBGAH/FgAAAAAWShYEACgYcGFycXVldC1tciB2ZXJzaW9uIDEuOC4wAG4AAABQQVIx",
            4 => "UEFSMRUAFRAVECwVBBUAFQYVBhwYBAEAAAAYBP////8WAAAAAAEAAAD/////FQIZLEgGc2NoZW1hFQIAFQIlABgFdmFsdWUAFgQZHBkcJggcFQIZJQAGGRgFdmFsdWUVABYEFlIWUiYIPBgEAQAAABgE/////xYAAAAAFlIWBAAoGHBhcnF1ZXQtbXIgdmVyc2lvbiAxLjkuMABuAAAAUEFSMQ==",
            5 => "UEFSMRUAFRAVECwVBBUAFQYVBhwYBAEAAAAYBP////8WACgE/////xgEAQAAAAAAAAEAAAD/////FQIZLEgGc2NoZW1hFQIAFQIlABgFdmFsdWUlGgAWBBkcGRwmCBwVAhklAAYZGAV2YWx1ZRUAFgQWahZqJgg8GAQBAAAAGAT/////FgAoBP////8YBAEAAAAAAAAWahYEACgZcGFycXVldC1tciB2ZXJzaW9uIDEuMTAuMBkcHAAAAIIAAABQQVIx",
            6 => "UEFSMRUAFRQVFCwVBBUAFQYVBhwYAQEYAf8WAAAAAAEAAAABAQAAAP8VAhksSAZzY2hlbWEVAgAVDCUAGAV2YWx1ZQAWBBkcGRwmCBwVDBklAAYZGAV2YWx1ZRUAFgQWShZKJgg8GAEBGAH/FgAAAAAWShYEAABOAAAAUEFSMQ==",
            _ => throw new ArgumentOutOfRangeException(nameof(sample))
        });
        var bytesColumn = sample is 0 or 2 or 3 or 6;
        var decimalColumn = sample is 2 or 3;
        var signedColumn = sample == 4;
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes), (in ParquetDataPageMetadata page) => CouldMatch(page.Statistics));
        var group = reader.RowGroups[0];
        var metadata = group.GetColumnMetadata(0);
        if (!CouldMatch(metadata.Statistics))
            throw new InvalidOperationException("Chunk statistics excluded the known matching value.");
        var matches = 0;
        if (bytesColumn)
        {
            var target = decimalColumn ? (byte)0xff : (byte)1;
            foreach (var buffer in group.Column<byte>(0))
                for (var i = 0; i < buffer.Count; i++)
                    if (buffer.GetValue(i)[0] == target)
                        matches++;
        }
        else if (signedColumn)
        {
            foreach (var buffer in group.Column<int>(0))
                foreach (var value in buffer.Values)
                    if (value == -1)
                        matches++;
        }
        else
        {
            foreach (var buffer in group.Column<uint>(0))
                foreach (var value in buffer.Values)
                    if (value == 1)
                        matches++;
        }
        if (matches != 1)
            throw new InvalidOperationException("Page pruning excluded the known matching value.");
        using var pages = metadata.OpenPages();
        AssertBounds(metadata.Statistics);
        AssertBounds(pages[0].Statistics);

        bool CouldMatch(ParquetStatistics statistics)
        {
            if (!statistics.HasMinimum || !statistics.HasMaximum)
                return true;
            if (bytesColumn)
                return decimalColumn ? (sbyte)statistics.Minimum[0] <= -1 : statistics.Minimum[0] <= 1;
            if (signedColumn)
                return BinaryPrimitives.ReadInt32LittleEndian(statistics.Minimum) <= -1;
            return BinaryPrimitives.ReadUInt32LittleEndian(statistics.Minimum) <= 1;
        }

        void AssertBounds(ParquetStatistics statistics)
        {
            if (statistics.HasMinimum != hasBounds || statistics.HasMaximum != hasBounds || statistics.NullCount != 0)
                throw new InvalidOperationException("Legacy bounds were exposed, or the valid null count was lost.");
        }
    }
}
