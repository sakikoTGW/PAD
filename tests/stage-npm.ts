#!/usr/bin/env bun
/** 校验 npm 摊平产物：文件齐、manifest 对、engines 没丢、能真的被 node 加载。 */
import { existsSync, mkdtempSync, readFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { loadRegistry } from '../modpack/registry.js'
import { stageDshNpm } from '../scripts/stage-npm.js'
import { findRepoRoot } from '../scripts/layout.js'

function fail(msg: string): never {
  console.error(`✗ ${msg}`)
  process.exit(1)
}

const REPO = findRepoRoot()
const dest = stageDshNpm()
for (const rel of [
  'package.json',
  'lib/index.js',
  'cordis.patch.yml',
  'README.md',
  'README.zh.md',
  'skills/pack-agent-dsh/SKILL.md',
  'agent-pack-dsh/modpack/registry.yaml',
  'agent-pack-dsh/pack-index/Cargo.toml',
  'agent-pack-dsh/pack-index/src/main.rs',
  'LICENSE',
]) {
  const p = join(dest, rel)
  if (!existsSync(p)) fail(`staged missing ${p}`)
}

const pkg = JSON.parse(readFileSync(join(dest, 'package.json'), 'utf8')) as {
  name?: string
  files?: string[]
  dsh?: { bundle?: { patch?: string } }
  engines?: { node?: string; dsh?: string }
}
if (pkg.name !== '@sakikotgw/pack-agent-dsh') fail(`staged name ${pkg.name}`)
if (!pkg.dsh?.bundle?.patch) fail('staged package is not a dsh.bundle')
if (pkg.files?.includes('src')) fail('DSH tarball must not ship TypeScript src')

// engines 必须原样带上：engines.dsh 是 DSH 的版本门槛，stage 丢了就等于发布一个没有门槛的包。
const sourcePkg = JSON.parse(readFileSync(join(import.meta.dirname, '../plugin/package.json'), 'utf8')) as {
  engines?: { node?: string; dsh?: string }
}
if (pkg.engines?.dsh !== sourcePkg.engines?.dsh) {
  fail(`staged engines.dsh ${pkg.engines?.dsh} != source ${sourcePkg.engines?.dsh}`)
}
if (pkg.engines?.node !== sourcePkg.engines?.node) {
  fail(`staged engines.node ${pkg.engines?.node} != source ${sourcePkg.engines?.node}`)
}

const lib = readFileSync(join(dest, 'lib', 'index.js'), 'utf8')
if (!lib.includes('@sakikotgw/pack-agent-dsh')) {
  fail('bundled lib must accept @sakikotgw/pack-agent-dsh as package root')
}

const reg = loadRegistry(join(dest, 'agent-pack-dsh', 'modpack', 'registry.yaml'))
if (!reg.units || Object.keys(reg.units).length < 1) fail('empty registry in staged package')

const skill = readFileSync(join(dest, 'skills', 'pack-agent-dsh', 'SKILL.md'), 'utf8')
if (!skill.includes('@sakikotgw/pack-agent-dsh')) {
  fail('staged SKILL.md must tell the host to add @sakikotgw/pack-agent-dsh')
}
const stagedReadme = readFileSync(join(dest, 'README.md'), 'utf8')
if (!stagedReadme.includes('dsh plugin --profile web add @sakikotgw/pack-agent-dsh')) {
  fail('staged README.md must install @sakikotgw/pack-agent-dsh')
}
if (stagedReadme.includes('北极星') || stagedReadme.toLowerCase().includes('north star')) {
  fail('DSH README must not use north-star copy')
}
if (stagedReadme.includes('便于被发现') || stagedReadme.includes('so they can be found')) {
  fail('DSH README must not tell installers to tag GitHub topics')
}
if (!stagedReadme.includes('https://www.npmjs.com/package/@sakikotgw/pack-agent-dsh')) {
  fail('DSH README must link the npm package')
}
if (!stagedReadme.includes('https://awesome-dsh-plugin.com/badge.svg')) {
  fail('DSH README must show the awesome-dsh-plugin badge')
}
if (!stagedReadme.includes('Manage DeepSeek Harness config like a Minecraft modpack.')) {
  fail('DSH README first sentence must be the Minecraft-modpack hook')
}
const stagedReadmeZh = readFileSync(join(dest, 'README.zh.md'), 'utf8')
if (!stagedReadmeZh.includes('像 MC 整合包一样管理 DeepSeek Harness 配置。')) {
  fail('DSH README.zh.md first sentence must be the Minecraft-modpack hook')
}
if (skill.includes('dsh plugin --profile web add "<compiled-dir>"')) {
  fail('SKILL.md must not tell the model to plugin-add a compiled pack')
}

// node 冒烟：真把 staged lib 加载一遍，走一次 pack 投影。
// fixture 在 monorepo 是 test/fixtures，在 PAD 独立仓是 tests/fixtures；两处都没有就跳过。
const fixture = [
  join(REPO, 'test', 'fixtures', 'demo.pack.json'),
  join(REPO, 'tests', 'fixtures', 'demo.pack.json'),
].find(p => existsSync(p))

if (fixture) {
  const href = pathToFileURL(join(dest, 'lib', 'index.js')).href
  const out = mkdtempSync(join(tmpdir(), 'stage-dsh-smoke-'))
  const smoke = join(import.meta.dirname, 'node-smoke.mjs')
  const r = spawnSync('node', [smoke, href, fixture, out], {
    encoding: 'utf8',
    cwd: dest,
    env: { ...process.env },
  })
  if (r.status !== 0) {
    fail(`node import of staged lib failed (exit ${r.status}):\n${r.stderr || ''}\n${r.stdout || ''}`)
  }
  if (!r.stdout?.includes('ok')) fail(`unexpected staged node stdout: ${r.stdout}`)
} else {
  console.log('! no test fixture (test/fixtures or tests/fixtures), skipped node smoke')
}

console.log(`✓ staged ${dest}`)
console.log('[OK] stage-dsh-npm')
