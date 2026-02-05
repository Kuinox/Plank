# Plank

Minimal Parquet writer under construction.

## Notes

- Current `Serialize` writes a single data page per column chunk.
- Future work: support multiple data pages per column chunk for streaming and page sizing.
