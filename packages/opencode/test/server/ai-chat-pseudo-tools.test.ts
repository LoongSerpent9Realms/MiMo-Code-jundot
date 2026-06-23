import { describe, expect, it } from "bun:test"
import { parsePseudoToolCalls } from "../../src/server/routes/instance/ai_chat"

describe("ai chat pseudo tool calls", () => {
  it("converts legacy grep_code XML calls into structured grep tool calls", () => {
    const calls = parsePseudoToolCalls(`
<tool_call>
<function=grep_code>
<parameter=path>editor/ai/ai_chat_panel.cpp</parameter>
<parameter=pattern>_add_attachment</parameter>
<parameter=limit>30</parameter>
</function>
</tool_call>
`)

    expect(calls).toHaveLength(1)
    expect(calls[0].function.name).toBe("grep")
    expect(JSON.parse(calls[0].function.arguments)).toEqual({
      path: "editor/ai/ai_chat_panel.cpp",
      pattern: "_add_attachment",
    })
  })

  it("keeps a legacy name when that is the declared available tool", () => {
    const calls = parsePseudoToolCalls(
      `
<tool_call>
<function=grep_code>
<parameter=path>editor/ai/ai_chat_panel.cpp</parameter>
<parameter=pattern>_add_attachment</parameter>
</function>
</tool_call>
`,
      new Set(["grep_code"]),
    )

    expect(calls).toHaveLength(1)
    expect(calls[0].function.name).toBe("grep_code")
  })

  it("normalizes read_file path into read_files paths", () => {
    const calls = parsePseudoToolCalls(`
<tool_call>
<function=read_file>
<parameter=path>src/main.ts</parameter>
</function>
</tool_call>
`)

    expect(calls).toHaveLength(1)
    expect(calls[0].function.name).toBe("read_files")
    expect(JSON.parse(calls[0].function.arguments)).toEqual({
      paths: ["src/main.ts"],
    })
  })
})
