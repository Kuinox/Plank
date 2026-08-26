# Plank

Plank reads and writes Parquet files in .NET.

Its APIs cover datasets, rows, logical columns, and the physical file structure. Most
applications can use the generated row APIs; the lower layers are available when data is
already column-oriented or the file layout needs to be controlled directly.

Schemas are declared as C# types. A source generator creates the corresponding readers and
writers, and reports incompatible mappings at build time.

Start with [Schema](articles/schema.md), then see [Reading](articles/reading/index.md) or
[Writing](articles/writing/index.md).

## Faster Parquet writes

Plank was built to reduce the time it takes to read and write Parquet files in .NET. Once a
reader or writer is initialized, processing is allocation-free: buffers are reused, generated
code avoids reflection, and columns can be encoded or decoded in parallel across multiple cores.

The benchmarks below measure complete in-memory reads and writes in both single-threaded and
multithreaded configurations.

<iframe
  id="plank-benchmarks"
  src="https://kuinox.github.io/Plank-Lab/?embed=docs"
  title="Performance benchmarks"
  loading="lazy"
  referrerpolicy="no-referrer"
  scrolling="no"
  style="display:block; width:100%; height:52rem; border:0; background:transparent; color-scheme:light dark; overflow:hidden;"
></iframe>

<script>
(() => {
  const frame = document.querySelector("#plank-benchmarks");
  if (!frame) return;

  const frameOrigin = new URL(frame.src, document.baseURI).origin;

  function sendTheme() {
    if (!frame.contentWindow) return;

    const page = getComputedStyle(document.body);
    const content = getComputedStyle(document.querySelector("article") ?? document.body);
    const value = name => page.getPropertyValue(name).trim();

    frame.contentWindow.postMessage({
      type: "plank-benchmarks-theme",
      theme: document.documentElement.getAttribute("data-bs-theme") || "light",
      styles: {
        color: content.color,
        fontFamily: content.fontFamily,
        fontSize: content.fontSize,
        lineHeight: content.lineHeight,
        backgroundColor: value("--bs-body-bg") || page.backgroundColor,
        secondaryColor: value("--bs-secondary-color"),
        borderColor: value("--bs-border-color")
      }
    }, frameOrigin);
  }

  window.addEventListener("message", event => {
    if (event.origin !== frameOrigin || event.source !== frame.contentWindow) return;
    if (event.data?.type === "plank-benchmarks-ready") sendTheme();
    if (event.data?.type === "plank-benchmarks-resize" && Number.isFinite(event.data.height)) {
      frame.style.height = `${Math.max(1, Math.ceil(event.data.height))}px`;
    }
  });

  frame.addEventListener("load", sendTheme);
  new MutationObserver(sendTheme).observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["data-bs-theme"]
  });
})();
</script>
