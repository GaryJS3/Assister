'use strict';
const notice = document.querySelector('#notice');
const archive = document.querySelector('#releases');
async function loadReleases() {
  const response = await fetch('/api/updates', { cache: 'no-store' });
  if (!response.ok) throw new Error('Could not load releases.');
  const releases = await response.json();
  archive.replaceChildren();
  if (!releases.length) { archive.textContent = 'No releases yet. Publish the first package to get started.'; return; }
  for (const release of releases) {
    const row = document.createElement('article'); row.className = 'release';
    const details = document.createElement('div');
    const title = document.createElement('h3'); title.textContent = release.appId;
    const meta = document.createElement('p'); meta.className = 'release-meta';
    meta.textContent = `${release.platform} / v${release.version} / ${(release.size / 1048576).toFixed(1)} MiB / ${new Date(release.publishedAt).toLocaleDateString()}`;
    const notes = document.createElement('p'); notes.textContent = release.notes || 'No release notes.';
    const hash = document.createElement('code'); hash.textContent = `SHA-256 ${release.sha256}`;
    details.append(title, meta, notes, hash);
    const download = document.createElement('a'); download.href = release.downloadUrl; download.textContent = `Download ${release.fileName}`;
    row.append(details, download); archive.append(row);
  }
}
document.querySelector('#refresh').addEventListener('click', () => loadReleases().catch(error => { notice.textContent = error.message; }));
document.querySelector('#upload').addEventListener('submit', async event => {
  event.preventDefault();
  const form = event.target; const button = form.querySelector('button'); const file = form.elements.package.files[0];
  if (!file || file.size === 0 || file.size > 512 * 1048576) { notice.textContent = 'Choose a nonempty package up to 512 MiB.'; return; }
  button.disabled = true; notice.textContent = 'Uploading package…';
  try {
    const path = [form.elements.appId.value, form.elements.platform.value, form.elements.version.value].map(encodeURIComponent).join('/');
    const query = new URLSearchParams({ name: file.name, notes: form.elements.notes.value });
    const response = await fetch(`/api/updates/${path}?${query}`, { method: 'PUT', headers: { Authorization: `Bearer ${form.elements.token.value}`, 'Content-Type': 'application/octet-stream' }, body: file });
    if (!response.ok) {
      const detail = await response.json().catch(() => null);
      throw new Error(detail?.error || detail?.detail || (response.status === 401 ? 'Upload token was rejected.' : `Upload failed (${response.status}).`));
    }
    form.elements.token.value = ''; notice.textContent = 'Release published. Clients can download it now.';
    await loadReleases();
  } catch (error) { notice.textContent = error.message; }
  finally { button.disabled = false; }
});
loadReleases().catch(error => { notice.textContent = error.message; });
