using System.Buffers.Binary;
using System.Diagnostics;
using Plank.Schema;

namespace Plank.Writing;

public sealed partial class ParquetWriter
{
    internal sealed partial class RowGroupState
    {
        void WriteColumn(ParquetWriter writer, int ordinal)
        {
            ref var state = ref _columnStore.Data.ColumnStates[ordinal];
            var rowCount = state.RowCount;
            var valueCount = state.ValueCount;
            if (_columnStore.Progress.RowCount < 0)
                _columnStore.Progress.RowCount = rowCount;
            if (_columnStore.Progress.RowCount != rowCount)
                throw new InvalidOperationException($"Column ordinal {ordinal} has {rowCount} rows but row group expects {_columnStore.Progress.RowCount}.");

            var writeStarted = Stopwatch.GetTimestamp();
            var waitForWriteTicks = state.EncodedTimestampTicks > 0 ? Math.Max(0, writeStarted - state.EncodedTimestampTicks) : 0;
            writer.BeginColumnWriteTiming(ordinal);
            var offset = writer._position;
            long totalUncompressedSize;
            long totalCompressedSize;
            var plan = SelectPageWritePlan(writer, ordinal, ref state);
            ExecutePageWritePlan(writer, ordinal, ref state, plan, out totalUncompressedSize, out totalCompressedSize);
            var writeTicks = writer.EndColumnWriteTiming(ordinal);
            var bytesWritten = checked((int)(writer._position - offset));
            writer._options.Log.ColumnWriteMetricsObserved(
                _columnStore.Schema.Columns[ordinal].Name,
                rowCount,
                valueCount,
                bytesWritten,
                state.EncodeDurationTicks,
                state.CompressionDurationTicks,
                waitForWriteTicks,
                writeTicks);
            if (state.StringRowCount > 0)
                writer._options.Log.StringEncodingMetricsObserved(
                    _columnStore.Schema.Columns[ordinal].Name,
                    state.StringRowCount,
                    state.StringNonNullCount,
                    state.StringSizePassTicks,
                    state.StringDefinitionLevelsTicks,
                    0,
                    state.StringUtf8WritePassTicks);

            _columnStore.Data.ColumnMetadata[ordinal] = new ColumnChunkMetadata(offset, state.ValueCount, totalUncompressedSize, totalCompressedSize, state.Encoding, state.Compression);
            state.ExternalData = default;
            if (state.ExternalDataOwner is not null)
            {
                state.ExternalDataOwner.Dispose();
                state.ExternalDataOwner = null;
            }
            ClearColumnDataMetrics(ref state);
            Volatile.Write(ref state.WriteState, WriteStateWritten);
        }

        readonly struct PageWritePlan
        {
            internal PageWritePlan(PageWriteMode mode, int bytesPerValue, int splitValueCount)
            {
                Mode = mode;
                BytesPerValue = bytesPerValue;
                SplitValueCount = splitValueCount;
            }

            internal PageWriteMode Mode { get; }
            internal int BytesPerValue { get; }
            internal int SplitValueCount { get; }
        }

        enum PageWriteMode
        {
            SinglePage,
            SplitFixedWidthRequired,
            SplitLevelFixedWidth,
            SplitVariableWidthRequired
        }

        PageWritePlan SelectPageWritePlan(ParquetWriter writer, int ordinal, ref ColumnState state)
        {
            if (TryCreateFixedWidthRequiredSplitPlan(ordinal, ref state, out var splitValueCount, out var bytesPerValue))
                return new PageWritePlan(PageWriteMode.SplitFixedWidthRequired, bytesPerValue, splitValueCount);
            if (TryCreateLevelFixedWidthSplitPlan(writer, ordinal, ref state, out bytesPerValue))
                return new PageWritePlan(PageWriteMode.SplitLevelFixedWidth, bytesPerValue, splitValueCount: 0);
            if (CanUseVariableWidthRequiredSplit(ordinal, ref state))
                return new PageWritePlan(PageWriteMode.SplitVariableWidthRequired, bytesPerValue: 0, splitValueCount: 0);

            return new PageWritePlan(PageWriteMode.SinglePage, bytesPerValue: 0, splitValueCount: 0);
        }

        void ExecutePageWritePlan(ParquetWriter writer, int ordinal, ref ColumnState state, PageWritePlan plan,
            out long totalUncompressedSize, out long totalCompressedSize)
        {
            switch (plan.Mode)
            {
                case PageWriteMode.SplitFixedWidthRequired:
                    WriteSplitPages(writer, ordinal, ref state, plan.SplitValueCount, plan.BytesPerValue,
                        out totalUncompressedSize, out totalCompressedSize);
                    break;
                case PageWriteMode.SplitLevelFixedWidth:
                    WriteSplitLevelFixedWidthPages(writer, ordinal, ref state, plan.BytesPerValue,
                        out totalUncompressedSize, out totalCompressedSize);
                    break;
                case PageWriteMode.SplitVariableWidthRequired:
                    WriteSplitVariableWidthPages(writer, ordinal, ref state, out totalUncompressedSize,
                        out totalCompressedSize);
                    break;
                default:
                    WriteSinglePage(writer, ordinal, ref state, out totalUncompressedSize, out totalCompressedSize);
                    break;
            }
        }

        bool TryCreateFixedWidthRequiredSplitPlan(int ordinal, ref ColumnState state, out int splitValueCount, out int bytesPerValue)
        {
            splitValueCount = 0;
            bytesPerValue = 0;
            if (state.DefinitionLevelsByteLength != 0 || state.RepetitionLevelsByteLength != 0)
                return false;
            if (_columnStore.Schema.Columns[ordinal].Options.Repetition is not ParquetRepetition.Required and not ParquetRepetition.Unspecified)
                return false;
            if (_columnStore.Schema.Columns[ordinal].PhysicalType == ParquetPhysicalType.Boolean)
                return false;
            if (!ColumnCodec.TryGetFixedWidthBytes(_columnStore.Schema.Columns[ordinal].PhysicalType, out bytesPerValue))
                return false;
            if (state.ValueCount <= 1)
                return false;

            var byValues = _options.RowGroupOptions.MaxPageValueCount > 0 ? _options.RowGroupOptions.MaxPageValueCount : int.MaxValue;
            var byBytes = _options.RowGroupOptions.MaxPageBytes > 0 ? Math.Max(1, _options.RowGroupOptions.MaxPageBytes / bytesPerValue) : int.MaxValue;
            splitValueCount = Math.Min(byValues, byBytes);
            if (splitValueCount <= 0)
                splitValueCount = 1;
            return splitValueCount < state.ValueCount;
        }

        bool CanUseVariableWidthRequiredSplit(int ordinal, ref ColumnState state)
        {
            if (state.DefinitionLevelsByteLength != 0 || state.RepetitionLevelsByteLength != 0)
                return false;
            if (_columnStore.Schema.Columns[ordinal].Options.Repetition is not ParquetRepetition.Required and not ParquetRepetition.Unspecified)
                return false;
            if (_columnStore.Schema.Columns[ordinal].PhysicalType is not ParquetPhysicalType.ByteArray)
                return false;
            if (state.ValueCount <= 1)
                return false;

            var hasByteLimit = _options.RowGroupOptions.MaxPageBytes > 0 && _options.RowGroupOptions.MaxPageBytes < int.MaxValue;
            var hasCountLimit = _options.RowGroupOptions.MaxPageValueCount > 0 && _options.RowGroupOptions.MaxPageValueCount < int.MaxValue;
            return hasByteLimit || hasCountLimit;
        }

        bool TryCreateLevelFixedWidthSplitPlan(ParquetWriter writer, int ordinal, ref ColumnState state,
            out int bytesPerValue)
        {
            bytesPerValue = 0;
            if (state.DefinitionLevelsByteLength == 0 && state.RepetitionLevelsByteLength == 0)
                return false;
            if (_columnStore.Schema.Columns[ordinal].PhysicalType == ParquetPhysicalType.Boolean)
                return false;
            if (!ColumnCodec.TryGetFixedWidthBytes(_columnStore.Schema.Columns[ordinal].PhysicalType, out bytesPerValue))
                return false;
            if (state.ValueCount <= 1)
                return false;
            var hasValueLimit = _options.RowGroupOptions.MaxPageValueCount > 0 && _options.RowGroupOptions.MaxPageValueCount < int.MaxValue;
            var hasByteLimit = _options.RowGroupOptions.MaxPageBytes > 0 && _options.RowGroupOptions.MaxPageBytes < int.MaxValue;
            return hasValueLimit || hasByteLimit;
        }

        void WriteSplitPages(ParquetWriter writer, int ordinal, ref ColumnState state, int splitValueCount, int bytesPerValue, out long totalUncompressedSize, out long totalCompressedSize)
        {
            totalUncompressedSize = 0;
            totalCompressedSize = 0;
            var valuesRemaining = state.ValueCount;
            var currentValueOffset = 0;
            while (valuesRemaining > 0)
            {
                var pageValueCount = Math.Min(valuesRemaining, splitValueCount);
                var pageEncodedOffset = currentValueOffset * bytesPerValue;
                var pageEncodedLength = pageValueCount * bytesPerValue;
                WritePage(
                    writer,
                    ordinal,
                    ref state,
                    pageValueCount,
                    pageValueCount,
                    nullCount: 0,
                    definitionLevelsByteLength: 0,
                    repetitionLevelsByteLength: 0,
                    pageEncodedOffset,
                    pageEncodedLength,
                    ref totalUncompressedSize,
                    ref totalCompressedSize);
                currentValueOffset += pageValueCount;
                valuesRemaining -= pageValueCount;
            }
        }

        void WriteSinglePage(ParquetWriter writer, int ordinal, ref ColumnState state, out long totalUncompressedSize, out long totalCompressedSize)
        {
            totalUncompressedSize = 0;
            totalCompressedSize = 0;
            WritePage(
                writer,
                ordinal,
                ref state,
                state.ValueCount,
                state.RowCount,
                state.NullCount,
                state.DefinitionLevelsByteLength,
                state.RepetitionLevelsByteLength,
                encodedOffset: 0,
                encodedLength: state.EncodedLength,
                ref totalUncompressedSize,
                ref totalCompressedSize);
        }

        void WriteSplitVariableWidthPages(ParquetWriter writer, int ordinal, ref ColumnState state, out long totalUncompressedSize, out long totalCompressedSize)
        {
            totalUncompressedSize = 0;
            totalCompressedSize = 0;

            var source = GetSourceSpan(ref state, 0, state.EncodedLength);
            var maxValues = _options.RowGroupOptions.MaxPageValueCount > 0 ? _options.RowGroupOptions.MaxPageValueCount : int.MaxValue;
            var maxBytes = _options.RowGroupOptions.MaxPageBytes > 0 ? _options.RowGroupOptions.MaxPageBytes : int.MaxValue;

            var valueIndex = 0;
            var payloadOffset = 0;
            while (valueIndex < state.ValueCount)
            {
                var pageStartOffset = payloadOffset;
                var pageBytes = 0;
                var pageValues = 0;

                while (valueIndex < state.ValueCount)
                {
                    if (payloadOffset > source.Length - sizeof(int))
                        throw new InvalidOperationException("Invalid byte-array payload while splitting pages.");

                    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(payloadOffset, sizeof(int)));
                    if (valueLength < 0)
                        throw new InvalidOperationException("Negative byte-array value length is invalid.");
                    var entryLength = checked(sizeof(int) + valueLength);
                    if (payloadOffset > source.Length - entryLength)
                        throw new InvalidOperationException("Invalid byte-array payload while splitting pages.");

                    var wouldExceedCount = pageValues >= maxValues;
                    var wouldExceedBytes = pageValues > 0 && pageBytes > maxBytes - entryLength;
                    if (wouldExceedCount || wouldExceedBytes)
                        break;

                    payloadOffset += entryLength;
                    pageBytes += entryLength;
                    pageValues++;
                    valueIndex++;
                }

                if (pageValues == 0)
                {
                    // Soft limit: always emit at least one full value, even if it's larger than page target.
                    if (payloadOffset > source.Length - sizeof(int))
                        throw new InvalidOperationException("Invalid byte-array payload while splitting pages.");
                    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(payloadOffset, sizeof(int)));
                    if (valueLength < 0)
                        throw new InvalidOperationException("Negative byte-array value length is invalid.");
                    var entryLength = checked(sizeof(int) + valueLength);
                    if (payloadOffset > source.Length - entryLength)
                        throw new InvalidOperationException("Invalid byte-array payload while splitting pages.");

                    payloadOffset += entryLength;
                    pageBytes += entryLength;
                    pageValues = 1;
                    valueIndex++;
                }

                WritePage(
                    writer,
                    ordinal,
                    ref state,
                    valueCount: pageValues,
                    rowCount: pageValues,
                    nullCount: 0,
                    definitionLevelsByteLength: 0,
                    repetitionLevelsByteLength: 0,
                    encodedOffset: pageStartOffset,
                    encodedLength: pageBytes,
                    ref totalUncompressedSize,
                    ref totalCompressedSize);

            }
        }
    }
}
