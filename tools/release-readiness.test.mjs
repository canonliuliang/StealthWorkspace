import test from 'node:test';
import assert from 'node:assert/strict';
import { expectedDownloads, releaseIsReady } from './release-readiness.mjs';

const root = 'https://github.com/canonliuliang/StealthWorkspace/releases/download/v1-preview/';
const html = ['win-x64', 'win-arm64'].map(arch => `<a href="${root}Stealth-1-preview-${arch}.zip">Download</a>`).join('');
const expected = expectedDownloads(html);
const complete = {
  draft: false, tag_name: 'v1-preview',
  assets: [...expected, { name: 'SHA256SUMS.txt', url: root + 'SHA256SUMS.txt' }]
    .map(file => ({ name: file.name, browser_download_url: file.url, state: 'uploaded', size: 512 }))
};

test('public release with both architectures and checksums can publish its website', () => {
  assert.equal(releaseIsReady(complete, expected), true);
});
test('partial upload, draft and missing release defer deployment', () => {
  assert.equal(releaseIsReady(null, expected), false);
  assert.equal(releaseIsReady({ ...complete, draft: true }, expected), false);
  for (const omitted of complete.assets) {
    assert.equal(releaseIsReady({ ...complete, assets: complete.assets.filter(asset => asset !== omitted) }, expected), false);
  }
});
test('wrong version, empty assets and unfinished uploads cannot produce live download buttons', () => {
  assert.equal(releaseIsReady({ ...complete, tag_name: 'v0' }, expected), false);
  for (const override of [{ size: 0 }, { state: 'new' }, { browser_download_url: 'https://example.com/other.zip' }]) {
    assert.equal(releaseIsReady({ ...complete, assets: [{ ...complete.assets[0], ...override }, ...complete.assets.slice(1)] }, expected), false);
  }
});
test('two visually distinct architecture buttons cannot share the same download', () => {
  assert.throws(() => expectedDownloads(html.replaceAll('win-arm64', 'win-x64')));
  assert.throws(() => expectedDownloads(html.replace('v1-preview/Stealth-1-preview-win-arm64', 'v2-preview/Stealth-1-preview-win-arm64')));
});
