"""Page-preserving repacking with bounded payload/footer memory.

Input must be flat, dictionary-free and have pages aligned at page_rows.
The footer is serialized a row group at a time, rather than retaining millions
of ColumnChunk objects. Indexes contain offsets only; payloads stay on disk.
"""
import copy
import math
import os
import struct
from pathlib import Path

import numpy as np
from fastparquet.cencoding import NumpyIO, ThriftObject
from fastparquet import parquet_thrift as thrift


def varint(value):
    result = bytearray()
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def integer(value):
    return varint((value << 1) ^ (value >> 63))


def list_header(count):
    return bytes([(count << 4) | 12]) if count < 15 else b'\xfc' + varint(count)


def read_footer(path):
    with Path(path).open('rb') as f:
        f.seek(-8, 2)
        size, magic = struct.unpack('<I4s', f.read(8))
        if magic != b'PAR1':
            raise ValueError('Not an unencrypted Parquet file')
        f.seek(-8-size, 2)
        return ThriftObject.from_buffer(f.read(size), 'FileMetaData')


class OffsetPages:
    def __init__(self, path, page_rows=1000):
        self.path = Path(path)
        self.page_rows = page_rows
        self.meta = read_footer(path)
        # Keep schema bytes verbatim: older fastparquet serializers do not
        # retain all LogicalType union wire types (notably Integer.bitWidth).
        with self.path.open('rb') as f:
            f.seek(-8,2)
            footer_size=struct.unpack('<I',f.read(4))[0]
            f.seek(-8-footer_size,2)
            wire=NumpyIO(f.read(footer_size))
        if bytes(wire.read(1)) != b'\x15':raise ValueError('Unexpected footer version field')
        while bytes(wire.read(1))[0] & 128:pass
        if bytes(wire.read(1)) != b'\x19':raise ValueError('Unexpected schema field')
        schema_start=wire.tell()
        header=bytes(wire.read(1))[0]
        if header>>4==15:
            while bytes(wire.read(1))[0]&128:pass
        for _ in self.meta.schema:ThriftObject.from_buffer(wire,'SchemaElement')
        schema_end=wire.tell()
        wire.seek(schema_start)
        self.schema_wire=bytes(wire.read(schema_end-schema_start))
        self.rows = self.meta.num_rows
        self.columns = []
        self.page_stats=[]
        if len(self.meta.schema) != self.meta.schema[0].num_children + 1:
            raise ValueError('Only flat columns supported')
        expected = math.ceil(self.rows/page_rows)
        with self.path.open('rb') as f:
            for ci in range(len(self.meta.schema)-1):
                records = np.empty((expected, 4), dtype=np.uint64)
                schema=self.meta.schema[ci+1]
                unsigned=schema.converted_type in (11,12,13,14)
                dtype={0:'u1',1:'u4' if unsigned else 'i4',2:'u8' if unsigned else 'i8',4:'f4',5:'f8'}.get(schema.type)
                low=np.empty(expected,dtype=dtype or object)
                high=np.empty(expected,dtype=dtype or object)
                nulls=np.zeros(expected,dtype=np.uint32)
                valid=np.zeros(expected,dtype=bool)
                if dtype is None:low.fill(None);high.fill(None)
                at = row = 0
                for group in self.meta.row_groups:
                    md = group.columns[ci].meta_data
                    if md.dictionary_page_offset is not None:
                        raise ValueError('Preparation must disable dictionaries')
                    f.seek(md.data_page_offset)
                    data = f.read(md.total_compressed_size)
                    buf = NumpyIO(data)
                    while buf.tell() < buf.len:
                        start = buf.tell()
                        header = ThriftObject.from_buffer(buf, 'PageHeader')
                        if header.type != 0:
                            raise ValueError('Preparation requires V1 data pages')
                        header_size = buf.tell() - start
                        count = header.data_page_header.num_values
                        if count != min(page_rows, self.rows-row):
                            raise ValueError(f'Unaligned page: column {ci}, row {row}, count {count}')
                        buf.seek(header.compressed_page_size, 1)
                        if buf.tell() > buf.len:
                            raise ValueError('Page exceeds column chunk')
                        records[at] = (md.data_page_offset+start, buf.tell()-start,
                                       header_size+header.uncompressed_page_size, header_size)
                        s=header.data_page_header.statistics
                        if s is not None:
                            nulls[at]=s.null_count or 0
                            mn=s.min_value if s.min_value is not None else s.min
                            mx=s.max_value if s.max_value is not None else s.max
                            if mn is not None and mx is not None and s.contents.get(7) is not False and s.contents.get(8) is not False:
                                if dtype:
                                    low[at]=np.frombuffer(mn,dtype=dtype)[0]
                                    high[at]=np.frombuffer(mx,dtype=dtype)[0]
                                else:low[at]=mn;high[at]=mx
                                valid[at]=True
                        at += 1
                        row += count
                if row != self.rows or at != expected:
                    raise ValueError('Page count mismatch')
                self.columns.append(records)
                self.page_stats.append((dtype,low,high,nulls,valid))
                print(f'Indexed column {ci+1}/{len(self.meta.schema)-1}', flush=True)

    def key(self, raw, ci):
        schema = self.meta.schema[ci+1]
        if schema.type == 0:
            return raw[0]
        if schema.type in (1, 2):
            unsigned = schema.converted_type in (11, 12, 13, 14)
            return int.from_bytes(raw, 'little', signed=not unsigned)
        if schema.type == 4:
            return struct.unpack('<f',raw)[0]
        if schema.type == 5:
            return struct.unpack('<d',raw)[0]
        return raw

    def statistics(self, begin, end, ci):
        dtype,lo,hi,counts,valid=self.page_stats[ci]
        nulls=int(counts[begin:end].sum())
        if not valid[begin:end].all():return thrift.Statistics(null_count=nulls)
        low=lo[begin:end].min();high=hi[begin:end].max()
        if dtype:
            low=np.asarray(low,dtype=dtype).tobytes()
            high=np.asarray(high,dtype=dtype).tobytes()
        numeric = self.meta.schema[ci+1].type in (0,1,2,4,5)
        return thrift.Statistics(null_count=nulls, min_value=low,max_value=high,
            min=low if numeric else None,max=high if numeric else None)

    def write(self, path, target, progress=None):
        if target <= 0 or target % self.page_rows:
            raise ValueError('Target must be a positive multiple of page_rows')
        path = Path(path)
        count = math.ceil(self.rows/target)
        footer_path = path.with_suffix('.footer.tmp')
        group_rows = []
        footer_groups=[]
        with self.path.open('rb') as source, path.open('wb') as out, footer_path.open('w+b') as foot:
            out.write(b'PAR1')
            # Thrift compact protocol: version, schema, num_rows, row_groups.
            foot.write(b'\x15'+integer(self.meta.version))
            foot.write(b'\x19'+self.schema_wire)
            foot.write(b'\x16'+integer(self.rows))
            foot.write(b'\x19'+list_header(count))
            for gi, start in enumerate(range(0,self.rows,target)):
                end = min(start+target,self.rows)
                chunks=[]
                p0,p1 = start//self.page_rows, math.ceil(end/self.page_rows)
                for ci, pages in enumerate(self.columns):
                    selected=pages[p0:p1]
                    md=copy.deepcopy(self.meta.row_groups[0].columns[ci].meta_data)
                    md.dictionary_page_offset=None
                    md.data_page_offset=out.tell()
                    md.num_values=end-start
                    md.total_compressed_size=int(selected[:,1].sum())
                    md.total_uncompressed_size=int(selected[:,2].sum())
                    stats=self.statistics(p0,p1,ci)
                    if stats is None:md.contents.pop(12,None)
                    else:md.contents[12]=stats.contents
                    md.encoding_stats=None
                    md.index_page_offset=None
                    md.bloom_filter_offset=None
                    md.contents.pop(15,None)
                    md.contents.pop(16,None)  # SizeStatistics belong to the old chunk.
                    md.contents.pop(17,None)  # GeospatialStatistics, if present.
                    begin=int(selected[0,0]);stop=begin
                    # Combine contiguous pages into large copies.
                    for offset,length,_,_ in selected:
                        offset,length=int(offset),int(length)
                        if offset!=stop:
                            self.copy(source,out,begin,stop-begin)
                            begin=offset
                        stop=offset+length
                    self.copy(source,out,begin,stop-begin)
                    chunks.append(thrift.ColumnChunk(file_offset=md.data_page_offset,meta_data=md))
                rg=thrift.RowGroup(columns=chunks,num_rows=end-start,
                    total_byte_size=sum(c.meta_data.total_uncompressed_size for c in chunks),
                    total_compressed_size=sum(c.meta_data.total_compressed_size for c in chunks))
                footer_groups.append(foot.tell())
                foot.write(bytes(rg.to_bytes()))
                group_rows.append(end-start)
                if progress and (gi%max(1,count//100)==0 or gi==count-1):progress(gi+1,count)
            # Preserve user key/value metadata, producer and column sort semantics.
            previous=4
            if self.meta.key_value_metadata is not None:
                foot.write(b'\x19'+list_header(len(self.meta.key_value_metadata)))
                for kv in self.meta.key_value_metadata:foot.write(bytes(kv.to_bytes()))
                previous=5
            if self.meta.created_by is not None:
                value=self.meta.created_by
                if isinstance(value,str):value=value.encode()
                foot.write(bytes([((6-previous)<<4)|8])+varint(len(value))+value)
                previous=6
            if self.meta.column_orders is not None:
                foot.write(bytes([((7-previous)<<4)|9])+list_header(len(self.meta.column_orders)))
                for order in self.meta.column_orders:foot.write(bytes(order.to_bytes()))
            foot.write(b'\x00')
            foot.flush()
            footer_size=foot.tell()
            if footer_size >= 2**32:raise ValueError('Footer exceeds Parquet uint32 length')
            footer_start=out.tell()
            foot.seek(0)
            while block:=foot.read(1024*1024):out.write(block)
            out.write(struct.pack('<I',footer_size)+b'PAR1')
        footer_path.unlink()
        assert sum(group_rows)==self.rows and len(group_rows)==count
        # Verify representative chunks against the immutable prepared pages,
        # reading their metadata from the finished file rather than writer state.
        with path.open('rb') as check,self.path.open('rb') as source:
            for gi in sorted(set((0,count//2,count-1))):
                check.seek(footer_start+footer_groups[gi])
                rg=ThriftObject.from_buffer(check.read(1024*1024),'RowGroup')
                assert rg.num_rows==group_rows[gi] and len(rg.columns)==len(self.columns)
                p0=gi*target//self.page_rows
                p1=math.ceil(min((gi+1)*target,self.rows)/self.page_rows)
                for ci in (0,len(self.columns)//2,len(self.columns)-1):
                    md=rg.columns[ci].meta_data
                    selected=self.columns[ci][p0:p1]
                    assert md.num_values==rg.num_rows and md.total_compressed_size==int(selected[:,1].sum())
                    check.seek(md.data_page_offset)
                    for source_offset,length,_,_ in selected:
                        source.seek(int(source_offset))
                        assert check.read(int(length))==source.read(int(length)), 'Copied page differs'
        return dict(file=str(path.resolve()),target=target,row_count=self.rows,row_group_count=count,
            last_group_rows=group_rows[-1],footer_bytes=footer_size,file_bytes=path.stat().st_size,
            page_rows=self.page_rows,pages_preserved=True,representative_pages_verified=True)

    @staticmethod
    def copy(source,out,offset,length):
        source.seek(offset)
        # Buffered copying works on every filesystem and never holds a chunk.
        while length:
            block=source.read(min(length,1024*1024))
            if not block:raise EOFError('Page source ended early')
            out.write(block)
            length-=len(block)
