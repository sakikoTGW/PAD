/**
 * 三个打包脚本共用的布局探测。
 *
 * 同一份文件在两个仓里都能跑：
 *   monorepo（pack-agent）  <repo>/agent-pack-dsh/{plugin,gateway,pack-index,modpack}
 *   独立仓（PAD）           <repo>/{plugin,gateway,pack-index,modpack}
 *
 * 不依赖 src/ —— 那两个模块（package-root / tmp-root）只有 monorepo 有。
 */
import { existsSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'

/** 往上找 .git（仓库是目录，worktree 是文件）。找不到就退回脚本的上一级。 */
export function findRepoRoot(from = import.meta.dirname): string {
  let dir = resolve(from, '..')
  for (let i = 0; i < 8; i++) {
    if (existsSync(join(dir, '.git'))) return dir
    const parent = dirname(dir)
    if (parent === dir) break
    dir = parent
  }
  return resolve(from, '..')
}

/** 到底在 monorepo 的 agent-pack-dsh 下，还是就在仓库根。 */
export function findPackBase(repo: string): string {
  const nested = join(repo, 'agent-pack-dsh')
  if (existsSync(join(nested, 'plugin'))) return nested
  if (existsSync(join(repo, 'plugin'))) return repo
  throw new Error(`plugin/ not found under ${repo}`)
}

/**
 * 临时根，给 cargo / dotnet / 解压这类大产物用：
 *   AGENT_PACK_TMP → <仓库盘>:\tmp\pack-agent → <repo>\dist\tmp
 * 规则和 monorepo 的 src/tmp-root.ts 一致，只是这里不 import 它。
 */
export function scratchRoot(repo: string): string {
  const env = process.env.AGENT_PACK_TMP?.trim()
  if (env) return env
  const drive = /^([A-Za-z]:)/.exec(repo)?.[1]
  const system = (process.env.SystemDrive || 'C:').toUpperCase()
  if (drive && drive.toUpperCase() !== system) return join(drive + '\\', 'tmp', 'pack-agent')
  return join(repo, 'dist', 'tmp')
}
