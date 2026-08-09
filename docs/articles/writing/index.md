# Writing

Plank has four write layers, ordered from the highest-level orchestration to the lowest-level file mechanics.

- [Dataset writer layer](datasets.md): builds on row writing to distribute rows across multiple parquet files.
- [Row write layer](rows.md): strongly typed writer for application-shaped rows.
- [Logical write layer](logical.md): schema-bound writer that encodes typed column values.
- [Physical write layer](physical.md): low-level writer for assembling serialized columns into row groups and parquet files.
