const taxiParquet = {
  fileSize: 49_961_641,
  footerStart: 49_955_276,
  footerLengthStart: 49_961_633,
  closingMagicStart: 49_961_637,
  rowGroups: [
    [4, 17_612_404],
    [17_612_404, 35_136_032],
    [35_136_032, 49_955_276]
  ],
  chunks: [
    [4, 120833, 0, 0], [120893, 4599473, 0, 1], [4599559, 9162750, 0, 2],
    [9162839, 9361162, 0, 3], [9361236, 10802675, 0, 4], [10802749, 10892736, 0, 5],
    [10892805, 10899937, 0, 6], [10900013, 11641568, 0, 7], [11641640, 12705004, 0, 8],
    [12705077, 12848456, 0, 9], [12848527, 13975062, 0, 10], [13975134, 14277997, 0, 11],
    [14278061, 14321442, 0, 12], [14321508, 15500024, 0, 13], [15500095, 15651225, 0, 14],
    [15651296, 15677418, 0, 15], [15677498, 17424187, 0, 16], [17424260, 17522142, 0, 17],
    [17522221, 17612334, 0, 18],
    [17612404, 17731750, 1, 0], [17731817, 22173761, 1, 1], [22173849, 26693787, 1, 2],
    [26693876, 26886103, 1, 3], [26886177, 28338097, 1, 4], [28338171, 28422241, 1, 5],
    [28422310, 28429598, 1, 6], [28429674, 29167833, 1, 7], [29167905, 30229550, 1, 8],
    [30229623, 30361171, 1, 9], [30361242, 31484482, 1, 10], [31484554, 31763290, 1, 11],
    [31763354, 31803969, 1, 12], [31804035, 33068085, 1, 13], [33068156, 33208285, 1, 14],
    [33208356, 33233235, 1, 15], [33233315, 34951319, 1, 16], [34951392, 35051801, 1, 17],
    [35051880, 35135962, 1, 18],
    [35136032, 35242952, 2, 0], [35243018, 39118251, 2, 1], [39118338, 43047607, 2, 2],
    [43047695, 43177036, 2, 3], [43177109, 44366697, 2, 4], [44366770, 44419873, 2, 5],
    [44419941, 44425092, 2, 6], [44425167, 45048882, 2, 7], [45048952, 45909969, 2, 8],
    [45910039, 46016192, 2, 9], [46016262, 47057279, 2, 10], [47057349, 47274102, 2, 11],
    [47274165, 47303407, 2, 12], [47303472, 48266555, 2, 13], [48266624, 48369210, 2, 14],
    [48369280, 48386648, 2, 15], [48386727, 49832942, 2, 16], [49833014, 49900387, 2, 17],
    [49900465, 49955207, 2, 18]
  ],
  pageSeams: [
    35, 847640, 5327593, 9162895, 9372294, 10802799, 10892838, 10900450, 11642095,
    12705121, 12854646, 13975314, 14278115, 14331778, 15502292, 15651350, 15722712,
    17424308, 17522262, 17612435, 18459346, 22900323, 26693935, 26897017, 28338221,
    28422343, 28430109, 29168362, 30229667, 30367081, 31484739, 31763407, 31814070,
    33070026, 33208410, 33276868, 34951445, 35051921, 35136067, 35970714, 39849289,
    43047751, 43187298, 44366820, 44419974, 44425613, 45049407, 45910078, 46041982,
    47057543, 47274218, 47312841, 48268275, 48369330, 48431382, 49833071, 49900506,
    1334841, 1849062, 2360372, 2896757, 3410620, 3929567, 4436163, 5812484, 6340236,
    6863244, 7408931, 7935984, 8469090, 8991603, 10254836, 11558297, 12492803,
    13602998, 15041588, 15610262, 16628787, 18984055, 19508568, 20014479, 20523259,
    21042927, 21561930, 22080611, 23425129, 23963424, 24482313, 25003285, 25534126,
    26065763, 26597953, 27784154, 29086142, 30017861, 31111937, 32587057, 33167598,
    34170175, 36537077, 37053714, 37571619, 38088505, 38668383, 40415843, 40943311,
    41474040, 42003776, 42593749, 44067788, 45880923, 46793599, 48064415, 48366530,
    49318330
  ]
};

function initializeParquetBackground() {
  const canvas = document.querySelector("canvas.plank-parquet-background");
  const home = canvas?.parentElement;
  if (!(canvas instanceof HTMLCanvasElement) || !home) return;

  let frame = 0;

  function scheduleRender() {
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      render();
    });
  }

  function render() {
    const bounds = canvas.getBoundingClientRect();
    const width = Math.max(1, bounds.width);
    const height = Math.max(1, bounds.height);
    const pixelRatio = Math.max(1, Math.min(
      window.devicePixelRatio || 1,
      2,
      8192 / width,
      8192 / height
    ));
    const bitmapWidth = Math.max(1, Math.round(width * pixelRatio));
    const bitmapHeight = Math.max(1, Math.round(height * pixelRatio));

    if (canvas.width !== bitmapWidth) canvas.width = bitmapWidth;
    if (canvas.height !== bitmapHeight) canvas.height = bitmapHeight;

    const context = canvas.getContext("2d");
    if (!context) return;

    context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    context.clearRect(0, 0, width, height);

    const style = getComputedStyle(canvas);
    const color = name => style.getPropertyValue(name).trim();
    const opacity = name => Number.parseFloat(style.getPropertyValue(name)) || 0;
    const targetRowHeight = Math.min(68, Math.max(52, width / 18));
    const rowCount = Math.max(1, Math.ceil(height / targetRowHeight));
    const rowHeight = height / rowCount;
    const bytesPerRow = Math.ceil(taxiParquet.fileSize / (rowCount - 1 + 0.618));

    function forEachPart(start, end, paint) {
      let offset = Math.max(0, start);
      const limit = Math.min(taxiParquet.fileSize, end);

      while (offset < limit) {
        const row = Math.floor(offset / bytesPerRow);
        const rowStart = row * bytesPerRow;
        const stop = Math.min(limit, rowStart + bytesPerRow);
        const x0 = ((offset - rowStart) / bytesPerRow) * width;
        const x1 = ((stop - rowStart) / bytesPerRow) * width;
        paint(x0, row * rowHeight, x1 - x0, rowHeight);
        offset = stop;
      }
    }

    function pointAt(offset) {
      const row = Math.min(rowCount - 1, Math.floor(offset / bytesPerRow));
      const rowStart = row * bytesPerRow;
      return {
        x: ((offset - rowStart) / bytesPerRow) * width,
        y: row * rowHeight
      };
    }

    function fillInterval(start, end, fillStyle, alpha) {
      context.fillStyle = fillStyle;
      context.globalAlpha = alpha;
      forEachPart(start, end, (x, y, partWidth, partHeight) => {
        context.fillRect(x, y, partWidth, partHeight);
      });
    }

    function strokeOffsets(offsets, alpha, lineWidth, inset = 0) {
      context.beginPath();
      for (const offset of offsets) {
        const point = pointAt(offset);
        context.moveTo(point.x, point.y + rowHeight * inset);
        context.lineTo(point.x, point.y + rowHeight * (1 - inset));
      }
      context.globalAlpha = alpha;
      context.lineWidth = lineWidth;
      context.strokeStyle = color("--pq-rule");
      context.stroke();
    }

    taxiParquet.rowGroups.forEach(([start, end], index) => {
      fillInterval(
        start,
        end,
        color(`--pq-pattern-rg-${index}`),
        opacity("--pq-row-group-opacity")
      );
    });

    for (const [start, end, , column] of taxiParquet.chunks) {
      fillInterval(
        start,
        end,
        color("--pq-pattern-chunk"),
        opacity(column % 2 ? "--pq-chunk-odd-opacity" : "--pq-chunk-even-opacity")
      );
    }

    const endPoint = pointAt(taxiParquet.fileSize);
    const footerPoint = pointAt(taxiParquet.footerStart);
    const footerLengthPoint = pointAt(taxiParquet.footerLengthStart);
    context.globalAlpha = opacity("--pq-magic-opacity");
    context.fillStyle = color("--pq-pattern-header");
    context.fillRect(0, 0, 1, rowHeight);
    context.fillRect(Math.max(0, endPoint.x - 1), endPoint.y, 1, rowHeight);

    context.globalAlpha = opacity("--pq-metadata-opacity");
    context.fillStyle = color("--pq-pattern-metadata");
    context.fillRect(
      footerPoint.x,
      footerPoint.y,
      Math.max(1, footerLengthPoint.x - footerPoint.x),
      rowHeight
    );

    context.globalAlpha = opacity("--pq-length-opacity");
    context.fillStyle = color("--pq-rule");
    context.fillRect(Math.max(0, endPoint.x - 2), endPoint.y, 1, rowHeight);

    context.beginPath();
    for (let row = 0; row < rowCount; row++) {
      const y = row * rowHeight;
      const lineEnd = row === rowCount - 1 ? endPoint.x : width;
      context.moveTo(0, y);
      context.lineTo(lineEnd, y);
    }
    context.globalAlpha = opacity("--pq-wrap-seam-opacity");
    context.lineWidth = 1;
    context.strokeStyle = color("--pq-rule");
    context.stroke();

    strokeOffsets(
      taxiParquet.rowGroups.slice(1).map(([start]) => start),
      opacity("--pq-row-group-seam-opacity"),
      1.15
    );
    strokeOffsets(
      taxiParquet.chunks.filter(([, , , column]) => column > 0).map(([start]) => start),
      opacity("--pq-chunk-seam-opacity"),
      1
    );
    strokeOffsets(
      taxiParquet.pageSeams,
      opacity("--pq-page-seam-opacity"),
      0.8,
      1 / 3
    );

    context.globalAlpha = 1;
  }

  new ResizeObserver(scheduleRender).observe(home);
  new MutationObserver(scheduleRender).observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["data-bs-theme"]
  });
  scheduleRender();
}

function initializeBenchmarkFrame() {
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
}

export default {
  defaultTheme: "auto",
  iconLinks: [
    {
      icon: "github",
      href: "https://github.com/Kuinox/Plank",
      title: "Plank on GitHub"
    },
    {
      icon: "box-seam",
      href: "https://www.nuget.org/packages/Plank",
      title: "Plank on NuGet"
    }
  ],
  start: () => {
    if (document.querySelector(".plank-home"))
      document.body.classList.add("plank-home-page");

    if (document.querySelector("#plank-benchmarks"))
      document.body.classList.add("plank-benchmarks-page");

    initializeParquetBackground();
    initializeBenchmarkFrame();
  }
};
