# Nullable struct fixtures

These fixtures were generated independently with PyArrow 25.0.1. They contain
an optional struct with an optional int32 leaf, definition levels `[0, 1, 2, 2]`,
and dense values `[42, 99]`. The filename identifies the data page version and
whether dictionary encoding is enabled.

```python
import pyarrow as pa
import pyarrow.parquet as pq

nested = pa.array(
    [None, {"x": None}, {"x": 42}, {"x": 99}],
    type=pa.struct([pa.field("x", pa.int32(), nullable=True)]),
)
for version in ["1.0", "2.0"]:
    for dictionary in [False, True]:
        pq.write_table(
            pa.table({"obj": nested}),
            f"nested-{version}-{dictionary}.parquet",
            data_page_version=version,
            use_dictionary=dictionary,
            compression=None,
        )
```
