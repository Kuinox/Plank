"""Copy compressed pages into new row groups, without decoding their payloads."""
import bisect
import copy
import mmap
import struct
from pathlib import Path

from fastparquet.cencoding import NumpyIO, ThriftObject
from fastparquet import parquet_thrift as thrift


def footer(path):
    with open(path, 'rb') as f:
        f.seek(0, 2)
        size = f.tell()
        if size < 12:
            raise ValueError('File is too short to be Parquet')
        f.seek(0)
        if f.read(4) != b'PAR1':
            raise ValueError('Only unencrypted PAR1 files are supported')
        f.seek(-8, 2)
        trailer = f.read(8)
        length = struct.unpack('<I', trailer[:4])[0]
        if trailer[4:] != b'PAR1' or length > size - 12:
            raise ValueError('Invalid Parquet footer')
        f.seek(size - length - 8)
        return ThriftObject.from_buffer(f.read(length), 'FileMetaData'), length, size - length - 8


class Pages:
    def __init__(self, path):
        self.path = Path(path)
        self.meta, self.footer_bytes, footer_offset = footer(path)
        schema = self.meta.schema
        if (len(schema) != schema[0].num_children + 1 or
                any(x.num_children or x.repetition_type == 2 for x in schema[1:])):
            raise ValueError('Page-preserving repacking currently supports flat, non-repeated columns')
        self.columns = [[] for _ in schema[1:]]
        common = None
        mandatory = set()
        previous = None
        row = 0
        with open(path, 'rb') as f, mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ) as data:
            for group in self.meta.row_groups:
                if group.num_rows <= 0:
                    continue
                if len(group.columns) != len(self.columns):
                    raise ValueError('Inconsistent column count')
                signatures = []
                group_boundaries = []
                for i, chunk in enumerate(group.columns):
                    if chunk.file_path or chunk.crypto_metadata or chunk.encrypted_column_metadata:
                        raise ValueError('External or encrypted column chunks are not supported')
                    md = chunk.meta_data
                    start = min(x for x in (md.data_page_offset, md.dictionary_page_offset) if x is not None)
                    end = start + md.total_compressed_size
                    if start < 4 or end > footer_offset:
                        raise ValueError('Column chunk falls outside the data region')
                    buf = NumpyIO(data[start:end])
                    dictionary = None
                    pages = []
                    rows = row
                    boundaries = {row}
                    while buf.tell() < buf.len:
                        offset = buf.tell()
                        header = ThriftObject.from_buffer(buf, 'PageHeader')
                        header_size = buf.tell() - offset
                        length = header.compressed_page_size
                        if length < 0 or length > buf.len - buf.tell():
                            raise ValueError('Invalid compressed page size')
                        buf.seek(length, 1)
                        raw = bytes(data[start + offset:start + buf.tell()])
                        uncompressed = header_size + header.uncompressed_page_size
                        if header.type == 2:
                            if dictionary is not None or pages:
                                raise ValueError('Invalid dictionary page placement')
                            dictionary = (raw, uncompressed)
                        elif header.type in (0, 3):
                            h = header.data_page_header if header.type == 0 else header.data_page_header_v2
                            count = h.num_values if header.type == 0 else h.num_rows
                            if count <= 0 or (header.type == 3 and count != h.num_values):
                                raise ValueError('Unsupported or invalid page row count')
                            pages.append((rows, rows + count, raw, uncompressed, h.encoding))
                            rows += count
                            boundaries.add(rows)
                        else:
                            raise ValueError('Unsupported page type')
                    if rows != row + group.num_rows or md.num_values != group.num_rows:
                        raise ValueError('Page row counts do not match the row group')
                    signature = (md.codec, dictionary[0] if dictionary else None)
                    signatures.append(signature)
                    group_boundaries.append(boundaries)
                    self.columns[i].extend((p, dictionary, md) for p in pages)
                aligned = set.intersection(*group_boundaries)
                common = aligned if common is None else common | aligned
                if previous is not None and signatures != previous:
                    mandatory.add(row)
                previous = signatures
                row += group.num_rows
        if row != self.meta.num_rows or row == 0:
            raise ValueError('Empty file or inconsistent total row count')
        self.rows = row
        self.boundaries = sorted(common)
        self.mandatory = sorted(mandatory)

    def layout(self, target):
        if target <= 0:
            raise ValueError('Row-group sizes must be positive')
        cuts = [0]
        while cuts[-1] < self.rows:
            start = cuts[-1]
            at = bisect.bisect_left(self.boundaries, min(self.rows, start + target))
            end = self.boundaries[at]
            force = bisect.bisect_right(self.mandatory, start)
            if force < len(self.mandatory):
                end = min(end, self.mandatory[force])
            cuts.append(end)
        return cuts

    def write(self, path, cuts):
        positions = [0] * len(self.columns)
        groups = []
        with open(path, 'xb') as output:
            output.write(b'PAR1')
            for start, end in zip(cuts, cuts[1:]):
                chunks = []
                for i, column in enumerate(self.columns):
                    begin = positions[i]
                    while positions[i] < len(column) and column[positions[i]][0][0] < end:
                        positions[i] += 1
                    selected = column[begin:positions[i]]
                    if not selected or selected[0][0][0] != start or selected[-1][0][1] != end:
                        raise ValueError('Cut is not aligned across columns')
                    dictionary = selected[0][1]
                    md = copy.deepcopy(selected[0][2])
                    if any(x[2].codec != md.codec or x[1] != dictionary for x in selected):
                        raise ValueError('Incompatible dictionaries/codecs in a new row group')
                    offset = output.tell()
                    compressed = uncompressed = 0
                    md.dictionary_page_offset = None
                    if dictionary:
                        md.dictionary_page_offset = output.tell()
                        output.write(dictionary[0])
                        compressed += len(dictionary[0])
                        uncompressed += dictionary[1]
                    md.data_page_offset = output.tell()
                    for page, _, _ in selected:
                        output.write(page[2])
                        compressed += len(page[2])
                        uncompressed += page[3]
                    md.num_values = end - start
                    md.total_compressed_size = compressed
                    md.total_uncompressed_size = uncompressed
                    md.contents[2] = sorted(set(e for _, _, source in selected for e in source.encodings))
                    md.statistics = None
                    md.encoding_stats = None
                    md.index_page_offset = None
                    md.bloom_filter_offset = None
                    md.contents.pop(15, None)
                    chunks.append(thrift.ColumnChunk(file_offset=offset, meta_data=md))
                groups.append(thrift.RowGroup(columns=chunks, num_rows=end-start,
                    total_byte_size=sum(c.meta_data.total_uncompressed_size for c in chunks),
                    total_compressed_size=sum(c.meta_data.total_compressed_size for c in chunks)))
            meta = copy.deepcopy(self.meta)
            meta.row_groups = groups
            raw = bytes(meta.to_bytes())
            output.write(raw)
            output.write(struct.pack('<I', len(raw)))
            output.write(b'PAR1')
        check, _, _ = footer(path)
        if check.num_rows != self.rows or len(check.row_groups) != len(cuts) - 1:
            raise ValueError('Generated footer failed validation')
        return check
