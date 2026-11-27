# Agent Orchestrator

A Windows desktop harness for designing and running multi-agent workflows visually.
**Agents are boxes, prompts are arrows.** Agents can read/create/edit files, search code, make web requests and run PowerShell.

Built with .NET 8 WPF, [Nodify](https://github.com/miroiu/nodify) and Microsoft.Extensions.AI.

## Build & run

```powershell
dotnet build
dotnet test
dotnet run --project src\AgentOrchestrator.App
```

Open any built-in orchestration from the **Orchestrations** panel (see below).

## Built-in orchestrations
Eight example orchestrations ship with the app. They are the files in `samples\`, embedded in `AgentOrchestrator.Core` and copied to `%APPDATA%\AgentOrchestrator\graphs` on first start (a deleted one is not restored; edits are kept). They use the *GitHub Copilot* provider with its default model; pick a model per agent if you prefer. Right-click one and choose **Show references…** to open its papers.

| Orchestration | Pattern & features shown | Papers |
|---|---|---|
| 1 · Plan → Code ⇄ Review | role-based software team; Handoff, keyword + Negate loop | [MetaGPT](https://arxiv.org/abs/2308.00352), [ChatDev](https://arxiv.org/abs/2307.07924) |
| 2 · Triage Router | controller dispatches via `route_to` (AgentChoice) | [HuggingGPT](https://arxiv.org/abs/2303.17580), [ReAct](https://arxiv.org/abs/2210.03629) |
| 3 · Parallel Specialists | fan-out / fan-in, `WaitForAll` | [Mixture-of-Agents](https://arxiv.org/abs/2406.04692) |
| 4 · Debate with a Judge | adversarial debate, Handoff transcript, early exit | [Multiagent Debate](https://arxiv.org/abs/2305.14325), [AI safety via debate](https://arxiv.org/abs/1805.00899) |
| 5 · Map-Reduce Research | split → parallel researchers (`web_request`) → merge → fact-check loop | [LLM×MapReduce](https://arxiv.org/abs/2410.09342), [STORM](https://arxiv.org/abs/2402.14207) |
| 6 · Writer ⇄ Critic | generate → critique → refine, Regex score gate | [Self-Refine](https://arxiv.org/abs/2303.17651), [CRITIC](https://arxiv.org/abs/2305.11738) |
| 7 · Human-in-the-Loop Operator | `ask_user`, `AlwaysAsk` approvals, independent auditor | [AutoGen](https://arxiv.org/abs/2308.08155), [ReAct](https://arxiv.org/abs/2210.03629) |
| 8 · Test-Driven Loop | tests first, agent-chosen pass/fix/escalate | [AlphaCodium](https://arxiv.org/abs/2401.08500), [CodeT](https://arxiv.org/abs/2207.10397), [Reflexion](https://arxiv.org/abs/2303.11366) |

A graph file may carry a `description` and a `references` list (`title`, `authors`, `url`).

## Concepts

### Agents (boxes)
Select a box to edit it in the right-hand inspector:
- **Provider / model**: GitHub Copilot, Ollama, LM Studio, or any OpenAI-compatible endpoint. The ⟳ button lists the provider's models.
- **System prompt** and optional temperature.
- **Tools**: `read_file`, `create_file`, `edit_file`, `list_directory`, `search_files`, `web_request`, `run_powershell`, `ask_user`.
- **Skills**: markdown instruction files, managed via **Skills…**, appended to the system prompt.
- **Tool approval**: `Auto`, `AskForRisky` (the default) or `AlwaysAsk`. Risky calls are PowerShell, file writes outside the working directory, and non-GET web requests.
- **Join mode**: `Any` runs once per incoming message; `WaitForAll` waits for every upstream branch.

### Arrows (prompts)
Drag from an agent's **out** connector to another agent's **in** connector. Select an arrow to edit:
- **Mode**
  - `Template`: the target starts a fresh conversation built from the prompt template.
  - `Handoff`: the target continues the source's conversation, with the arrow's text appended as added context.
- **Placeholders**:
  - `{{output}}`: the source's final answer
  - `{{input}}`: the source's input
  - `{{task}}`: the original task
  - `{{source}}`: the source agent's name
  - `{{message}}`: the message passed via `route_to`
- **Condition**
  - `Always`: fires on every completion.
  - `Keyword`: the output contains the text (case-insensitive).
  - `Regex`: the output matches the pattern.
  - `AgentChoice`: the agent picks the route with the `route_to` tool.
  - **Negate** inverts the condition.
- **Max traversals**: caps loops (0 = unlimited). The graph also has a **max total steps** limit.

Arrows that point backwards (loops) are drawn dashed.

### Orchestrations (left panel, top)
Lists every orchestration you can use: the graphs in the `graphs` folder, plus any graph file you've opened or saved elsewhere. The one open in the editor is highlighted.
- **Click** an orchestration to open it in the editor. **＋ New** starts an empty graph, and **Open…** adds a graph file from anywhere. Opening an orchestration leaves the current session, so the next **Run** starts a **new session** that appears at the top of the Sessions list.
- **Right-click** an orchestration to open it, start a **new session with it**, show it in Explorer, or **Delete** it (graphs-folder files) / **Remove from list** (files elsewhere, which are not deleted).

### Sessions (left panel)
A session pairs a **working directory** with an **orchestration** (a graph file), and keeps its runs together.
- **＋ New session** opens a ChatGPT-style page: one prompt box for the task, with the session settings underneath. The settings are the **working directory** (native Windows folder picker), the **orchestration** (a saved graph; **…** picks any `.json` graph, and an unsaved current graph is saved first) and the agent it **starts at**. Press **Enter** (or ↑) to create the session and start the run. **Shift+Enter** adds a new line, and **Esc**/**✕ Close** goes back to the editor. The session is named after the first line of the task; rename it with **Edit…**.
- **Selecting a session** loads its graph and replays the transcript of its earlier runs (the most recent 20). If the graph file is gone, the snapshot from its latest run is used.
- **Right-click** a session to edit it, open its folder, or delete it. Deleting a session keeps the run logs, which stay in **Run history…**.
- **Rerun…** (session details, or right-click) runs the session's first task again in the same session, using the current orchestration. Starting it deletes the session's previous runs, their run logs and its transcript, so earlier runs no longer show in **Run history…** or the *"Earlier in this session"* context. The page is prefilled with the first task and entry agent; **Cancel** keeps the history.
- Every run in a session gets an *"Earlier in this session"* section in each agent's system prompt: the previous tasks and final outputs, from up to 5 runs.
- The last active session is reopened on startup; otherwise the new-session page is shown. Running with no session open shows the new-session page.

### Asking the user (`ask_user`)
Agents with the `ask_user` tool enabled (on by default for new agents) can ask you a question mid-run, optionally with suggested answers. A dialog opens and the agent's box turns purple until you answer. You can click a suggestion, type a free-form answer (Ctrl+Enter to send), or **Skip**, which tells the agent to make a reasonable assumption. `ask_user` never needs tool approval. Questions and answers appear in the transcript and run logs.

The built-in **Asking the user** skill (`skills\asking-the-user.md`) is created on first start. It teaches agents when to ask: ambiguous requirements, destructive actions, or information only you have. It also tells them not to ask about things they can look up themselves, and to ask one question at a time with suggested options. Add it to an agent's skills along with the tool. You can edit the file; it is only re-created if you delete it.

### Running
**Run (F5)**: pick the entry agent and the task. Inside a session, the run is added to that session and uses its working directory. Without one (for example, after picking an orchestration), you also choose the working directory, and a new session named after the task is created. The run view shows:
- live streaming output, tool calls and results, and which arrows fired;
- node state colours;
- **Pause/Resume** and **Stop**.

Parallel branches run concurrently, unless that's turned off in the graph settings. Agents whose outgoing arrows don't fire become the run's final outputs.

## Themes
Pick a theme from the **Theme** dropdown at the right end of the toolbar: **Dark** (the default), **Light**, **Midnight** or **Sand**. The change applies immediately and is saved to `settings.json`. Palettes are defined in `src/AgentOrchestrator.App/Services/ThemeManager.cs`, and the control styles that use them are in `Themes/Controls.xaml`. To add a theme, add another `ThemePalette` entry.

## Data locations
Everything lives under `%APPDATA%\AgentOrchestrator`:

| Path | Contents |
|---|---|
| `graphs\*.json` | saved graphs (default Save location) |
| `skills\*.md` | skills (front matter `name` / `description`, then markdown) |
| `runs\{runId}\` | `graph.json` snapshot, `run.json` summary, `events.jsonl` event log (see **Run history…**) |
| `sessions\{id}.json` | sessions: name, working directory, graph path, run ids |
| `settings.json` | providers, theme, last opened graph and session |
| `secrets.json` | API keys / PATs, encrypted with Windows DPAPI (current user) |

## GitHub Copilot
The *GitHub Copilot* provider uses your Copilot subscription through the official
[GitHub Copilot SDK](https://www.nuget.org/packages/GitHub.Copilot.SDK). The SDK's bundled runtime
(`runtimes\<rid>\native\copilot-runtime.exe`, about 160 MB) is downloaded at build time and copied to the output.

1. Sign in once with `gh auth login` (or `copilot login`), or paste a GitHub token with Copilot access in **Providers…**.
2. In **Providers…**, select *GitHub Copilot* and press **Test** to list your Copilot models.
3. Choose a model per agent. The ⟳ button lists the models.

Copilot supplies only the model. The harness's own tools, approval modes and `ask_user` still apply.
Copilot's built-in tools, skills and custom instructions are disabled. Temperature is ignored.
Runtime state is kept in `%APPDATA%\AgentOrchestrator\copilot`.

GitHub Models (`models.github.ai`) was retired on 2026-07-30. Existing GitHub Models providers are marked *(retired)*.

## Project layout
```
src/AgentOrchestrator.Core        models, graph engine, tools, providers, persistence (no UI)
src/AgentOrchestrator.App         WPF + Nodify UI
tests/AgentOrchestrator.Core.Tests  xUnit: engine, tools, stores, OpenAI-compatible streaming via a mock server
```

> ⚠️ File access is unrestricted and agents can run PowerShell. Keep risky tools on `AskForRisky` unless you trust the workflow.
