import { Hono } from "hono"
import { describeRoute, validator } from "hono-openapi"
import z from "zod"
import { jsonSchema, tool, type ModelMessage, type Tool } from "ai"
import { Effect } from "effect"
import * as Stream from "effect/Stream"
import { Agent } from "@/agent/agent"
import { Bus } from "@/bus"
import { TuiEvent } from "@/cli/cmd/tui/event"
import { Provider } from "@/provider"
import { LLM } from "@/session/llm"
import { Session } from "@/session"
import { MessageID, PartID, SessionID } from "@/session/schema"
import { ModelID, ProviderID } from "@/provider/schema"
import { MessageV2 } from "@/session/message-v2"
import { errorMessage } from "@/util/error"
import { errors } from "../../error"
import { runRequest } from "./trace"

const JsonValue = z.any()

const ChatMessage = z
  .object({
    role: z.enum(["user", "system", "assistant", "developer", "tool"]),
    content: z.union([z.string(), z.array(JsonValue), z.null()]).optional(),
    tool_calls: z.array(JsonValue).optional(),
    tool_call_id: z.string().optional(),
    name: z.string().optional(),
  })
  .passthrough()

const ChatInput = z
  .object({
    message: z.string().optional().describe("The user's message"),
    plugin_id: z.string().optional().describe("Jundot plugin ID"),
    action: z.string().optional().describe("Jundot action"),
    sessionID: z.string().optional().describe("Optional session ID to continue conversation"),
    sessionId: z.string().optional().describe("Optional session ID to continue conversation"),
    model: z
      .object({
        providerID: z.string(),
        modelID: z.string(),
      })
      .optional()
      .describe("Optional model selection"),
    agent: z.string().optional().describe("Optional agent name"),
    messages: z.array(ChatMessage).optional().describe("Optional message history"),
    tools: z.array(JsonValue).optional().describe("OpenAI-compatible tool definitions"),
    context_mode: z.string().optional().describe("Jundot context mode"),
    output_language: z.string().optional().describe("Jundot output language"),
  })
  .passthrough()

export type ChatToolCall = { id: string; type: "function"; function: { name: string; arguments: string } }
type CollectedChatResponse = {
  content: string
  finishReason: string
  toolCalls: ChatToolCall[]
  eventTypes: string[]
  errors: string[]
}

let jundotMirrorSessionID: SessionID | undefined
const JUNDOT_WEB_SOURCE_POLICY =
  "When research needs current web information, prefer authoritative sources and avoid low-quality small sites. For game-related research, prefer Steam, Epic Games Store, official game or publisher sites, and Bilibili videos. If the editor exposes fetch_url, use it only with authoritative URLs and save research files under .JundotAI/research/."

function contentToText(content: unknown): string {
  if (typeof content === "string") return content
  if (content == null) return ""
  if (Array.isArray(content)) {
    return content
      .map((part) => {
        if (typeof part === "string") return part
        if (part && typeof part === "object" && "text" in part && typeof part.text === "string") return part.text
        return JSON.stringify(part)
      })
      .filter(Boolean)
      .join("\n")
  }
  return JSON.stringify(content)
}

function compactToolOutput(text: string) {
  const limit = 12_000
  if (text.length <= limit) return text
  return text.slice(0, limit) + `\n\n[Tool output truncated: ${text.length - limit} characters omitted]`
}

function normalizeMessages(input: z.infer<typeof ChatInput>): ModelMessage[] {
  const toolNames = new Map<string, string>()

  return (input.messages ?? [])
    .flatMap((message): ModelMessage[] => {
      if (message.role === "developer" || message.role === "system") {
        return [{ role: "system", content: contentToText(message.content) }]
      }

      if (message.role === "user") {
        return [{ role: "user", content: contentToText(message.content) }]
      }

      if (message.role === "assistant") {
        const content: any[] = []
        const text = contentToText(message.content)
        if (text) content.push({ type: "text", text })
        for (const call of message.tool_calls ?? []) {
          const id = String(call?.id ?? "")
          const name = String(call?.function?.name ?? call?.name ?? "")
          if (!id || !name) continue
          toolNames.set(id, name)
          let args: unknown = call?.function?.arguments ?? call?.arguments ?? {}
          if (typeof args === "string") {
            try {
              args = JSON.parse(args)
            } catch {}
          }
          content.push({ type: "tool-call", toolCallId: id, toolName: name, input: args })
        }
        return [{ role: "assistant", content: content.length ? content : "" } as ModelMessage]
      }

      if (message.role === "tool") {
        const toolCallId = message.tool_call_id ?? ""
        if (!toolCallId) return []
        const toolName = message.name ?? toolNames.get(toolCallId) ?? "tool"
        return [
          {
            role: "tool",
            content: [
              {
                type: "tool-result",
                toolCallId,
                toolName,
                output: { type: "text", value: compactToolOutput(contentToText(message.content)) },
              },
            ],
          } as ModelMessage,
        ]
      }

      return []
    })
    .filter((message) => message.role !== "user" || contentToText(message.content).trim())
}

function inferMessage(input: z.infer<typeof ChatInput>) {
  if (input.message?.trim()) return input.message
  const lastUser = [...(input.messages ?? [])].reverse().find((message) => message.role === "user")
  return contentToText(lastUser?.content).trim()
}

function hasToolResults(input: z.infer<typeof ChatInput>) {
  return (input.messages ?? []).some((message) => message.role === "tool")
}

function isBroadImplementationRequest(message: string) {
  return /做.*功能|实现|添加|新增|修改|修复|开发|重构|帮我做/.test(message)
}

function hasExplicitPath(message: string) {
  return /([A-Za-z]:[\\/]|res:\/\/|[A-Za-z0-9_.-]+[\\/][A-Za-z0-9_.\\/ -]+|\.[A-Za-z0-9_]+)\b/.test(message)
}

function isContinueRequest(message: string) {
  return /^(please\s+)?continue\b/i.test(message.trim()) || /\u7ee7\u7eed|\u63a5\u7740|\u4ece\u521a\u624d|\u4ece\u4e0a\u6b21/.test(message)
}

function hasImplementationHistory(messages: ModelMessage[]) {
  return messages.some((item) => {
    if (item.role !== "user" && item.role !== "assistant") return false
    const text = contentToText(item.content)
    return isBroadImplementationRequest(text) || hasExplicitPath(text)
  })
}

function stripEchoedTranscript(content: string) {
  if (!/^\s*(User|Assistant|You|AI)\s*[:\uFF1A]/i.test(content)) return content

  const paragraphs = content.split(/(\r?\n\s*\r?\n)/)
  let index = 0
  let stripped = false

  while (index < paragraphs.length) {
    const paragraph = paragraphs[index] ?? ""
    if (!paragraph.trim()) {
      index++
      continue
    }
    if (!/^\s*(User|Assistant|You|AI)\s*[:\uFF1A]/i.test(paragraph)) break
    stripped = true
    index++
    while (index < paragraphs.length && !(paragraphs[index] ?? "").trim()) index++
  }

  if (!stripped) return content
  const next = paragraphs.slice(index).join("").trimStart()
  return next || content
}

function decodeXmlText(input: string) {
  return input
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&amp;/g, "&")
}

function parseScalar(input: string): unknown {
  const value = decodeXmlText(input.trim())
  try {
    return JSON.parse(value)
  } catch {}
  if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
    return value.slice(1, -1).replace(/\\\\/g, "\\").replace(/\\"/g, '"').replace(/\\'/g, "'")
  }
  if (/^-?\d+(\.\d+)?$/.test(value)) return Number(value)
  if (value === "true") return true
  if (value === "false") return false
  if (value === "null") return null
  return value
}

const PSEUDO_TOOL_NAME_ALIASES: Record<string, string> = {
  grep_code: "grep",
  search_code: "grep",
  read_file: "read_files",
  webfetch: "fetch_url",
}

function normalizePseudoToolCall(name: string, args: Record<string, unknown>, availableToolNames?: Set<string>) {
  const aliasedName = PSEUDO_TOOL_NAME_ALIASES[name] ?? name
  const normalizedName =
    availableToolNames && !availableToolNames.has(aliasedName) && availableToolNames.has(name) ? name : aliasedName

  if (normalizedName === "read_files") {
    if (!("paths" in args)) {
      const path = args.path ?? args.filePath
      if (path !== undefined) args.paths = path
    }
    delete args.path
    delete args.filePath
    if (typeof args.paths === "string") args.paths = [args.paths]
  }

  if (normalizedName === "grep" && "limit" in args) {
    delete args.limit
  }

  if (normalizedName === "fetch_url" && !("dest_path" in args)) {
    const rawUrl = typeof args.url === "string" ? args.url : "research"
    let filename = "research.txt"
    try {
      const url = new URL(rawUrl)
      filename = (url.hostname + url.pathname).replace(/[^A-Za-z0-9_.-]+/g, "_").replace(/^_+|_+$/g, "")
      if (!filename) filename = "research"
      if (!/\.[A-Za-z0-9]+$/.test(filename)) filename += ".html"
    } catch {}
    args.dest_path = `.JundotAI/research/${filename}`
  }

  return normalizedName
}

export function parsePseudoToolCalls(content: string, availableToolNames?: Set<string>): ChatToolCall[] {
  const result: ChatToolCall[] = []
  const blocks = content.matchAll(/<tool_call>([\s\S]*?)<\/tool_call>/g)
  let index = 0

  for (const block of blocks) {
    const inner = block[1] ?? ""
    const fn = inner.match(/<function=([A-Za-z0-9_.-]+)>([\s\S]*?)<\/function>/)
    if (!fn) continue

    const args: Record<string, unknown> = {}
    const params = fn[2].matchAll(/<parameter=([A-Za-z0-9_.-]+)>([\s\S]*?)<\/parameter>/g)
    for (const param of params) {
      args[param[1]] = parseScalar(param[2] ?? "")
    }
    const name = normalizePseudoToolCall(fn[1], args, availableToolNames)

    result.push({
      id: `call_mimocode_text_${Date.now()}_${index++}`,
      type: "function",
      function: {
        name,
        arguments: JSON.stringify(args),
      },
    })
  }

  return result
}

function buildPlanningResponse() {
  return [
    "这个功能建议先拆成几步做：",
    "",
    "1. 在文件系统 Dock 的右键菜单里增加“向 AI 提问此文件”。",
    "2. 取得当前选中文件路径，并把路径/文件名作为上下文传给 AI 面板。",
    "3. 在 AI 面板新增接收外部文件提问的入口，自动填充提示词或直接发送。",
    "4. 补充状态提示：未启动 HTTP 服务、未选中文件、目录不可直接提问等情况。",
    "5. 最后再做一次编辑器内手动验证。",
    "",
    "第一步建议先看 `editor/docks/filesystem_dock.*` 的右键菜单代码，再接到 `editor/ai/*`。",
  ].join("\n")
}

function normalizeTools(input: z.infer<typeof ChatInput>): Record<string, Tool> {
  const result: Record<string, Tool> = {}
  for (const item of input.tools ?? []) {
    const fn = item?.type === "function" ? item.function : item?.function ?? item
    const name = fn?.name
    if (typeof name !== "string" || !name) continue
    result[name] = tool({
      description: typeof fn.description === "string" ? fn.description : undefined,
      inputSchema: jsonSchema(fn.parameters ?? { type: "object", additionalProperties: true }),
    })
  }
  return result
}

function addToolCall(
  toolCalls: ChatToolCall[],
  seenToolCallIds: Set<string>,
  toolCallId: unknown,
  toolName: unknown,
  input: unknown,
) {
  const id = String(toolCallId ?? "")
  const name = String(toolName ?? "")
  if (!id || !name || seenToolCallIds.has(id)) return
  seenToolCallIds.add(id)
  toolCalls.push({
    id,
    type: "function",
    function: {
      name,
      arguments: JSON.stringify(input ?? {}),
    },
  })
}

function toExistingSessionID(input: string | undefined) {
  if (!input) return undefined
  try {
    return SessionID.make(input)
  } catch {
    return undefined
  }
}

function buildErrorChatResponse(sessionID: string, model: string, error: unknown) {
  const content = `AI request failed inside MiMoCode: ${errorMessage(error)}`
  const choices = [
    {
      index: 0,
      message: {
        role: "assistant",
        content,
      },
      finish_reason: "stop",
    },
  ]
  return {
    id: `chatcmpl_${sessionID}`,
    object: "chat.completion",
    created: Math.floor(Date.now() / 1000),
    model,
    choices,
    content,
    finish_reason: "stop",
    openai_compatible: { choices },
  }
}

export const AiChatRoutes = () => {
  const app = new Hono()

  app.post(
    "/",
    describeRoute({
      summary: "AI Chat",
      description: "Stream AI chat completions",
      operationId: "ai.chat",
      responses: {
        200: {
          description: "Stream of chat events",
          content: {
            "application/json": {},
          },
        },
        ...errors(400, 404),
      },
    }),
    validator("json", ChatInput),
    async (c) => {
      const body = c.req.valid("json")
      const message = inferMessage(body)
      if (!message) {
        c.status(400)
        return c.json({ error: "message or messages with a user entry is required" })
      }

      const fallbackSessionID = body.sessionID ?? body.sessionId ?? SessionID.descending()
      const fallbackModel = body.model ? `${body.model.providerID}/${body.model.modelID}` : "unknown"
      let result
      try {
        result = await runRequest(
        "AiChatRoutes.chat",
        c,
        Effect.gen(function* () {
          const agentService = yield* Agent.Service
          const providerService = yield* Provider.Service
          const llmService = yield* LLM.Service
          const sessionService = yield* Session.Service
          const busService = yield* Bus.Service

          const agentName = body.agent ?? (yield* agentService.defaultAgent())
          const agent = yield* agentService.get(agentName)

          const model = body.model ?? (yield* providerService.defaultModel())
          const resolvedModel = yield* providerService.getModel(ProviderID.make(model.providerID), ModelID.make(model.modelID))

          const sessionID =
            body.sessionID || body.sessionId ? SessionID.make(body.sessionID ?? body.sessionId!) : SessionID.descending()

          const messages = normalizeMessages(body)
          if (!messages.some((item) => item.role === "user" && contentToText(item.content).trim() === message)) {
            messages.push({
              role: "user",
              content: message,
            })
          }

          const userMessage: MessageV2.User = {
            id: MessageID.ascending(),
            sessionID,
            agentID: undefined,
            role: "user",
            time: { created: Date.now() },
            agent: agent.name,
            model: {
              providerID: resolvedModel.providerID,
              modelID: resolvedModel.id,
              variant: agent.variant,
            },
            tools: undefined,
            system: undefined,
          }

          const toolResultContinuation = hasToolResults(body)
          const availableTools = normalizeTools(body)
          const hasTools = Object.keys(availableTools).length > 0
          const planningOnly = !toolResultContinuation && isBroadImplementationRequest(message) && !hasExplicitPath(message)
          const blockedToolContinuation =
            !toolResultContinuation && !hasTools && isContinueRequest(message) && hasImplementationHistory(messages)

          const input = {
            user: userMessage,
            sessionID,
            model: resolvedModel,
            agent,
            system: toolResultContinuation
              ? [
                  "The editor has returned tool results. Produce the final answer now using only the current conversation and tool results. Do not request more tools. Do not write XML-like <tool_call> tags.",
                ]
              : planningOnly
                ? [
                    "The user is asking for a Jundot/Godot editor engine feature, but no concrete file path was provided. Do not call tools yet. Reply in Chinese with a short task breakdown: goal, 3-5 implementation steps, likely C++ editor files/modules to inspect such as editor/docks/filesystem_dock.* and editor/ai/*, and the first recommended next step. Do not mention package.json, VS Code, or extension manifests unless the user explicitly asks for them. Do not claim you are inspecting files now.",
                  ]
                : [JUNDOT_WEB_SOURCE_POLICY],
            messages,
            tools: toolResultContinuation || planningOnly || blockedToolContinuation ? {} : availableTools,
          }

          const publishStatus = (
            message: string,
            variant: "info" | "success" | "warning" | "error" = "info",
            duration = 3000,
          ) =>
            busService
              .publish(TuiEvent.ToastShow, {
                title: "Jundot 引擎",
                message,
                variant,
                duration,
              })
              .pipe(Effect.ignore)

          yield* publishStatus(
            planningOnly
              ? "正在拆分引擎任务..."
              : toolResultContinuation
                ? "工具结果已返回，正在生成最终回复..."
                : "正在处理引擎 AI 请求...",
          )

          const collectResponse = (streamInput: typeof input) =>
            Effect.gen(function* () {
              const response: CollectedChatResponse = {
                content: "",
                finishReason: "stop",
                toolCalls: [],
                eventTypes: [],
                errors: [],
              }
              const seenEventTypes = new Set<string>()
              const seenToolCallIds = new Set<string>()

              yield* llmService.stream(streamInput).pipe(
                Stream.tap((event) =>
                  Effect.sync(() => {
                    const item = event as any
                    if (!seenEventTypes.has(item.type)) {
                      seenEventTypes.add(item.type)
                      response.eventTypes.push(item.type)
                    }

                    if (item.type === "text-delta") {
                      response.content += String(item.text ?? item.delta ?? "")
                    }

                    if (item.type === "tool-call" || item.type === "tool-input-available") {
                      addToolCall(response.toolCalls, seenToolCallIds, item.toolCallId ?? item.id, item.toolName, item.input)
                    }

                    if (item.type === "error") {
                      response.errors.push(errorMessage(item.error ?? item.errorText ?? item))
                    }

                    if (item.type === "finish-step" || item.type === "finish") {
                      response.finishReason = item.finishReason ?? response.finishReason
                    }
                  }),
                ),
                Stream.runDrain,
              )

              return response
            })

          let collected: CollectedChatResponse = blockedToolContinuation
            ? {
                content:
                  "当前模式没有可用的 Function Calling 工具，不能继续读取或修改项目代码。请在 AI 设置里启用工具调用后再继续，或改成只让 AI 做文字分析。",
                finishReason: "stop",
                toolCalls: [],
                eventTypes: ["tools-unavailable"],
                errors: [],
              }
            : planningOnly
            ? {
                content: buildPlanningResponse(),
                finishReason: "stop",
                toolCalls: [],
                eventTypes: ["planning"],
                errors: [],
              }
            : yield* collectResponse(input)

          if (!collected.content.trim() && !collected.toolCalls.length && !collected.errors.length) {
            collected = yield* collectResponse({
              ...input,
              messages: [
                ...messages,
                {
                  role: "user",
                  content:
                    "The previous assistant response was empty. Continue the user's request now. If implementation requires more context, use structured tool calls. Otherwise provide a concrete answer. Do not return an empty message.",
                },
              ],
            })
          }

          let content = collected.content
          let finishReason = collected.finishReason
          const toolCalls = collected.toolCalls
          content = stripEchoedTranscript(content)

          if (!toolCalls.length && !planningOnly && !toolResultContinuation) {
            const pseudoToolCalls = parsePseudoToolCalls(content, new Set(Object.keys(availableTools)))
            if (pseudoToolCalls.length && hasTools) {
              toolCalls.push(...pseudoToolCalls)
              content = ""
            } else if (pseudoToolCalls.length) {
              content =
                "AI requested a tool call, but this request did not include any callable tools. Please retry from the editor with Function Calling/tools enabled."
              finishReason = "stop"
            }
          }

          if (planningOnly && parsePseudoToolCalls(content).length) {
            content = buildPlanningResponse()
          }

          if (toolCalls.length) {
            finishReason = "tool_calls"
          }

          if (!content.trim() && !toolCalls.length) {
            content = collected.errors.length
              ? `AI stream error: ${collected.errors.join("\n")}`
              : `AI returned an empty response. Stream events: ${collected.eventTypes.length ? collected.eventTypes.join(", ") : "none"}. Please retry, or provide the specific file/path to inspect.`
            finishReason = "stop"
          }

          if (finishReason === "tool_calls") {
            const toolNames = toolCalls
              .map((item) => (typeof item.function?.name === "string" ? item.function.name : undefined))
              .filter(Boolean)
              .slice(0, 3)
              .join(", ")
            yield* publishStatus(toolNames ? `等待执行工具：${toolNames}` : "等待执行工具...", "info", 5000)
          } else if (collected.errors.length) {
            yield* publishStatus("引擎 AI 请求出错，详情已写入回复。", "error", 6000)
          } else {
            yield* publishStatus("引擎 AI 回复完成。", "success", 2500)
          }

          if (finishReason !== "tool_calls" && content.trim()) yield* Effect.gen(function* () {
            const requestedMirrorSessionID = toExistingSessionID(body.sessionID ?? body.sessionId)
            const mirrorSessionID = yield* Effect.gen(function* () {
              if (requestedMirrorSessionID) {
                const existing = yield* sessionService
                  .get(requestedMirrorSessionID)
                  .pipe(Effect.as(requestedMirrorSessionID), Effect.catch(() => Effect.succeed(undefined)))
                if (existing) return existing
              }

              if (jundotMirrorSessionID) {
                const existing = yield* sessionService
                  .get(jundotMirrorSessionID)
                  .pipe(Effect.as(jundotMirrorSessionID), Effect.catch(() => Effect.succeed(undefined)))
                if (existing) return existing
              }

              const created = yield* sessionService.create({ title: "Jundot AI" })
              jundotMirrorSessionID = created.id
              return created.id
            })

            const now = Date.now()
            const userID = MessageID.ascending()
            const assistantID = MessageID.ascending()
            const mirrorContent = content.trim()

            const mirrorUser: MessageV2.User = {
              id: userID,
              sessionID: mirrorSessionID,
              agentID: undefined,
              role: "user",
              time: { created: now },
              agent: agent.name,
              model: {
                providerID: resolvedModel.providerID,
                modelID: resolvedModel.id,
                variant: agent.variant,
              },
              tools: undefined,
              system: undefined,
            }

            const mirrorAssistant: MessageV2.Assistant = {
              id: assistantID,
              sessionID: mirrorSessionID,
              agentID: undefined,
              role: "assistant",
              parentID: userID,
              time: { created: now, completed: Date.now() },
              modelID: resolvedModel.id,
              providerID: resolvedModel.providerID,
              mode: "build",
              agent: agent.name,
              path: {
                cwd: process.cwd(),
                root: process.cwd(),
              },
              cost: 0,
              tokens: {
                input: 0,
                output: 0,
                reasoning: 0,
                cache: { read: 0, write: 0 },
              },
              variant: agent.variant,
              finish: finishReason,
            }

            yield* sessionService.updateMessage(mirrorUser)
            yield* sessionService.updatePart({
              id: PartID.ascending(),
              sessionID: mirrorSessionID,
              messageID: userID,
              type: "text",
              text: message,
              time: { start: now, end: now },
            })
            yield* sessionService.updateMessage(mirrorAssistant)
            yield* sessionService.updatePart({
              id: PartID.ascending(),
              sessionID: mirrorSessionID,
              messageID: assistantID,
              type: "text",
              text: mirrorContent,
              time: { start: now, end: Date.now() },
            })
            yield* busService.publish(TuiEvent.SessionSelect, { sessionID: mirrorSessionID }).pipe(Effect.ignore)
          }).pipe(Effect.catch(() => Effect.void))

          const assistantMessage: Record<string, unknown> = {
            role: "assistant",
            content,
          }
          if (toolCalls.length) assistantMessage.tool_calls = toolCalls
          const choices = [
            {
              index: 0,
              message: assistantMessage,
              finish_reason: finishReason,
            },
          ]

          return {
            id: `chatcmpl_${sessionID}`,
            object: "chat.completion",
            created: Math.floor(Date.now() / 1000),
            model: `${resolvedModel.providerID}/${resolvedModel.id}`,
            choices,
            content,
            finish_reason: finishReason,
            openai_compatible: { choices },
          }
        }),
      )
      } catch (error) {
        result = buildErrorChatResponse(fallbackSessionID, fallbackModel, error)
      }

      return c.json(result)
    },
  )

  return app
}
