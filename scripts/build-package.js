#!/usr/bin/env node
// Validate and assemble one VPM package without requiring a Unity project.

import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import AdmZip from 'adm-zip';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PACKAGES = {
  'normal-field-offset': 'io.github.noname0310.normal-field-offset',
  'blendshape-pose-override': 'io.github.noname0310.blendshape-pose-override',
};

const readJson = (filename) => JSON.parse(readFileSync(filename, 'utf8'));
const isFile = (filename) => existsSync(filename) && statSync(filename).isFile();
const relativePath = (base, filename) => path.relative(base, filename).split(path.sep).join('/');

function* walk(directory) {
  const entries = readdirSync(directory, { withFileTypes: true })
    .sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
  for (const entry of entries) {
    const filename = path.join(directory, entry.name);
    if (entry.isSymbolicLink()) {
      throw new Error(`Package contains a symbolic link: ${filename}`);
    }
    yield { filename, entry };
    if (entry.isDirectory()) yield* walk(filename);
  }
}

function buildPackage(shortName) {
  const packageId = PACKAGES[shortName];
  const packageDir = path.join(ROOT, 'Packages', packageId);
  const manifestPath = path.join(packageDir, 'package.json');
  const manifest = readJson(manifestPath);
  for (const field of ['name', 'displayName', 'version', 'unity', 'author', 'url', 'vpmDependencies']) {
    const value = manifest[field];
    if (!value || (typeof value === 'object' && Object.keys(value).length === 0)) {
      throw new Error(`Missing manifest field: ${field}`);
    }
  }
  if (manifest.name !== packageId) throw new Error('The package ID must match its directory name');
  if (manifest.license !== 'MIT OR Apache-2.0') {
    throw new Error('Expected the MIT OR Apache-2.0 license expression');
  }
  const version = manifest.version;
  if (typeof version !== 'string' || version.trim() !== version ||
      !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version)) {
    throw new Error('This release workflow supports stable major.minor.patch versions');
  }

  const tag = `${shortName}-v${version}`;
  const archiveName = `${packageId}-${version}.zip`;
  const repository = readJson(path.join(ROOT, 'source.json')).githubRepos[0];
  const expectedUrl = `https://github.com/${repository}/releases/download/${tag}/${archiveName}`;
  if (manifest.url !== expectedUrl) throw new Error(`Update package.json url to ${expectedUrl}`);
  for (const filename of ['README.md', 'CHANGELOG.md', 'LICENSE.md', 'LICENSE-MIT', 'LICENSE-APACHE']) {
    if (!isFile(path.join(packageDir, filename))) throw new Error(`Missing package file: ${filename}`);
  }
  for (const filename of ['LICENSE.md', 'LICENSE-MIT', 'LICENSE-APACHE']) {
    if (!readFileSync(path.join(ROOT, filename)).equals(readFileSync(path.join(packageDir, filename)))) {
      throw new Error(`Package license differs from root license: ${filename}`);
    }
  }

  const files = [];
  const guids = new Set();
  for (const { filename, entry } of walk(packageDir)) {
    if (path.extname(filename) === '.meta') {
      if (!existsSync(filename.slice(0, -'.meta'.length))) {
        throw new Error(`Orphaned metadata: ${filename}`);
      }
      const match = readFileSync(filename, 'utf8').match(/^guid: ([0-9a-f]{32})\r?$/m);
      if (!match || guids.has(match[1])) {
        throw new Error(`Missing, invalid, or duplicate GUID: ${filename}`);
      }
      guids.add(match[1]);
    } else if (!isFile(`${filename}.meta`)) {
      throw new Error(`Missing Unity metadata: ${filename}`);
    }
    if (entry.isFile()) files.push(filename);
  }

  const zip = new AdmZip();
  for (const filename of files.sort()) {
    const entry = zip.addFile(relativePath(packageDir, filename), readFileSync(filename), '', 0o644);
    // ZIP timestamps have no timezone. Use the same local calendar date on every host.
    entry.header.time = new Date(2026, 0, 1, 0, 0, 0);
  }
  const archiveData = zip.toBuffer();
  const verification = new AdmZip(archiveData);
  if (!verification.test()) throw new Error('Archive CRC validation failed');
  if (!verification.readFile('package.json')?.equals(readFileSync(manifestPath))) {
    throw new Error('The archive must contain package.json at its root');
  }

  const outputDir = path.join(ROOT, 'dist', shortName);
  const archivePath = path.join(outputDir, archiveName);
  mkdirSync(outputDir, { recursive: true });
  writeFileSync(archivePath, archiveData);
  writeFileSync(path.join(outputDir, 'package.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  return {
    package_id: packageId,
    version,
    tag,
    title: `${manifest.displayName} ${version}`,
    archive: relativePath(ROOT, archivePath),
    notes: relativePath(ROOT, path.join(packageDir, 'CHANGELOG.md')),
    sha256: createHash('sha256').update(archiveData).digest('hex'),
  };
}

try {
  const { values, positionals } = parseArgs({
    allowPositionals: true,
    options: {
      'github-output': { type: 'string' },
      help: { type: 'boolean', short: 'h' },
    },
  });
  if (values.help) {
    console.log('Usage: node scripts/build-package.js <package> [--github-output <file>]');
    console.log(`Packages: ${Object.keys(PACKAGES).join(', ')}`);
  } else {
    if (positionals.length !== 1 || !Object.hasOwn(PACKAGES, positionals[0])) {
      throw new Error(`Choose one package: ${Object.keys(PACKAGES).join(', ')}`);
    }
    const result = buildPackage(positionals[0]);
    if (values['github-output']) {
      appendFileSync(values['github-output'], Object.entries(result).map(([key, value]) => `${key}=${value}\n`).join(''));
    }
    console.log(JSON.stringify(result, null, 2));
  }
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
