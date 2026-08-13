// Benchmark result matrix UI, schema v1.
(() => {
  const root = document.querySelector("#write-benchmarks");
  if (!root) return;

  const seriesColors = {
    "plank-single": "var(--bench-plank)",
    "plank-multi": "var(--bench-plank-multi)",
    "parquetsharp-single": "var(--bench-sharp)",
    "parquetsharp-multi": "var(--bench-sharp-multi)",
    "parquetnet-single": "var(--bench-net)"
  };
  const encodingOrder = [
    "plain",
    "rle",
    "dictionary",
    "delta_binary_packed",
    "delta_length_byte_array",
    "delta_byte_array",
    "byte_stream_split"
  ];

  Promise.all([loadResults(root.dataset.writeResults), loadResults(root.dataset.readResults)])
    .then(([writeReport, readReport]) => render(writeReport, readReport))
    .catch(error => {
      root.innerHTML = "";
      const message = element("p", "benchmark-error");
      message.setAttribute("role", "alert");
      message.textContent = `Benchmark results could not be loaded (${error.message}).`;
      root.append(message);
    });

  function loadResults(url) {
    return fetch(url).then(response => {
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response.json();
    });
  }

  function render(writeReport, readReport) {
    root.innerHTML = "";
    const operationTabs = element("div", "benchmark-tabs benchmark-operation-tabs");
    const writeTab = element("button", "benchmark-tab");
    const readTab = element("button", "benchmark-tab");
    const writePanel = element("div", "benchmark-operation-panel");
    const readPanel = element("div", "benchmark-operation-panel");
    operationTabs.setAttribute("role", "tablist");
    operationTabs.setAttribute("aria-label", "Benchmark operation");
    configureOperationTab(writeTab, "write", "Write", true);
    configureOperationTab(readTab, "read", "Read", false);
    writePanel.id = "benchmark-operation-write";
    writePanel.setAttribute("role", "tabpanel");
    writePanel.setAttribute("aria-labelledby", writeTab.id);
    readPanel.id = "benchmark-operation-read";
    readPanel.setAttribute("role", "tabpanel");
    readPanel.setAttribute("aria-labelledby", readTab.id);
    readPanel.hidden = true;

    operationTabs.append(writeTab, readTab);
    writePanel.append(renderReport(writeReport, "write"));
    readPanel.append(renderReport(readReport, "read"));
    root.append(operationTabs, writePanel, readPanel);
    writeTab.addEventListener("click", () => selectOperation(true));
    readTab.addEventListener("click", () => selectOperation(false));
    writeTab.addEventListener("keydown", event => navigateOperation(event, true));
    readTab.addEventListener("keydown", event => navigateOperation(event, false));

    function configureOperationTab(button, id, label, selected) {
      button.type = "button";
      button.id = `benchmark-operation-tab-${id}`;
      button.setAttribute("role", "tab");
      button.setAttribute("aria-controls", `benchmark-operation-${id}`);
      button.setAttribute("aria-selected", selected ? "true" : "false");
      button.tabIndex = selected ? 0 : -1;
      button.textContent = label;
    }

    function selectOperation(writeSelected) {
      writeTab.setAttribute("aria-selected", writeSelected ? "true" : "false");
      readTab.setAttribute("aria-selected", writeSelected ? "false" : "true");
      writeTab.tabIndex = writeSelected ? 0 : -1;
      readTab.tabIndex = writeSelected ? -1 : 0;
      writePanel.hidden = !writeSelected;
      readPanel.hidden = writeSelected;
    }

    function navigateOperation(event, writeSelected) {
      if (!["ArrowRight", "ArrowLeft", "Home", "End"].includes(event.key)) return;
      event.preventDefault();
      const nextWriteSelected = event.key === "Home" ? true : event.key === "End" ? false : !writeSelected;
      selectOperation(nextWriteSelected);
      (nextWriteSelected ? writeTab : readTab).focus();
    }

  }

  function renderReport(report, operation) {
    const container = element("div", "benchmark-report");
    const tabs = element("div", "benchmark-tabs benchmark-dataset-tabs");
    tabs.setAttribute("role", "tablist");
    tabs.setAttribute("aria-label", `${operation === "write" ? "Write" : "Read"} benchmark data set`);
    const panels = [];

    report.suites.forEach((suite, index) => {
      const button = element("button", "benchmark-tab");
      button.type = "button";
      button.id = `benchmark-${operation}-tab-${suite.id}`;
      button.setAttribute("role", "tab");
      button.setAttribute("aria-controls", `benchmark-${operation}-panel-${suite.id}`);
      button.setAttribute("aria-selected", index === 0 ? "true" : "false");
      button.tabIndex = index === 0 ? 0 : -1;
      button.textContent = suite.label;
      tabs.append(button);

      const panel = renderSuite(suite, operation);
      panel.id = `benchmark-${operation}-panel-${suite.id}`;
      panel.setAttribute("role", "tabpanel");
      panel.setAttribute("aria-labelledby", button.id);
      panel.tabIndex = 0;
      panel.hidden = index !== 0;
      panels.push(panel);

      button.addEventListener("click", () => selectTab(index));
      button.addEventListener("keydown", event => navigateTabs(event, index));
    });

    container.append(tabs, ...panels, renderMethodology(report));
    return container;

    function selectTab(index) {
      [...tabs.children].forEach((tab, tabIndex) => {
        const selected = tabIndex === index;
        tab.setAttribute("aria-selected", selected ? "true" : "false");
        tab.tabIndex = selected ? 0 : -1;
        panels[tabIndex].hidden = !selected;
      });
    }

    function navigateTabs(event, index) {
      let next = index;
      if (event.key === "ArrowRight") next = (index + 1) % panels.length;
      else if (event.key === "ArrowLeft") next = (index - 1 + panels.length) % panels.length;
      else if (event.key === "Home") next = 0;
      else if (event.key === "End") next = panels.length - 1;
      else return;
      event.preventDefault();
      selectTab(next);
      tabs.children[next].focus();
    }
  }

  function renderSuite(suite, operation) {
    const panel = element("div", "benchmark-panel");
    const selectorLabel = element("p", "benchmark-case-selector-label");
    const matrixWrapper = element("div", "benchmark-case-matrix-wrapper");
    const matrix = element("table", "benchmark-case-matrix");
    const output = element("div", "benchmark-selection");
    const multiThreads = suite.cases
      .flatMap(item => item.measurements)
      .find(isMultiThreaded)?.threads;
    const selectorLabelText = `Data type × Encoding · Cell times: 1 thread / ${multiThreads ?? "all"} threads · Red means Plank lost`;
    const encodings = encodingOrder;
    const rows = [];
    const cases = new Map();
    const buttons = [];
    selectorLabel.textContent = selectorLabelText;
    suite.cases.forEach((item, index) => {
      const key = caseRowKey(item);
      if (!rows.some(row => row.key === key))
        rows.push({ key, label: item.dataTypes.length === 1 ? item.dataTypes[0] : "Complete" });
      cases.set(`${key}:${item.encoding}`, { item, index });
    });

    const head = document.createElement("thead");
    const headerRow = document.createElement("tr");
    const corner = document.createElement("th");
    corner.scope = "col";
    corner.textContent = "Data type";
    headerRow.append(corner);
    encodings.forEach(encoding => {
      const header = document.createElement("th");
      header.scope = "col";
      header.textContent = formatEncoding(encoding);
      headerRow.append(header);
    });
    head.append(headerRow);
    matrix.append(head);

    const body = document.createElement("tbody");
    rows.forEach(row => {
      const tableRow = document.createElement("tr");
      const label = document.createElement("th");
      label.scope = "row";
      label.textContent = row.label;
      tableRow.append(label);
      encodings.forEach(encoding => {
        const cell = document.createElement("td");
        const benchmarkCase = cases.get(`${row.key}:${encoding}`);
        if (!benchmarkCase) {
          cell.className = "benchmark-matrix-unavailable";
          cell.textContent = "—";
        } else {
          const button = element("button", "benchmark-matrix-cell");
          const singleWinner = fastestMeasurement(benchmarkCase.item.measurements.filter(isSingleThreaded));
          const multiWinner = fastestMeasurement(benchmarkCase.item.measurements.filter(isMultiThreaded));
          button.type = "button";
          button.setAttribute("aria-pressed", benchmarkCase.index === 0 ? "true" : "false");
          button.setAttribute("aria-label",
            `${row.label}, ${formatEncoding(encoding)}: ` +
            `1 thread ${matrixDuration(singleWinner)}, ` +
            `${multiThreadLabel(benchmarkCase.item.measurements)} ${matrixDuration(multiWinner)}`);
          button.append(
            matrixResult(singleWinner, "plank-single"),
            document.createTextNode(" / "),
            matrixResult(multiWinner, "plank-multi"));
          button.addEventListener("click", () => showCase(benchmarkCase.index));
          buttons.push({ button, index: benchmarkCase.index });
          cell.append(button);
        }
        tableRow.append(cell);
      });
      body.append(tableRow);
    });
    matrix.append(body);
    matrixWrapper.append(matrix);
    panel.append(selectorLabel, matrixWrapper, output);
    showCase(0);
    return panel;

    function showCase(index) {
      buttons.forEach(entry => entry.button.setAttribute("aria-pressed", entry.index === index ? "true" : "false"));
      output.replaceChildren(renderCase(suite.cases[index], operation));
    }
  }

  function renderCase(item, operation) {
    const section = element("section", "benchmark-case");
    const title = element("h3");
    const size = element("p", "benchmark-case-size");
    const dataType = item.dataTypes.length === 1 ? item.dataTypes[0] : item.label;
    title.textContent = `${dataType} · ${formatEncoding(item.encoding)}`;
    size.textContent = `${formatInteger(item.rowCount)} rows · ${formatInteger(item.columnCount)} ${item.columnCount === 1 ? "column" : "columns"}`;
    const groups = element("div", "benchmark-thread-groups");
    groups.append(
      renderThreadGroup("Single thread", item.measurements.filter(isSingleThreaded), operation),
      renderThreadGroup(multiThreadLabel(item.measurements), item.measurements.filter(isMultiThreaded), operation));
    section.append(title, size, groups);
    return section;
  }

  function renderThreadGroup(label, measurements, operation) {
    const group = element("section", "benchmark-thread-group");
    const title = document.createElement("h4");
    const bars = element("div", "benchmark-bars");
    const available = measurements.filter(result => result.available);
    const maximum = available.length === 0
      ? 0
      : Math.max(...available.map(result => result.medianMilliseconds));
    const winner = fastestMeasurement(available);
    title.textContent = label;
    measurements.forEach(result => bars.append(renderBar(result, maximum, winner?.implementationId, operation)));
    group.append(title, bars);
    return group;
  }

  function caseRowKey(item) {
    return item.dataTypes.length === 1 ? item.dataTypes[0] : "complete";
  }

  function matrixDuration(measurement) {
    return measurement?.medianMilliseconds == null
      ? "Unavailable"
      : formatDuration(measurement.medianMilliseconds);
  }

  function matrixResult(measurement, plankImplementationId) {
    const result = element("span", "benchmark-matrix-result");
    result.dataset.lost = String(measurement?.implementationId !== plankImplementationId);
    result.textContent = matrixDuration(measurement);
    return result;
  }

  function isSingleThreaded(measurement) {
    return !measurement.implementationId.endsWith("-multi");
  }

  function isMultiThreaded(measurement) {
    return measurement.implementationId.endsWith("-multi");
  }

  function multiThreadLabel(measurements) {
    const threads = measurements.find(isMultiThreaded)?.threads;
    return threads == null ? "Multithreaded" : `${threads} threads`;
  }

  function fastestMeasurement(measurements) {
    return measurements
      .filter(measurement => measurement.available && measurement.medianMilliseconds != null)
      .reduce((fastest, measurement) =>
        fastest == null || measurement.medianMilliseconds < fastest.medianMilliseconds ? measurement : fastest,
      null);
  }

  function formatEncoding(encoding) {
    return encoding.split("_").map(word => word === "rle" ? "RLE" : word[0].toUpperCase() + word.slice(1)).join(" ");
  }

  function renderBar(result, maximum, winnerId, operation) {
    const row = element("div", "benchmark-bar-row");
    row.style.setProperty("--series-color", seriesColors[result.implementationId] || "currentColor");
    row.dataset.winner = String(result.implementationId === winnerId);
    const label = element("div", "benchmark-series-label");
    label.textContent = result.label;
    row.append(label);
    if (!result.available) {
      const unavailable = element("div", "benchmark-unavailable");
      unavailable.textContent = `Unavailable — ${result.unavailableReason}`;
      row.append(unavailable);
      return row;
    }

    const track = element("div", "benchmark-track");
    const width = maximum === 0 ? 0 : result.medianMilliseconds / maximum * 100;
    track.style.setProperty("--bar-width", `${width}%`);
    track.setAttribute("role", "img");
    const duration = formatDuration(result.medianMilliseconds);
    const resultText = operation === "write" ? `${duration} · ${formatBytes(result.outputBytes)}` : duration;
    track.setAttribute("aria-label", `${result.label}: ${resultText}`);
    const fill = element("span", "benchmark-fill");
    const value = element("span", "benchmark-value");
    value.textContent = resultText;
    if (result.implementationId === winnerId) {
      const fastest = element("span", "benchmark-fastest");
      fastest.textContent = "Fastest";
      value.append(fastest);
    }
    track.append(fill, value);
    row.append(track);
    return row;
  }

  function renderMethodology(report) {
    const details = element("details", "benchmark-methodology");
    const summary = element("summary");
    summary.textContent = "Methodology and machine";
    const metadata = element("dl", "benchmark-metadata");
    const libraries = Object.entries(report.environment.libraries).map(([name, version]) => `${name} ${version}`).join(", ");
    const entries = [
      ["CPU", `${report.environment.cpu} · ${report.environment.logicalProcessors} logical processors`],
      ["Runtime", `${report.environment.operatingSystem} · ${report.environment.dotNetVersion}`],
      ["Libraries", libraries],
      ["Commit", report.environment.commit],
      ["Runs", `${report.configuration.warmups} warmups, ${report.configuration.iterations} measured iterations; median with interquartile variation`],
      ["Format", `Data Page ${report.configuration.dataPageVersion}, ${report.configuration.compression} compression, no page indexes or Bloom filters`],
      ["Timing", report.configuration.timingBoundary],
      ["Data", "January 2024 NYC Yellow Taxi data and deterministic synthetic columns"]
    ];
    entries.forEach(([term, description]) => {
      const wrapper = document.createElement("div");
      const dt = document.createElement("dt");
      const dd = document.createElement("dd");
      dt.textContent = term;
      dd.textContent = description;
      wrapper.append(dt, dd);
      metadata.append(wrapper);
    });
    details.append(summary, metadata);
    return details;
  }

  function element(name, className) {
    const node = document.createElement(name);
    if (className) node.className = className;
    return node;
  }

  function formatNumber(value) {
    return new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(value);
  }

  function formatInteger(value) {
    return new Intl.NumberFormat().format(value);
  }

  function formatDuration(milliseconds) {
    return milliseconds < 1 ? `${formatNumber(milliseconds * 1000)} µs` : `${formatNumber(milliseconds)} ms`;
  }

  function formatBytes(bytes) {
    return `${formatNumber(bytes / 1024 / 1024)} MiB`;
  }

})();
