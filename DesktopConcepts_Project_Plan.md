# DesktopConcepts — Full Project Plan
Client: Kevwe · Status: Pre-implementation planning · Stack: WPF / C# (.NET), reusing NexLink patterns

---

## 0. Product Definition

> A lightweight Windows desktop widget that automatically delivers three high-quality AI-generated technical concepts every day, rotating through them on a 7-minute cycle, staying quietly on the desktop with minimal resource usage, while allowing migration from local AI to cloud AI later without changing the user experience.

Everything below serves this sentence. If a feature doesn't serve it, it's Phase 15 (roadmap), not v1.

**Locked decisions (confirmed with Kevwe):**
- Hybrid AI model: **both local and cloud modes ship in v1 and remain fully functional.** Local (free, offline) is the default. Cloud (API key required) is an optional upgrade. The user picks which mode they want on first run via a setup-time choice screen — this can be changed later from AI Settings.
- WhatsApp is the client comms channel
- No payment/scope has been quoted yet — that comes after the architecture doc, per the client's request

---

## 1. Functional Specification

### 1.1 Core loop
- Generate exactly **three concepts per calendar day**, grouped as a `DailyConceptSet`
- All three concepts share the same category (today's weekday category)
- Each concept is a distinct topic within that category — no duplicates within the set or against history
- Length target per concept: 5–8 sentences
- Technical but beginner-accessible explanation
- No topic repeats across days (tracked against history)
- Category rotates by weekday
- The Rotation Scheduler cycles through the three concepts in order (index 0 → 1 → 2 → 0) every 7 minutes
- History store appends all three concepts when the daily set is generated

### 1.2 Default weekday → category map (editable, not hardcoded)
| Day | Category |
|---|---|
| Monday | Programming |
| Tuesday | Cybersecurity |
| Wednesday | Networking |
| Thursday | AI |
| Friday | Operating Systems |
| Saturday | Mathematics |
| Sunday | Computer Engineering |

### 1.3 Widget states
```
Compact → (click) → Expanded → (outside click / timeout) → Compact
Expanded → (pin) → Pinned → (unpin) → Compact
```
- **Compact**: small always-on-top badge, e.g. "Today's AI"
- **Expanded**: title, explanation, Read More / Copy / Next buttons
- **Pinned**: never auto-collapses
- **Collapsed**: returns to Compact after timeout or a click outside the window

### 1.4 Explicit state machine transitions (no implicit states allowed)
| From | Trigger | To |
|---|---|---|
| Compact | Click | Expanded |
| Expanded | Outside click | Compact |
| Expanded | Pin | Pinned |
| Pinned | Unpin | Compact |
| Expanded | Timeout | Compact |

---

## 2. Non-Functional Requirements

| Metric | Target |
|---|---|
| Startup time | < 300 ms |
| Idle memory | < 120 MB |
| Idle CPU | ~0% |
| CPU during generation | < 15% |
| Battery impact | Not noticeable |
| Internet requirement | Only for cloud mode or update checks — everything else works offline |

These are pass/fail gates for Milestone 6 (Quality), not aspirational.

---

## 3. Architecture — Layered, Clean Separation

```
Presentation  →  Application  →  Domain  →  Infrastructure
```

- **Presentation**: views, animations, theme, controls. Knows nothing about AI.
- **Application**: state manager, timers, commands, navigation, scheduler (business logic).
- **Domain**: pure logic — Concept, Topic, Weekday, Settings, interfaces. No WPF, no HTTP, no JSON, nothing Windows-specific.
- **Infrastructure**: everything external — AI providers, storage, logging, settings, Markdown, networking, filesystem.

### 3.1 Project structure
```
DesktopConcepts
├── DesktopConcepts.UI              (Presentation)
├── DesktopConcepts.Application
├── DesktopConcepts.Domain
├── DesktopConcepts.Infrastructure
│   ├── AI
│   ├── Storage
│   ├── Configuration
│   └── Logging
└── DesktopConcepts.Tests
```

### 3.2 Window behavior (WPF-specific)
- `WS_EX_TOOLWINDOW` to hide from Alt-Tab and taskbar
- `ShowInTaskbar="False"` on the WPF Window
- v1 ships as a standard **always-on-top floating window** — this is the confirmed v1 window model
- "Pinned behind desktop icons" (WorkerW trick) is **explicitly out of v1 scope** — it relies on undocumented Windows internals, behaves inconsistently across Windows 10/11 builds, and breaks whenever Explorer restarts; moved to §13 as an experimental v2 item
- Collapse-on-outside-click via focus-loss events (`Window.Deactivated`), not a global mouse hook (hooks tend to trip antivirus heuristics)

---

## 4. AI Provider Design (the decision that prevents a future rewrite)

**Never call an AI backend directly from application code.**

```
IConceptProvider
    GenerateConceptAsync(category, recentTitlesToAvoid) → Concept
         ├── LocalProvider   (generic OpenAI-compatible local endpoint)
         └── CloudProvider   (stub in v1, wired in later)
```

**Correction to the earlier draft:** don't hardcode against LM Studio specifically. Implement the local provider against a **generic OpenAI-compatible chat completion endpoint** (`POST /v1/chat/completions` style). LM Studio, Ollama's OpenAI-compat mode, and most local inference servers all speak this same shape, so this one implementation covers all of them with no extra work — and nothing else in the app needs to change to swap between them or add cloud later (OpenAI, Anthropic, Gemini, whatever Kevwe eventually wants).

Config just points at a base URL + model name; the provider doesn't care what's actually serving it.

**Default cloud model (when cloud tier is built):** `claude-haiku-4-5` (Anthropic). At the usage volume of this app (a handful of short generations per day per user), cost is not a meaningful constraint — Haiku 4.5 is cheap enough to be effectively free at this scale while delivering a clear quality step up over local inference. This only applies to the optional cloud upgrade path; local mode (free, default) is unchanged.

---

## 5. Scheduler — four separate, single-purpose services

Don't rely on ad-hoc timers scattered through the code.

| Service | Responsibility |
|---|---|
| Daily Scheduler | Local mode: calls `GenerateConceptAsync()` **3 times** per day, persists the full set to History.md |
| Cloud Prefetch Service | Cloud mode only: pre-fetches up to **7 days** of `DailyConceptSets` in a single batch; refills silently when buffer drops below **3 days remaining**; deduplication runs across the whole batch at fetch time |
| Rotation Scheduler | Advances the active concept index (0 → 1 → 2 → 0) every **7 minutes** across the current `DailyConceptSet` — same for both modes |
| Refresh Scheduler | Checks for app updates (24 h cadence) |

**Setup-time choice screen (runs before any other first-run logic):**
On first launch (`AppSettings.IsFirstRun == true`), the widget shows a single screen:
> "How do you want this to work?"
> - (A) Local AI — works fully offline, downloads a model file via local AI server
> - (B) Cloud AI — lightweight, needs internet + your own API key

This sets `AppSettings.Mode` and clears `IsFirstRun` before the existing first-run flows (model download for local, buffer fill for cloud) run. The AI Settings screen lets the user switch modes later — the setup screen is the up-front version of the same choice, not a replacement.

**`DailyConceptSet`** is the container that ties the three daily concepts together:
- Holds an `IReadOnlyList<Concept>` of exactly 3 concepts for a given `DateOnly`
- The Rotation Scheduler's active index is an offset into this list
- All three concepts are appended to History.md when the set is generated or consumed from buffer

**Cloud prefetch buffer (`buffer.json`):**
- Stored at `%AppData%\DesktopConcepts\buffer.json` — completely separate from `History.md`
- Queue semantics: sets are consumed in order; each day pops the next entry
- Refill threshold: when ≤ 3 days remain and internet is available, silent background refill to 7 days
- Exhausted buffer + no internet → existing `GenerationFailed` / error-view behavior
- Deduplication against `History.md` and within the batch runs at prefetch time, not per-day

All schedulers are pausable together when the widget is in Pinned state.

---

## 6. Configuration (`config.json`)

```json
{
  "mode": "local",
  "refresh": "24h",
  "theme": "dark",
  "provider": {
    "baseUrl": "http://localhost:1234/v1",
    "model": "phi-3-mini"
  },
  "topics": {
    "Monday": "Programming",
    "Tuesday": "Cybersecurity",
    "Wednesday": "Networking",
    "Thursday": "AI",
    "Friday": "Operating Systems",
    "Saturday": "Mathematics",
    "Sunday": "Computer Engineering"
  }
}
```
No recompiling needed for topic/theme/provider changes — this is what makes it maintainable for Kevwe and any future non-technical user.

---

## 7. Storage

```
%AppData%\DesktopConcepts
├── Settings.json       (includes Mode and IsFirstRun flag)
├── History.md          (append-only, three entries per day — one per concept in the DailyConceptSet)
├── buffer.json         (cloud mode only — prefetch queue, separate from History.md, consumed daily)
├── last_run.txt        (date of last successful generation, prevents re-running on same calendar day)
├── Logs\
└── Models\             (local mode — downloaded model files)
```
Nothing lives under `Program Files`. Dedupe for v1 works by feeding a rolling list of past concept titles/keywords back into the prompt as "avoid repeating these" — semantic similarity matching is a v2 upgrade, not a blocker.

---

## 8. Logging
- Levels: Information / Warning / Error / Critical
- Daily rolling log files, not one giant file
- Never log full AI prompts/responses at Info level (privacy + noise) — Debug level only, off by default

---

## 9. Error Handling
Standard failure — e.g. local AI endpoint offline — must:
- Never crash
- Never show a raw stack trace to the user
- Show a plain message with actions: **Retry** / **Open AI Settings**

Other failure modes to design for explicitly:
- No internet and cloud mode selected → clear message, fall back to last cached concept if one exists
- First run, no local model downloaded yet → guided first-run flow, not a silent failure
- Config file corrupted/hand-edited badly → validate on load, fall back to defaults rather than crashing

---

## 10. Theme & Animation Systems
- Color tokens, not hardcoded colors: Primary, Secondary, Accent, Background, Surface, Text, Border
- Centralized animation set: Fade, Scale, Slide, Expand, Collapse — all 200–250 ms, same easing curve, so the whole app feels like one thing rather than several

---

## 11. Security
Even fully local and offline, treat all of the following as untrusted input and validate/sanitize:
- Config JSON
- Markdown history file
- AI-generated responses (escape before rendering if HTML rendering is ever added)

---

## 12. Distribution Concerns (this ships publicly, not just to Kevwe)
- **Model delivery: download-on-first-run, not bundled.** Bundling a quantized local model adds several GB to the installer, undermining the "lightweight" core requirement. The installer stays small; the model is fetched on first launch.
- First-run flow **must** include: visible download progress indicator, resumable/retryable download (not a one-shot that leaves a broken state on interruption), and a clear failure UI — not a hang or a crash — if the download cannot complete.
- Graceful degradation if a user's machine can't realistically run local inference (low RAM / no GPU) — detect and suggest cloud mode instead of just being slow or broken.

---

## 13. Extensibility Roadmap (v2+, explicitly out of v1 scope)
Multiple widgets · Favorite concepts · Search history · Markdown export · Voice narration · Daily notifications · Multi-language support · Cloud sync · Plugin system · Community prompt packs

**Experimental v2 — "Pin behind desktop icons" (WorkerW):** inject the widget into the WorkerW layer so concepts appear behind desktop icons. Deferred from v1 because it depends on undocumented Windows internals, breaks on Explorer restart, and behaves inconsistently across Windows 10/11 builds. Track and revisit when/if Microsoft exposes a stable API for this pattern.

Keeping these out of v1 on purpose — they're what "generic local provider" and "clean layers" are buying you the option to add later without a rewrite.

---

## 14. Development Roadmap

**Milestone 1 — Foundation**
Solution structure, projects, dependency injection, configuration, logging

**Milestone 2 — Core Domain**
Models, interfaces, state machine, scheduler

**Milestone 3 — Infrastructure**
Generic local AI provider, settings storage, history storage

**Milestone 4 — UI**
Compact widget, expanded panel, pinning, animations, dark theme

**Milestone 5 — Integration**
Wire provider into scheduler, display generated concepts, history + dedupe

**Milestone 6 — Quality**
Unit tests, performance tuning against the Section 2 targets, installer, auto-update, documentation

---

## 15. Testing Checklist (v1 definition of done)
- [ ] Cold start under 300ms on a mid-range laptop
- [ ] Idle memory/CPU within target over a 1-hour soak
- [ ] Full state machine transition table exercised (all 5 rows in §1.4)
- [ ] Local provider works against at least 2 different OpenAI-compatible servers (proves the generic-endpoint bet)
- [ ] Offline behavior: airplane mode, local mode — still works
- [ ] Offline behavior: airplane mode, cloud mode — fails gracefully with the Retry/Settings message
- [ ] Config hand-edited with a syntax error — app still launches on defaults
- [ ] 30-day simulated run — no topic repeats across days, weekday categories rotate correctly, each day produces exactly 3 distinct concepts
- [ ] Rotation Scheduler advances index 0→1→2→0 every 7 minutes across the DailyConceptSet
- [ ] Fresh machine, no local model downloaded yet — first-run download flow completes without a crash; progress shown; retry works on simulated failure
- [ ] Uninstall leaves no orphaned files outside `%AppData%\DesktopConcepts`
- [ ] **Setup-time choice: selecting Local sets Mode="local" and IsFirstRun=false before any first-run logic runs**
- [ ] **Setup-time choice: selecting Cloud sets Mode="cloud" and IsFirstRun=false before any first-run logic runs**
- [ ] **Both modes work end-to-end: local generates 3 concepts on demand; cloud consumes from prefetch buffer**
- [ ] **7-day cloud prefetch produces exactly 21 unique concepts with no duplicates against history or within the batch**
- [ ] **Buffer refill triggers when remaining count drops below 3 — does not block the UI thread**
- [ ] **Exhausted buffer + no internet falls back to the existing GenerationFailed / error-view behavior, no crash**

---

## 16. Closed Decisions (previously open)
- ~~Bundle the local model in the installer vs. download-on-first-run~~ → **Decided: download-on-first-run** (see §12)
- ~~Exact cloud provider(s) to support~~ → **Decided: Claude Haiku 4.5 as default cloud model** (see §4); interface is generic so adding others later is additive
- ~~Whether "pinned behind desktop icons" is a real requirement~~ → **Decided: out of v1 scope**, moved to §13 as experimental v2 item; v1 is always-on-top floating window
- ~~Drop local mode / cloud only~~ → **Decided: hybrid is confirmed. Both modes ship in v1.** Local is the default (free, offline). Cloud is the optional upgrade path. Setup-time choice screen lets the user pick on first run; AI Settings lets them switch later. Neither mode was removed.
