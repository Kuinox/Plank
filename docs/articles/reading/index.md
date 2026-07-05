# Reading

Plank has three read layers. Each layer serves a different purpose.

- [Physical read layer](physical.md): low-level parquet parser for file metadata and encoded column data.
- Logical read layer: schema-bound reader that decodes column data into typed values.
- Generated row layer: source-generated row reader for application-shaped data.
