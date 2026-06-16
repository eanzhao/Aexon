# Aexon

[![CI](https://github.com/eanzhao/Aexon/actions/workflows/ci.yml/badge.svg?branch=dev)](https://github.com/eanzhao/Aexon/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Aexon?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Aexon/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Aexon?logo=nuget&label=downloads)](https://www.nuget.org/packages/Aexon/)

> 中文版见 [README_zh.md](README_zh.md).

Aexon is a .NET 10 command-line tool with two responsibilities sitting on the same binary:

1. **A local coding agent.** A C# reimplementation of [Claude Code](https://docs.anthropic.com/en/docs/claude-code) — agentic tool loop, streaming output, resumable sessions, hooks, MCP, subagents — all intact. You can use it as a drop-in Claude Code alternative.
2. **A CLI front-door for the company stack.** A single `aexon` binary that talks to [NyxID](https://github.com/ChronoAIProject/NyxID) (Agent Connectivity Gateway: unified credential + network egress for AI agents), [Aevatar](https://aevatar.ai) (Actor + Event multi-agent collaboration runtime with Workflow YAML orchestration), and Chrono-Storage (Bun + Hono multi-bucket S3 abstraction) — so local dev, scripts, and CI all reach those backends through one tool.

The two lines reinforce each other: the coding agent gives you in-terminal collaboration with the model, while the stack integration turns *log in / call an agent / read and write objects* into short commands like `aexon login`, `aexon aevatar`, `aexon storage`.

## Two directions

### 1. Sharpen the local coding agent

Inherited from upstream Claude Code and continuing to evolve in the .NET ecosystem:

- Interactive REPL with streaming responses, plus a non-interactive `--print` mode
- Built-in tools: Bash / Read / Write / Edit / Glob / Grep / WebFetch / WebSearch
- Subagents, background runs, mailbox, team orchestration
- Hooks and stdio MCP servers loaded from project or user `settings.json`
- Session resume / fork, compaction (`/compact` / `/microcompact` / `/pcompact`), session memory, token and cost tracking
- CLAUDE.md memory layering: `system → user → project` merged, with `.claudeignore` support
- Cross-platform CI on Ubuntu / Windows / macOS with an 80% line-coverage gate
- Shipped on NuGet as the `Aexon` .NET global tool

### 2. Wire up the company stack

| Product | Role | Aexon entry point |
|---------|------|-------------------|
| **NyxID** | Agent Connectivity Gateway: OIDC + API-key auth, Credential Injection Proxy, MCP Tool Wrapping, private-network / localhost reach via a Credential Node | `aexon login` / `aexon logout` / `aexon llm` (same as `/login` etc. in the REPL) |
| **Aevatar** | Multi-agent collaboration runtime on Actor + Event (Orleans transport by default), with Workflow YAML declaring `roles + steps + routes`; Chat exposed over SSE / WebSocket so you can stream the collaboration | `aexon aevatar` subcommand family + `aexon aevatar web` (spins up the Aevatar workflow studio in-process) |
| **Chrono-Storage** | Multi-bucket S3 abstraction (Bun + Hono + AWS SDK v3, MinIO-compatible) exposing bucket / object / presigned URL / batch-delete / cross-bucket copy over HTTP | `aexon storage ls/cat/get/put/put-text/rm` (currently via Aevatar's explorer proxy) |

The three services are independent, but inside aexon they're strung together by a NyxID token: log in once, and `aevatar` / `storage` automatically reuse credentials from `~/.nyxid/` (aexon-specific preferences like the default provider/model live separately at `~/.aexon/preferences.json`). Aexon's Anthropic / OpenAI traffic also flows through NyxID — real API keys are injected server-side, so the local machine never sees a raw key.

## Install

Recommended — install as a global .NET tool from NuGet:

```bash
dotnet tool install --global Aexon
aexon --help
```

Upgrade:

```bash
dotnet tool update --global Aexon
```

Install into a project-local tools folder:

```bash
dotnet tool install --tool-path ./.tools Aexon
./.tools/aexon --help
```

Just want the `.nupkg`:

```bash
nuget install Aexon -Source https://api.nuget.org/v3/index.json -OutputDirectory ./packages
```

Run from source (local development):

```bash
dotnet restore Aexon.slnx
dotnet build Aexon.slnx --configuration Release
dotnet run --project src/Aexon.Cli
```

[scripts/reinstall.sh](scripts/reinstall.sh) repacks the current source and reinstalls it as a global tool — convenient for local iteration.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.100+)
- A [NyxID](https://github.com/ChronoAIProject/NyxID) account — LLM traffic (and, increasingly, the rest of your credentials) flows through the NyxID gateway instead of local `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`. Default instance is `https://nyx-api.chrono-ai.fun`; set `NYXID_BASE_URL` to point at your own.
- Optional: a local Ollama, read from `OLLAMA_HOST` / `OLLAMA_BASE_URL`, defaulting to `http://127.0.0.1:11434`.

## One-time setup

Aexon no longer reads `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, or `appsettings*.json`. All Anthropic / OpenAI requests go through NyxID's `/api/v1/llm/gateway`. Three steps:

1. **Register a provider credential in NyxID.** Open `/keys → Add Service` in the web console, or use the NyxID CLI:

   ```bash
   nyxid service add llm-anthropic --credential-env ANTHROPIC_API_KEY
   nyxid service add llm-openai    --credential-env OPENAI_API_KEY
   ```

2. **Sign in from aexon** (browser OAuth; tokens land in `~/.nyxid/` — same layout as the upstream nyxid CLI, so one login works for both):

   ```bash
   aexon login
   ```

3. **Pick a default provider.** On the very first LLM-facing invocation (interactive REPL or `aexon "<prompt>"`), aexon checks `~/.aexon/preferences.json`; if no default is set it walks you through the picker automatically. The picker pulls from **two** NyxID sources and merges them:

   - **Gateway providers** — `GET /api/v1/llm/status` returns the auto-seeded LLM providers (`anthropic`, `openai`, …). These route through `/api/v1/llm/<slug>/v1/`.
   - **AI Services** — `GET /api/v1/keys` returns every user-scoped AI Service in the NyxID dashboard (Chrono LLM, Mimo, any custom OpenAI-compatible endpoint you added). Each active HTTP service is probed with `GET /api/v1/proxy/s/<slug>/models` (NyxID's proxy handler forwards to `{endpoint_url}/models`, and per NyxID convention the service's configured `endpoint_url` already bakes in `/v1`); services that return an OpenAI-shaped `{ data: [{id}, …] }` (or `{ models: […] }`) body are surfaced as LLM-capable, along with their concrete model list. Services that don't respond OpenAI-style are filtered out silently.

   The merged picker shows gateway entries (indexed `G1`, `G2`, …) and AI Services (indexed `P1`, `P2`, …); you can pick by index or by slug. For AI Services it then renders the probed model list so you can pick a model by number.

   You can also configure it ahead of time:

   ```bash
   aexon llm                         # interactive picker (same flow as first-run)
   aexon llm use anthropic gpt-4o    # gateway provider
   aexon llm use chrono-llm          # an AI Service slug — auto-picks the first probed model
   aexon llm use proxy:mimo qwen3    # `proxy:` prefix is optional; explicit when the slug collides
   ```

   Gateway defaults and AI-Service defaults are mutually exclusive — writing one clears the other. `aexon llm show` prints whichever is active; `aexon llm list` shows both tables.

   In `--print` mode or when stdin is not a TTY, aexon refuses to prompt and exits with actionable guidance instead of hanging.

After that, `aexon "some prompt"` just works — when the active default is an AI Service, Aexon routes chat through `/api/v1/proxy/s/<slug>/chat/completions` (NyxID proxies this to `{endpoint_url}/chat/completions`).

Ollama runs locally and doesn't go through NyxID — invoke it explicitly with `--provider ollama --model <tag>` when you need it.

## Getting started

### As a local coding agent

```bash
# Interactive REPL
aexon

# Non-interactive: run one prompt and exit
aexon "explain this repo"

# Machine-readable single-shot output
aexon --print --output-format json "summarize this repo"

# Pipe content in and deny all permission requests (CI-safe mode)
cat file.py | aexon --print --approval-mode deny "review this code"

# Override working directory and model
aexon --cwd /path/to/project --model opus "summarize this repo"

# One-off provider switch (must be `ready` in NyxID)
aexon --provider openai --model gpt-4o "summarize this repo"

# One-off Ollama run
aexon --provider ollama --model qwen3:4b "summarize this repo"

# Resume the most recent session
aexon --continue
```

### As a stack CLI

NyxID login / identity / LLM:

```bash
aexon login                 # browser OAuth
aexon logout                # revoke refresh token + clear local creds
aexon llm                   # interactive default-provider picker
aexon llm list              # which providers NyxID has and whether they're ready
aexon llm use <p> [model]   # set default non-interactively
aexon llm show / clear
```

Aevatar — chat with the backend and launch the workflow studio:

```bash
aexon aevatar                              # REPL on the current conversation (mainnet by default)
aexon aevatar --endpoint <url>            # use a specific Aevatar endpoint for this invocation only
aexon aevatar "draft a short summary"      # send + stream in the active conversation
aexon aevatar new [title]                  # create a new conversation
aexon aevatar list                         # list conversations in the current scope
aexon aevatar open <id>                    # switch to an existing conversation
aexon aevatar delete [id]                  # delete a conversation (defaults to active)
aexon aevatar config show                  # show/change base URL + scope
aexon aevatar config set-url <url>
aexon aevatar config set-scope <scopeId>
aexon aevatar chat [--endpoint <url>] [--port N] [--no-browser]
aexon aevatar web [--endpoint <url>] [--port N] [--no-browser]   # in-process Aevatar workflow studio with /api/* reverse-proxy
```

Chrono-Storage — read and write objects via Aevatar's explorer proxy:

```bash
aexon storage ls [prefix]              # list files (optionally filtered by prefix)
aexon storage cat <key>                # dump a text file to stdout
aexon storage get <key> [local]        # download to a local path (stdout if omitted)
aexon storage put <key> <local>        # upload a binary (multipart)
aexon storage put-text <key>           # read text from stdin and upload
aexon storage rm <key>
```

These three share the base URL and scope stored in `AevatarChatSettingsStore` — `/aevatar config set-url` also affects `/storage`.

## Hooks and MCP settings

Without an explicit `--settings`, aexon merges `settings.json` from these locations:

- `~/.aexon/settings.json`
- `~/.claude/settings.json`
- `<working directory>/.aexon/settings.json`
- `<working directory>/.claude/settings.json`

MCP currently supports stdio servers and registers their tools dynamically at startup. Built-in tools use a lazy-load strategy: the high-frequency set is always resident, while lower-frequency tools (web search, cron, mailbox, team, tasks, remote triggers, monitoring, managed worktrees) stay deferred until the model calls `ToolSearch` with `select:ToolA,ToolB`.

## Project memory (CLAUDE.md)

Load order is `system → user → project`; later entries override earlier ones. The project layer walks from the repo root down to the cwd, honors `.claudeignore`, and reloads on the next prompt build after files change.

Memory sources:

- `<app base>/CLAUDE.md`
- `<app base>/.claude/CLAUDE.md`
- `<app base>/.claude/rules/*.md`
- `~/.claude/CLAUDE.md`
- `~/.claude/rules/*.md`
- `CLAUDE.md`
- `.claude/CLAUDE.md`
- `.claude/rules/*.md`
- `CLAUDE.local.md`

## Test and coverage

```bash
dotnet restore Aexon.slnx
dotnet build Aexon.slnx --configuration Release --no-restore
dotnet format Aexon.slnx --verify-no-changes --no-restore --severity error
dotnet test Aexon.slnx \
  --configuration Release \
  --no-restore \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=json \
  /p:CoverletOutput=TestResults/coverage-ci/ \
  /p:Threshold=80 \
  /p:ThresholdType=line \
  /p:ThresholdStat=total
```

GitHub Actions runs the same checks on push / PR across Ubuntu, Windows, and macOS; coverage is uploaded as an artifact per OS job.

## Project structure

```text
src/
├── Aexon.Cli/          # Entry, REPL, CLI option parsing, composition root
├── Aexon.Core/         # Query engine, agents, hooks, MCP, permissions, context, storage
│   ├── Auth/           # NyxID login, tokens, credential store
│   ├── Aevatar/        # Aevatar chat client + settings + chrono-storage client
│   └── …               # providers, tools runtime, compaction, memory, etc.
├── Aexon.Tools/        # Built-in tools, web tools, subagent-facing tools
└── Aexon.Commands/     # Slash commands: /aevatar, /storage, /agents, /mailbox, /team, /compact …
```

## CLI options

| Option | Description |
|--------|-------------|
| `--cwd <path>` | Session working directory |
| `--model <name>` | Model name or alias (`sonnet` / `opus` / `haiku`); falls back to stored default |
| `--provider <name>` | `anthropic` / `openai` / `ollama`; falls back to stored default |
| `--resume <session>` | Resume by id / directory / manifest / transcript path |
| `--continue` | Resume the most recently updated session |
| `--fork-session` | Fork the resumed transcript into a brand-new session |
| `--settings <path>` | Load hooks and MCP servers from a specific `settings.json` |
| `--mcp-config` | Alias for `--settings` |
| `--print`, `-p` | Run a single non-interactive prompt and exit |
| `--output-format <text\|markdown\|json>` | Format for `--print` output |
| `--approval-mode <allow\|deny>` | Non-interactive permission policy for `--print` |
| `--max-turns <n>` | Max assistant/tool turns for the run |
| `--help` | Show help |
| `<prompt>` | Initial prompt; omit for interactive mode |

## Slash commands (in the REPL)

**Session & model:** `/help`, `/clear`, `/exit`, `/cost`, `/model`, `/effort`, `/fast`, `/title`, `/tag`, `/session`, `/mode`

**Identity & LLM:** `/login`, `/logout`, `/llm` (also `list / use / show / clear`)

**Compaction & memory:** `/compact`, `/microcompact`, `/pcompact`, `/session-memory`, `/memory`

**Orchestration:** `/agents` (plus `summary / list / wait / tail / prune / stop`), `/mailbox`, `/team` (plus `create / show / dissolve`)

**Stack:** `/aevatar` (chat + `web`), `/storage` (S3-style read/write)

**Engineering flow:** `/diff`, `/review`, `/commit`, `/branch`, `/pr`, `/init`, `/doctor`, `/status`, `/stats`

## Dependencies

| Package | Purpose |
|---------|---------|
| [Anthropic SDK](https://www.nuget.org/packages/Anthropic) 12.9.0 | Claude API client |
| [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI) 10.2.0 | Unified `IChatClient` abstraction, middleware pipeline, structured-output helpers |
| [Microsoft.Extensions.AI.OpenAI](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI) 10.2.0-preview.1.26063.2 | OpenAI `IChatClient` adapter |
| [OpenAI](https://www.nuget.org/packages/OpenAI) 2.8.0 | OpenAI and compatible endpoints |
| [OllamaSharp](https://www.nuget.org/packages/OllamaSharp) 5.4.10 | Ollama `IChatClient` implementation |
| Microsoft.Extensions.FileSystemGlobbing 10.0.0 | Glob-based file discovery |
| [Spectre.Console](https://spectreconsole.net/) 0.54.0 | Terminal UI |
| Microsoft.Extensions.DependencyInjection 10.0.0 | DI container |
| Microsoft.AspNetCore.App 10.0.0 | Kestrel + reverse-proxy behind `aexon aevatar web` |

## Architecture

The `QueryEngine` main loop follows a standard agentic pattern:

1. Assemble the system prompt from environment, tools, memory, and runtime context
2. Hand the conversation to MEAI's `IChatClient`
3. Stream the assistant turn (with a buffered fallback)
4. Execute requested tools with local permission checks
5. Append tool results to the conversation
6. Stop once the model no longer calls tools

On the CLI side, chat clients are registered through `AddChatClient()`, and MEAI middleware adds retries, logging, OpenTelemetry, provider-specific option mapping, and structured-output helpers. All I/O is async (`IAsyncEnumerable<QueryEvent>`) so the REPL streams progressively.

## The three products at a glance

- **NyxID** — Rust-written Agent Connectivity Gateway (`~/Code/NyxID`). Responsibilities: (1) OIDC + API-key auth; (2) Credential Injection Proxy — it custodies third-party API keys (Anthropic / OpenAI / Google / Slack / …) so agents only ever hold a scoped token while the real key is injected server-side; (3) wrapping underlying services as MCP tools; (4) NAT traversal via a Credential Node so agents can reach internal / localhost services. Aexon's `~/.aexon/nyxid.json` shares the token directory with the NyxID CLI's `~/.nyxid/`.
- **Aevatar** — .NET multi-agent collaboration runtime (`~/Code/aevatar`). Kernel: Actor + Event (Orleans by default, swappable to Kafka / MassTransit transport). Orchestration: Workflow YAML — `roles + steps + routes` in a single file, with step types like `llm_call`, `parallel`, `vote_consensus`, `connector_call`, composing sequence / branch / loop / parallel / vote / human approval with zero code. Chat is `POST /api/chat` over SSE / WebSocket so you can stream the collaboration. `aexon aevatar` hits its `/api/scopes/{scope}/chat-history` endpoints.
- **Chrono-Storage** — Bun + Hono + AWS SDK v3 multi-bucket object-storage abstraction (`~/Code/chrono-storage`). It doesn't reimplement S3; it sits in front of any S3-compatible backend (AWS S3, MinIO, …) and exposes a unified HTTP surface: bucket CRUD, object CRUD, batch-delete by prefix, cross-bucket copy, presigned URLs. Default port 3805, `GET /health`, `GET /openapi.json`. `aexon storage` currently routes through Aevatar's explorer proxy.

## License

MIT.
