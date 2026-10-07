# Encoding profile viewer

`index.html` is a self-contained viewer recovered from the latest saved NYC report. It retains that report's CSS, graph and mobile interactions, without embedded NYC data or static NYC benchmark claims. The runtime bundles hyparquet and compression decoders; production use needs no npm dependencies or external scripts. The compression-level reader accepts both Int32 and Int64 values.

Open the page directly, or serve this directory with `python3 -m http.server 5173 --bind 127.0.0.1`. Drop a profile Parquet file onto the page. The same template is embedded in `Plank.Tool` to generate standalone reports using `plank report PROFILE.parquet [OUTPUT.html]`.

The recovered controls include full Pareto frontiers, shifted logarithmic size, time starting at zero, muted dominated points, compression colors and encoding shapes, separate legends, reader-feature presets and capability filters, clear filters, physical column types, sorting and mobile cards.

For DOM interaction checks, install the development dependencies and run `npm test -- /absolute/path/to/profile.parquet`. Checks verify local compressed Parquet loading, graph creation, physical types, clearing filters and the feature slider. These checks do not claim browser layout/render verification.
