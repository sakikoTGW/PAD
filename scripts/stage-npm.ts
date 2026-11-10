#!/usr/bin/env bun
/**
 * 把 DSH 插件摊成一个能直接 npm publish 的平目录。
 *
 * npm 的 files 只能挑包目录里的东西，而插件要带上包外的 modpack/registry.yaml 和
 * pack-index 源码，所以发布前先摊平，再在摊出来的目录里 npm publish。
 *
 * 同一份文件在两个仓里都能跑，布局由 layout.ts 探测。
 * 摊平目录：AGENT_PACK_TMP 有值就用它（兼容老路径 E:\tmp\pack-agent\npm-pack-agent-dsh），
 * 否则用 <repo>/dist/npm-pack-agent-dsh（dist/ 已 ignore）。
 */
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { findPackBase, findRepoRoot } from './layout.js'

const PACKAGE_DIRNAME = 'npm-pack-agent-dsh'
const REPO = findRepoRoot()
const BASE = findPackBase(REPO)

/** 摊平根：尊重 AGENT_PACK_TMP（老工作流写死了这个），否则落在仓库自己的 dist/。 */
function stageRoot(): string {
  const env = process.env.AGENT_PACK_TMP?.trim()
  return env ? env : join(REPO, 'dist')
}

export function stageDshNpmDest(): string {
  return join(stageRoot(), PACKAGE_DIRNAME)
}

function copyRequired(from: string, to: string): void {
  if (!existsSync(from)) throw new Error(`stage missing ${from}`)
  mkdirSync(dirname(to), { recursive: true })
  cpSync(from, to, { recursive: true })
}

/** 包根旁边的东西：monorepo 在 <repo>/，独立仓在 <repo>/。 */
function repoFile(...rel: string[]): string {
  for (const candidate of [join(REPO, ...rel), join(REPO, '..', ...rel)]) {
    if (existsSync(candidate)) return candidate
  }
  return join(REPO, ...rel)
}

export function stageDshNpm(): string {
  const plugin = join(BASE, 'plugin')
  const lib = join(plugin, 'lib', 'index.js')
  if (!existsSync(lib)) {
    throw new Error(`missing ${lib}; run bun run build:dsh first`)
  }

  const dest = stageDshNpmDest()
  rmSync(dest, { recursive: true, force: true })
  mkdirSync(dest, { recursive: true })

  const pluginPkg = JSON.parse(readFileSync(join(plugin, 'package.json'), 'utf8')) as {
    name?: string
    version?: string
    description?: string
    keywords?: string[]
    author?: string
    homepage?: string
    bugs?: unknown
    repository?: unknown
    dsh?: unknown
    engines?: { node?: string; dsh?: string }
  }
  const rootPkg = existsSync(join(REPO, 'package.json'))
    ? (JSON.parse(readFileSync(join(REPO, 'package.json'), 'utf8')) as {
        author?: string
        bugs?: unknown
        repository?: unknown
      })
    : {}

  copyRequired(join(plugin, 'lib'), join(dest, 'lib'))
  copyRequired(join(plugin, 'skills'), join(dest, 'skills'))
  copyRequired(join(plugin, 'cordis.patch.yml'), join(dest, 'cordis.patch.yml'))
  copyRequired(join(plugin, 'README.md'), join(dest, 'README.md'))
  copyRequired(join(plugin, 'README.zh.md'), join(dest, 'README.zh.md'))
  copyRequired(join(plugin, 'INSTALL.md'), join(dest, 'INSTALL.md'))
  copyRequired(repoFile('LICENSE'), join(dest, 'LICENSE'))
  copyRequired(join(BASE, 'modpack', 'registry.yaml'), join(dest, 'agent-pack-dsh', 'modpack', 'registry.yaml'))
  copyRequired(join(BASE, 'pack-index', 'Cargo.toml'), join(dest, 'agent-pack-dsh', 'pack-index', 'Cargo.toml'))
  copyRequired(join(BASE, 'pack-index', 'Cargo.lock'), join(dest, 'agent-pack-dsh', 'pack-index', 'Cargo.lock'))
  copyRequired(join(BASE, 'pack-index', 'src'), join(dest, 'agent-pack-dsh', 'pack-index', 'src'))

  const stagedPkg = {
    name: pluginPkg.name ?? '@sakikotgw/pack-agent-dsh',
    version: pluginPkg.version,
    description: pluginPkg.description,
    type: 'module',
    main: './lib/index.js',
    exports: { '.': './lib/index.js' },
    files: [
      'lib',
      'skills',
      'cordis.patch.yml',
      'agent-pack-dsh',
      'README.md',
      'README.zh.md',
      'INSTALL.md',
      'LICENSE',
    ],
    license: 'MIT',
    author: pluginPkg.author ?? rootPkg.author,
    homepage: pluginPkg.homepage,
    bugs: pluginPkg.bugs ?? rootPkg.bugs,
    repository: pluginPkg.repository ?? rootPkg.repository,
    keywords: pluginPkg.keywords,
    dsh: pluginPkg.dsh,
    // 源清单声明什么就带什么：engines.dsh 是 DSH 的版本门槛，不能在这里丢。
    engines: {
      node: pluginPkg.engines?.node ?? '>=20.11.0',
      ...(pluginPkg.engines?.dsh ? { dsh: pluginPkg.engines.dsh } : {}),
    },
    publishConfig: { access: 'public' },
  }
  writeFileSync(join(dest, 'package.json'), `${JSON.stringify(stagedPkg, null, 2)}\n`)
  return dest
}

if (import.meta.main) {
  console.log(stageDshNpm())
}
