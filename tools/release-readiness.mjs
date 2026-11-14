export function expectedDownloads(html) {
  const links = [...html.matchAll(/href="(https:\/\/github\.com\/canonliuliang\/StealthWorkspace\/releases\/download\/([^/"?]+)\/([^/"?]+\.zip))"/g)]
    .map(([, url, tag, name]) => ({ url, tag, name }));
  if (links.length !== 2 || new Set(links.map(link => link.tag)).size !== 1 ||
      !links.some(link => link.name.endsWith('-win-x64.zip')) ||
      !links.some(link => link.name.endsWith('-win-arm64.zip'))) {
    throw new Error('Website must link to distinct x64 and ARM64 ZIPs from one release.');
  }
  return links;
}

export function releaseIsReady(release, expected) {
  if (!release || release.draft !== false || release.tag_name !== expected[0].tag) return false;
  const required = [...expected, {
    name: 'SHA256SUMS.txt',
    url: `https://github.com/canonliuliang/StealthWorkspace/releases/download/${expected[0].tag}/SHA256SUMS.txt`
  }];
  return required.every(file => release.assets?.some(asset =>
    asset.name === file.name && asset.state === 'uploaded' && asset.size > 0 && asset.browser_download_url === file.url));
}
