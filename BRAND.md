# Plank visual identity

## Direction: quiet precision

Plank should look like a well-maintained open-source engineering library: clear, exact, and
easy to inspect. The identity is there to make the project recognizable, not to turn the
documentation into a product landing page.

The public line is:

> **Fast Parquet for modern .NET.**

Supporting descriptions should stay factual. Prefer concrete capabilities—source-generated
row APIs, logical column APIs, physical file access, and measured performance—over campaign
language or broad superlatives.

## Logo

The mark is a `P` assembled from four rectangular data planks. Its seams can suggest column
chunks, pages, and aligned buffers without relying on literal wood imagery.

- `assets/brand/plank-mark.svg` — primary color mark for avatars and package art.
- `assets/brand/plank-mark-mono.svg` — restrained mark for documentation and constrained use.
- `assets/brand/plank-lockup.svg` — horizontal lockup for the repository README.
- `assets/brand/plank-icon.png` — raster package icon for NuGet.

Keep clear space around the mark equal to one quarter of its width. Do not rotate it, add
texture, or place it on a visually busy background.

## Color

| Token | Hex | Use |
| --- | --- | --- |
| Ink | `#13201c` | Primary type and dark surfaces |
| Paper | `#f6f2e9` | Logo contrast and occasional warm surface |
| Fir | `#0b6557` | Primary link and supported-library accent |
| Mint | `#66d0b0` | Small data-flow details on dark backgrounds |
| Ember | `#d85c35` | Experimental Lab label and small contrast details |

Documentation should use a neutral light or dark canvas with **one accent color at a time**.
Large tinted backgrounds, decorative gradients, and repeated colored panels are not part of
the system.

## Typography

Use the platform sans-serif stack for prose and the platform monospace stack for code and
measurements. Headings should be only modestly larger than body copy. The documentation must
remain fast and self-contained; it does not load brand fonts.

## Documentation

- Prefer normal Markdown headings, paragraphs, lists, tables, and fenced code blocks.
- Put installation, examples, source, issues, and API links ahead of visual storytelling.
- Keep article text around 70–76 characters wide and preserve DocFX navigation conventions.
- The homepage background pattern is derived from the January 2024 NYC Yellow Taxi fixture.
  The complete file is treated as one continuous byte stream. A decorative canvas lays it out from
  byte zero to EOF and returns the stream only when it reaches the available page width, recalculating
  the wrap points when the page resizes. It is drawn once, never tiled: the opening magic appears once
  at the top and the metadata/length/closing-magic trailer appears once, partway across the final line.
  Row groups and chunks continue naturally across line returns. Every strong seam is a real
  column-chunk edge and every short seam is a real page boundary. Neighboring green-gray fills
  alternate by column-chunk ordinal. Fir and ember terminal segments are enlarged just enough to
  remain visible. It should provide character rather than ask to be read as a diagram. Avoid blueprint
  grids, brick bonds, herringbone, recurring tiles, and literal wood grain.
- Use thin borders and spacing for hierarchy; avoid large shadows, hover lifts, animations,
  oversized display type, metric strips, and closing calls to action.
- Diagrams are appropriate only when they clarify an API or file-format relationship.

## Plank Lab

Plank Lab is a benchmark renderer and experimental companion repository, not a standalone
brand destination. When opened directly it needs only a compact title and experimental
disclaimer. When embedded in the main documentation it should show the benchmark interface
without separate navigation, hero content, or promotional framing.

Ember may identify experimental controls or labels, but benchmark win/loss colors remain
reserved for their data meaning.
