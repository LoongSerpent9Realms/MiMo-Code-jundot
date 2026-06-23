#!/usr/bin/env bun

import { $ } from "bun"
import { fileURLToPath } from "url"

const DEFAULT_GH_REPO = "LoongSerpent9Realms/MiMo-Code-jundot"
const dir = fileURLToPath(new URL("..", import.meta.url))
process.chdir(dir)

const required = (name: string) => {
  const val = process.env[name]
  if (!val) throw new Error(`Missing required env: ${name}`)
  return val
}

const GH_REPO = process.env.GH_REPO || DEFAULT_GH_REPO
const SKIP_NPM_PUBLISH = process.env.SKIP_NPM_PUBLISH === "1" || process.env.SKIP_NPM_PUBLISH === "true"

if (!SKIP_NPM_PUBLISH) {
  const NPM_TOKEN = required("NPM_TOKEN")
  process.env.NODE_AUTH_TOKEN = NPM_TOKEN
}

process.env.GH_REPO = GH_REPO
process.env.GH_TOKEN = process.env.GH_TOKEN || process.env.GITHUB_TOKEN

if (!process.env.GH_TOKEN) throw new Error("Missing required env: GH_TOKEN or GITHUB_TOKEN")

console.log("=== build ===\n")
await $`bun packages/opencode/script/build.ts --single --skip-install`

console.log("=== version ===\n")
await $`bun script/version.ts`

const { Script } = await import("@mimo-ai/script")
console.log(`\nReleasing v${Script.version} (channel: ${Script.channel})\n`)

if (!SKIP_NPM_PUBLISH) {
  console.log("\n=== publish npm ===\n")
  await $`./script/publish.ts`
} else {
  console.log("\n=== skip npm publish (SKIP_NPM_PUBLISH=1) ===\n")
}

if (Script.release) {
  console.log("\n=== finalize release ===\n")
  await $`gh release edit v${Script.version} --draft=false --repo ${GH_REPO}`
  console.log(`https://github.com/${GH_REPO}/releases/tag/v${Script.version}`)
}

console.log("\n=== done ===")
