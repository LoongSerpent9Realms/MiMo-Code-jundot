import { describe, expect, test } from "bun:test"
import { Locale } from "../../src/util"

describe("Locale.truncate", () => {
  test("truncates long strings", () => {
    expect(Locale.truncate("abcdef", 4)).toBe("abc…")
  })

  test("ignores missing or invalid text", () => {
    expect(Locale.truncate(undefined, 4)).toBe("")
    expect(Locale.truncate(null, 4)).toBe("")
    expect(Locale.truncate(1, 4)).toBe("")
  })
})
