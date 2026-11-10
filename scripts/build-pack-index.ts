#!/usr/bin/env bun
/**
 * 编译 pack-index（Windows 走 VS Build Tools vcvars64）。
 * 产物写到临时根（AGENT_PACK_TMP，缺省 <仓库盘>:\tmp\pack-agent）下的 pack-index-target，
 * 不落 C:\Users\...\Temp —— 2026-08-13 曾被撑满过。
 *
 * 布局自己探测（见 layout.ts），monorepo 和 PAD 独立仓同一份文件。
 */
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { findPackBase, findRepoRoot, scratchRoot } from './layout.js'

const REPO = findRepoRoot()
const BASE = findPackBase(REPO)

export function packIndexTargetDir(): string {
  return process.env.CARGO_TARGET_DIR?.trim() || join(scratchRoot(REPO), 'pack-index-target')
}

export function packIndexBinPath(): string {
  if (process.env.PACK_INDEX_BIN?.trim()) return process.env.PACK_INDEX_BIN.trim()
  const name = process.platform === 'win32' ? 'pack-index.exe' : 'pack-index'
  return join(packIndexTargetDir(), 'release', name)
}

function findVcvars64(): string | undefined {
  const pf86 = process.env['ProgramFiles(x86)']
  if (!pf86) return undefined
  const vswhere = join(pf86, 'Microsoft Visual Studio', 'Installer', 'vswhere.exe')
  if (!existsSync(vswhere)) return undefined
  const r = spawnSync(
    vswhere,
    [
      '-latest',
      '-products',
      '*',
      '-requires',
      'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
      '-find',
      '**/vcvars64.bat',
    ],
    { encoding: 'utf8' },
  )
  return (r.stdout || '')
    .split(/\r?\n/)
    .map(s => s.trim())
    .find(s => s.toLowerCase().endsWith('vcvars64.bat'))
}

export function buildPackIndex(): string {
  const bin = packIndexBinPath()
  if (existsSync(bin) && !process.env.PACK_INDEX_REBUILD) return bin

  const manifest = join(BASE, 'pack-index', 'Cargo.toml')
  if (!existsSync(manifest)) throw new Error(`missing ${manifest}`)
  const target = packIndexTargetDir()
  mkdirSync(target, { recursive: true })
  const tmp = scratchRoot(REPO)
  mkdirSync(tmp, { recursive: true })
  const env = {
    ...process.env,
    CARGO_TARGET_DIR: target,
    TMP: tmp,
    TEMP: tmp,
    TMPDIR: tmp,
  }
  const spawnOpts = {
    encoding: 'utf8' as const,
    env,
    cwd: dirname(manifest),
    maxBuffer: 64 * 1024 * 1024,
    timeout: 10 * 60 * 1000,
  }

  let r
  if (process.platform === 'win32') {
    const vc = findVcvars64()
    if (!vc) {
      throw new Error('未找到 MSVC vcvars64.bat，装 VS Build Tools 的 C++ 工作负载后再编 pack-index')
    }
    const bat = join(tmp, 'build-pack-index.bat')
    const body = [
      '@echo off',
      `call "${vc}"`,
      `if errorlevel 1 exit /b 1`,
      `cargo build --release --manifest-path "${manifest}" --target-dir "${target}"`,
      'exit /b %ERRORLEVEL%',
      '',
    ].join('\r\n')
    writeFileSync(bat, body, 'utf8')
    r = spawnSync('cmd.exe', ['/c', bat], spawnOpts)
  } else {
    r = spawnSync(
      'cargo',
      ['build', '--release', '--manifest-path', manifest, '--target-dir', target],
      spawnOpts,
    )
  }
  if (r.status !== 0) {
    throw new Error(`cargo build pack-index failed:\n${r.stderr || ''}\n${r.stdout || ''}`)
  }
  if (!existsSync(bin)) {
    throw new Error(`pack-index binary missing at ${bin}`)
  }
  return bin
}

export function ensurePackIndexBin(): string {
  const bin = packIndexBinPath()
  if (existsSync(bin) && !process.env.PACK_INDEX_REBUILD) return bin
  return buildPackIndex()
}

if (import.meta.main) {
  const bin = buildPackIndex()
  console.log(bin)
}
