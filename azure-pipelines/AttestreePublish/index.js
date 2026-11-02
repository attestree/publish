// #4830 U13 (plan docs/plans/2026-10-24-1609-feat-own-apps-and-private-winget-sources-plan.md, R15, R32; KTD8).
// The Attestree publish task for Azure Pipelines: a thin wrapper around the attestree-publish CLI.
//
// Node, not PowerShell: Azure Pipelines runs a task's PowerShell handler on Windows agents only, and this task must run
// on Linux and macOS agents too. It uses no npm package: the agent passes inputs as INPUT_<NAME> variables, and the job's
// access token as the SystemVssConnection endpoint's auth parameter.
//
// The service connection input is what ties the job to the connection: without a task referencing it,
// System.OidcRequestUri refuses to mint its token (A1). The CLI asks for the token itself, with the connection's id and the
// job's access token, so no token passes through this script's arguments or output.
'use strict';

const { spawnSync } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

function input(name) {
  const value = process.env['INPUT_' + name.toUpperCase()];
  return value === undefined ? '' : value.trim();
}

function fail(message) {
  console.log('##vso[task.logissue type=error]' + message);
  console.log('##vso[task.complete result=Failed;]' + message);
  process.exit(1);
}

async function main() {
  const rids = { 'win32/x64': 'win-x64', 'linux/x64': 'linux-x64', 'linux/arm64': 'linux-arm64', 'darwin/arm64': 'osx-arm64' };
  const rid = rids[process.platform + '/' + process.arch];
  if (!rid) {
    fail('attestree-publish has no build for a ' + process.platform + ' ' + process.arch + ' agent.');
  }
  const name = rid.startsWith('win-') ? 'attestree-publish-' + rid + '.exe' : 'attestree-publish-' + rid;

  const pins = fs.readFileSync(path.join(__dirname, 'cli-sha256.txt'), 'utf8').split(/\r?\n/).filter((l) => l && !l.startsWith('#'));
  const versionLine = pins.find((l) => l.startsWith('version '));
  const pinLine = pins.find((l) => /^[0-9a-f]{64}\s+/.test(l) && l.split(/\s+/)[1] === name);
  if (!versionLine || !pinLine) {
    fail('This version of the task pins no attestree-publish build for ' + rid + '.');
  }
  const version = versionLine.slice('version '.length).trim();
  const expected = pinLine.split(/\s+/)[0];

  const response = await fetch('https://github.com/attestree/publish/releases/download/cli-v' + version + '/' + name);
  if (!response.ok) {
    fail('The attestree-publish ' + version + ' download answered ' + response.status + '.');
  }
  const bytes = Buffer.from(await response.arrayBuffer());
  const actual = crypto.createHash('sha256').update(bytes).digest('hex');
  if (actual !== expected) {
    fail('The downloaded ' + name + ' hashes to ' + actual + ', not the ' + expected + ' this version of the task pins. Nothing was run.');
  }
  const cli = path.join(os.tmpdir(), 'attestree-publish-' + version + '-' + name);
  fs.writeFileSync(cli, bytes, { mode: 0o755 });

  const args = ['--instance', input('instance'), '--package-id', input('packageId'), '--version', input('version')];
  for (const line of input('installers').split(/\r?\n/)) {
    if (line.trim()) {
      args.push('--installer', line.trim());
    }
  }
  if (input('manifest')) {
    args.push('--manifest', input('manifest'));
  }
  if (input('wait') === 'true') {
    args.push('--wait', '--wait-timeout', input('waitTimeout') || '60');
  }
  if (input('verbose') === 'true') {
    args.push('--verbose');
  }

  const env = Object.assign({}, process.env, {
    // The CLI mints the federated token for this connection through System.OidcRequestUri (SYSTEM_OIDCREQUESTURI).
    ATTESTREE_SERVICE_CONNECTION: input('connectedServiceName'),
    SYSTEM_ACCESSTOKEN: process.env['ENDPOINT_AUTH_PARAMETER_SYSTEMVSSCONNECTION_ACCESSTOKEN'] || process.env['SYSTEM_ACCESSTOKEN'] || '',
  });
  const run = spawnSync(cli, args, { stdio: 'inherit', env });
  if (run.error) {
    fail('attestree-publish could not be started.');
  }
  if (run.status !== 0) {
    console.log('##vso[task.complete result=Failed;]attestree-publish exited with ' + run.status + '.');
  }
  process.exit(run.status === null ? 1 : run.status);
}

main().catch(() => fail('attestree-publish could not be downloaded or run.'));
