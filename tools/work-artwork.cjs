'use strict';
// One selection keeps preparation, interpolation, previews and installation on
// the same artwork revision. A missing selection preserves the original v3.
const fs = require('node:fs');
const path = require('node:path');
const repo = path.resolve(__dirname, '..');
const selectionFile = path.join(repo, 'reference/work-artwork-selection.json');

// A process-local override builds a candidate without changing the installed
// selection. An explicit file argument still has the highest priority.
function loadWorkArtwork(file = process.env.TABLEPET_WORK_SELECTION || selectionFile) {
  let selection;
  try { selection = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '')); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    selection = { artworkVersion:3, transitionVersion:1 };
  }
  const { artworkVersion, transitionVersion } = selection;
  if (!Number.isSafeInteger(artworkVersion) || artworkVersion < 3 ||
      !Number.isSafeInteger(transitionVersion) || transitionVersion < 1)
    throw Error('Work artwork selection requires artworkVersion >= 3 and transitionVersion >= 1');
  const assetRoot = path.join(repo, 'assets/strawberry-cat');
  const previewName = 'work-preview-v' + artworkVersion;
  const transitionName = 'work-transition-v' + transitionVersion;
  return Object.freeze({
    artworkVersion, transitionVersion, previewName, transitionName,
    sourceFile:'sources/work-v' + artworkVersion + '/focused-eyes.png',
    previewRoot:path.join(assetRoot, previewName),
    transitionRoot:path.join(assetRoot, transitionName),
    gazeRoot:path.join(assetRoot, transitionName, 'gaze'),
    artworkReference:path.join(repo, 'reference/work-v' + artworkVersion),
    artworkArtifacts:path.join(repo, 'artifacts/work-v' + artworkVersion),
    transitionArtifacts:path.join(repo, 'artifacts', transitionName)
  });
}

module.exports = { loadWorkArtwork };
