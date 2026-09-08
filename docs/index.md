---
title: Plank
description: A high-performance, managed Apache Parquet library for .NET.
---

<div class="plank-home">
  <canvas class="plank-parquet-background" aria-hidden="true"></canvas>
  <header class="plank-home-intro">
    <div class="plank-home-copy">
      <h1>Plank</h1>
      <p class="plank-home-lede">Plank is a high-performance, managed .NET implementation for reading and writing <a href="https://parquet.apache.org/">Apache Parquet</a> files.</p>
      <div class="plank-install" aria-label="Install Plank from NuGet">
        <span aria-hidden="true">$</span>
        <code>dotnet add package Plank</code>
      </div>
    </div>
    <section class="plank-schema-example" aria-labelledby="plank-schema-title">
      <h2 id="plank-schema-title">Quick start</h2>
      <div class="plank-quickstart-files">
      <div class="plank-code-file">
      <p>Declare a schema with <code>[ParquetSchema]</code>:</p>
      <pre><code class="lang-csharp-invisible">using Plank.Schema;</code></pre>
      <pre><code class="lang-csharp">[ParquetSchema]
public sealed partial class Measurement
{
    public int SensorId { get; init; }
    public double Value { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}</code></pre>
      <p>The source generator adds strongly typed APIs to <code>Measurement</code>.</p>
      </div>
      <div class="plank-code-file">
      <p>Write</p>
      <pre><code class="lang-csharp">using var output = File.Create("measurements.parquet");
using var writer = Measurement.CreateRowWriter(output);&#10;
for (var sensorId = 1; sensorId &lt;= 3; sensorId++)
{
    var row = writer.GetRow();
    row.SensorId = sensorId;
    row.Value = 20.0 + sensorId;
    row.RecordedAt = DateTimeOffset.UtcNow;
}&#10;
writer.Complete();</code></pre>
      <p>Read</p>
      <pre><code class="lang-csharp">using var input = File.OpenRead("measurements.parquet");
using var reader = Measurement.CreateRowReader(input);&#10;
foreach (var row in reader)
    Console.WriteLine(
        $"{row.SensorId}: {row.Value} " +
        $"at {row.RecordedAt:O}");</code></pre>
      </div>
      </div>
    </section>
  </header>
  <div class="plank-home-grid">
    <section class="plank-home-panel plank-features-panel" aria-label="Features">
      <ul class="plank-feature-list">
        <li><strong>Fast by design</strong><span>Preallocated, reusable buffers keep steady-state reads and writes allocation-free. Encoding, decoding, compression, and decompression are parallelized across columns and row groups.</span></li>
        <li><strong>Source-generated APIs</strong><span>Declare your Parquet schema as a C# model and get matching row readers and writers, dataset writers, projections, and column accessors at compile time.</span></li>
        <li><strong>APIs for every workload</strong><span>Use rows for application-shaped data, columns for columnar workloads, datasets for partitioned outputs, or the physical API for low-level control.</span></li>
        <li><strong>Fine-grained configuration</strong><span>Configure encodings and compression globally or per column, with control over row groups, pages, indexes, statistics, Bloom filters, and CRCs. Limit parallelism or use worker hooks to pin threads to specific CPUs.</span></li>
        <li><strong>Built for evolving data</strong><span>Read selected columns, handle compatible schema changes, and define schemas dynamically at runtime.</span></li>
      </ul>
    </section>
  </div>
  <section class="plank-api-guides" aria-labelledby="plank-api-guides-title">
    <div class="plank-section-intro">
      <h2 id="plank-api-guides-title">API guides</h2>
      <p>Most applications can stay with these generated row APIs. The dataset API routes rows into partitioned files, while the column and physical APIs provide progressively more control.</p>
    </div>
    <div class="plank-guide-grid">
      <section class="plank-guide-group plank-guide-reading" aria-labelledby="plank-reading-title">
        <header><h3 id="plank-reading-title">Read Parquet</h3></header>
        <div class="plank-guide-links">
          <a href="articles/reading/rows.md"><strong>Row</strong><span>Generated row APIs</span></a>
          <a href="articles/reading/logical.md"><strong>Logical columns</strong><span>Column APIs</span></a>
          <a href="articles/reading/physical.md"><strong>Physical files</strong><span>Physical API</span></a>
        </div>
      </section>
      <section class="plank-guide-group plank-guide-writing" aria-labelledby="plank-writing-title">
        <header><h3 id="plank-writing-title">Write Parquet</h3></header>
        <div class="plank-guide-links">
          <a href="articles/writing/datasets.md"><strong>Datasets</strong><span>Partitioned files</span></a>
          <a href="articles/writing/rows.md"><strong>Row</strong><span>Generated row APIs</span></a>
          <a href="articles/writing/logical.md"><strong>Logical columns</strong><span>Column APIs</span></a>
          <a href="articles/writing/physical.md"><strong>Physical files</strong><span>Physical API</span></a>
        </div>
      </section>
    </div>
  </section>
</div>
