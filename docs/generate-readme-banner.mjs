// Export the website's canvas drawing as SVG for GitHub's static README.
// Run: node docs/generate-readme-banner.mjs
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
const width = 960, height = 240;
const css = readFileSync(new URL('plank-template/public/main.css', import.meta.url), 'utf8');
for (const theme of ['light', 'dark']) {
const light = theme === 'light' ? css.slice(0, css.indexOf(':root[data-bs-theme="dark"]')) : css.slice(css.indexOf(':root[data-bs-theme="dark"]'), css.indexOf('\n* {'));
const variables = Object.fromEntries([...light.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map(m => [m[1], m[2]]));
// Strengthen the README background while keeping the website's file geometry.
['#8fcdb7', '#6dbb9e', '#4ca889'].forEach((color, i) => {
  variables[`--pq-pattern-rg-${i}`] = theme === 'light' ? color : ['#205b49', '#28715b', '#31876b'][i];
});
variables['--pq-row-group-opacity'] = '0.32';
variables['--pq-chunk-even-opacity'] = '0.065';
variables['--pq-chunk-odd-opacity'] = '0.14';
if (theme === 'dark') {
  variables['--pq-rule'] = '#96b3a5';
  variables['--pq-wrap-seam-opacity'] = '0.19';
  variables['--pq-row-group-seam-opacity'] = '0.25';
  variables['--pq-chunk-seam-opacity'] = '0.15';
  variables['--pq-page-seam-opacity'] = '0.045';
}
const parts = [];
let path = '';
const n = value => Number(value.toFixed(4));
const context = {
  globalAlpha: 1,
  setTransform() {}, clearRect() {},
  fillRect(x, y, w, h) {
    parts.push(`<rect x="${n(x)}" y="${n(y)}" width="${n(w)}" height="${n(h)}" fill="${this.fillStyle}" opacity="${this.globalAlpha}"/>`);
  },
  beginPath() { path = ''; },
  moveTo(x, y) { path += `M${n(x)} ${n(y)}`; },
  lineTo(x, y) { path += `L${n(x)} ${n(y)}`; },
  stroke() {
    parts.push(`<path d="${path}" fill="none" stroke="${this.strokeStyle}" stroke-width="${this.lineWidth}" opacity="${this.globalAlpha}"/>`);
  }
};
class HTMLCanvasElement {
  parentElement = {};
  getBoundingClientRect() { return { width, height }; }
  getContext() { return context; }
}
const canvas = new HTMLCanvasElement();
const sandbox = {
  HTMLCanvasElement,
  document: { querySelector: () => canvas, documentElement: {} },
  window: { devicePixelRatio: 1 },
  getComputedStyle: () => ({ getPropertyValue: name => variables[name] ?? '' }),
  requestAnimationFrame: callback => { callback(); return 1; },
  ResizeObserver: class { observe() {} },
  MutationObserver: class { observe() {} }
};
const source = readFileSync(new URL('plank-template/public/main.js', import.meta.url), 'utf8');
vm.runInNewContext(source.slice(0, source.indexOf('export default')) + '\ninitializeParquetBackground();', sandbox);
const mark = readFileSync(new URL('../assets/brand/plank-mark.svg', import.meta.url), 'utf8').replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '').replace(/<title>[\s\S]*?<\/title>|<desc>[\s\S]*?<\/desc>/g, '');
const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-labelledby="title desc">
<title id="title">Plank</title>
<desc id="desc">The website's programmatic Parquet background, mapping the same taxi file row groups, column chunks, pages, and footer.</desc>
<rect width="${width}" height="${height}" fill="${variables['--plank-canvas']}"/>
${parts.join('\n')}
<g transform="translate(48 64) scale(1.75)">${mark}</g>
<text x="192" y="145" font-family="system-ui, sans-serif" font-size="64" font-weight="700" letter-spacing="-2" fill="${variables['--plank-ink']}">Plank</text>
</svg>\n`;
writeFileSync(fileURLToPath(new URL(`../assets/brand/plank-readme${theme === 'dark' ? '-dark' : ''}.svg`, import.meta.url)), svg);

}
