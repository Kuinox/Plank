# Actual Encoding Audit

This report writes one small parquet file for each library/type/requested-encoding combination,
then reads the column chunk metadata back with ParquetSharp.

Rows marked `not_parquet_supported` are skipped before writing because the Parquet format does not allow
that encoding for the requested physical type.

## Summary

- `delta_binary_packed` is valid only for `int32` and `int64`; `bool`, `float`, `double`, and `string` are not benchmarked for it.
- `delta_length_byte_array` and `delta_byte_array` are valid only for `string` in this matrix.
- `byte_stream_split` is valid for `int32`, `int64`, `float`, and `double`; `bool` and `string` are not benchmarked for it.

## Parquet.Net Actual Usage

| Requested | Used | Gave up | Unsupported | Not Parquet-supported |
| --- | ---: | ---: | ---: | ---: |
| plain | 6 | 0 | 0 | 0 |
| dictionary | 1 | 5 | 0 | 0 |
| delta_binary_packed | 2 | 0 | 0 | 4 |
| delta_length_byte_array | 0 | 0 | 1 | 5 |
| delta_byte_array | 0 | 0 | 1 | 5 |
| byte_stream_split | 0 | 0 | 4 | 2 |

| Library | Type | Requested | Status | Actual | Raw encodings | Error |
| --- | --- | --- | --- | --- | --- | --- |
| plank | bool | plain | used_requested | plain | Plain |  |
| plank | bool | dictionary | used_requested | dictionary | Plain|PlainDictionary |  |
| plank | bool | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | bool | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | bool | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | bool | byte_stream_split | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | int32 | plain | used_requested | plain | Plain |  |
| plank | int32 | dictionary | used_requested | dictionary | Plain|PlainDictionary |  |
| plank | int32 | delta_binary_packed | used_requested | delta_binary_packed | DeltaBinaryPacked |  |
| plank | int32 | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | int32 | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | int32 | byte_stream_split | used_requested | byte_stream_split | ByteStreamSplit |  |
| plank | int64 | plain | used_requested | plain | Plain |  |
| plank | int64 | dictionary | used_requested | dictionary | Plain|PlainDictionary |  |
| plank | int64 | delta_binary_packed | used_requested | delta_binary_packed | DeltaBinaryPacked |  |
| plank | int64 | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | int64 | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | int64 | byte_stream_split | used_requested | byte_stream_split | ByteStreamSplit |  |
| plank | float | plain | used_requested | plain | Plain |  |
| plank | float | dictionary | used_requested | dictionary | Plain|PlainDictionary |  |
| plank | float | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | float | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | float | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | float | byte_stream_split | used_requested | byte_stream_split | ByteStreamSplit |  |
| plank | double | plain | used_requested | plain | Plain |  |
| plank | double | dictionary | used_requested | dictionary | Plain|PlainDictionary |  |
| plank | double | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | double | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | double | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | double | byte_stream_split | used_requested | byte_stream_split | ByteStreamSplit |  |
| plank | string | plain | used_requested | plain | Plain |  |
| plank | string | dictionary | used_requested | dictionary | Plain|PlainDictionary |  |
| plank | string | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| plank | string | delta_length_byte_array | used_requested | delta_length_byte_array | DeltaLengthByteArray |  |
| plank | string | delta_byte_array | used_requested | delta_byte_array | DeltaByteArray |  |
| plank | string | byte_stream_split | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | bool | plain | used_requested | plain | Plain|Rle |  |
| parquetsharp | bool | dictionary | gave_up | plain | Plain|Rle |  |
| parquetsharp | bool | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | bool | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | bool | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | bool | byte_stream_split | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | int32 | plain | used_requested | plain | Plain|Rle |  |
| parquetsharp | int32 | dictionary | used_requested | dictionary | Plain|Rle|RleDictionary |  |
| parquetsharp | int32 | delta_binary_packed | used_requested | delta_binary_packed | DeltaBinaryPacked|Rle |  |
| parquetsharp | int32 | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | int32 | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | int32 | byte_stream_split | used_requested | byte_stream_split | ByteStreamSplit|Rle |  |
| parquetsharp | int64 | plain | used_requested | plain | Plain|Rle |  |
| parquetsharp | int64 | dictionary | used_requested | dictionary | Plain|Rle|RleDictionary |  |
| parquetsharp | int64 | delta_binary_packed | used_requested | delta_binary_packed | DeltaBinaryPacked|Rle |  |
| parquetsharp | int64 | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | int64 | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | int64 | byte_stream_split | used_requested | byte_stream_split | ByteStreamSplit|Rle |  |
| parquetsharp | float | plain | used_requested | plain | Plain|Rle |  |
| parquetsharp | float | dictionary | used_requested | dictionary | Plain|Rle|RleDictionary |  |
| parquetsharp | float | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | float | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | float | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | float | byte_stream_split | used_requested | byte_stream_split | ByteStreamSplit|Rle |  |
| parquetsharp | double | plain | used_requested | plain | Plain|Rle |  |
| parquetsharp | double | dictionary | used_requested | dictionary | Plain|Rle|RleDictionary |  |
| parquetsharp | double | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | double | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | double | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | double | byte_stream_split | used_requested | byte_stream_split | ByteStreamSplit|Rle |  |
| parquetsharp | string | plain | used_requested | plain | Plain|Rle |  |
| parquetsharp | string | dictionary | used_requested | dictionary | Plain|Rle|RleDictionary |  |
| parquetsharp | string | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquetsharp | string | delta_length_byte_array | used_requested | delta_length_byte_array | DeltaLengthByteArray|Rle |  |
| parquetsharp | string | delta_byte_array | used_requested | delta_byte_array | DeltaByteArray|Rle |  |
| parquetsharp | string | byte_stream_split | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | bool | plain | used_requested | plain | Plain|Rle |  |
| parquet.net | bool | dictionary | gave_up | plain | Plain|Rle |  |
| parquet.net | bool | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | bool | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | bool | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | bool | byte_stream_split | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | int32 | plain | used_requested | plain | Plain|Rle |  |
| parquet.net | int32 | dictionary | gave_up | plain | Plain|Rle |  |
| parquet.net | int32 | delta_binary_packed | used_requested | delta_binary_packed | DeltaBinaryPacked|Rle |  |
| parquet.net | int32 | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | int32 | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | int32 | byte_stream_split | unsupported |  |  | Parquet.Net benchmark path does not support encoding 'byte_stream_split'. |
| parquet.net | int64 | plain | used_requested | plain | Plain|Rle |  |
| parquet.net | int64 | dictionary | gave_up | plain | Plain|Rle |  |
| parquet.net | int64 | delta_binary_packed | used_requested | delta_binary_packed | DeltaBinaryPacked|Rle |  |
| parquet.net | int64 | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | int64 | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | int64 | byte_stream_split | unsupported |  |  | Parquet.Net benchmark path does not support encoding 'byte_stream_split'. |
| parquet.net | float | plain | used_requested | plain | Plain|Rle |  |
| parquet.net | float | dictionary | gave_up | plain | Plain|Rle |  |
| parquet.net | float | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | float | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | float | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | float | byte_stream_split | unsupported |  |  | Parquet.Net benchmark path does not support encoding 'byte_stream_split'. |
| parquet.net | double | plain | used_requested | plain | Plain|Rle |  |
| parquet.net | double | dictionary | gave_up | plain | Plain|Rle |  |
| parquet.net | double | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | double | delta_length_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | double | delta_byte_array | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | double | byte_stream_split | unsupported |  |  | Parquet.Net benchmark path does not support encoding 'byte_stream_split'. |
| parquet.net | string | plain | used_requested | plain | Plain|Rle |  |
| parquet.net | string | dictionary | used_requested | dictionary | PlainDictionary|Rle |  |
| parquet.net | string | delta_binary_packed | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
| parquet.net | string | delta_length_byte_array | unsupported |  |  | Parquet.Net benchmark path does not support encoding 'delta_length_byte_array'. |
| parquet.net | string | delta_byte_array | unsupported |  |  | Parquet.Net benchmark path does not support encoding 'delta_byte_array'. |
| parquet.net | string | byte_stream_split | not_parquet_supported |  |  | The Parquet format does not support this encoding for this physical type. |
