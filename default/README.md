# Default documents

The base set the application lays down on an empty data folder: model formats, system prompts, prompt presets, sampler
presets and scenarios, one document per file, in the data folder's own layout (`docs/STORAGE.md`). A file's name is its id — a slug of its
name and eight fixed hex digits, which never change between releases, so a connection naming one keeps working after an
update. `eng/Tests/NE.ChatAI.Tests/DefaultDocumentsTests.cs` reads every file as its type.

## Model formats (`instruct/`)

Turn markers, stop sequences and thought markers were checked against the model's own chat template (`chat_template.jinja`
or `tokenizer_config.json` on Hugging Face) and, where SillyTavern ships one, its instruct template.

| File | Name | For | Checked against |
| --- | --- | --- | --- |
| `gemma-4-d1000001` | Gemma 4 | Gemma 4 and its fine-tunes; thinking on puts `<\|think\|>` at the top of the system turn | `google/gemma-4-31b-it` `chat_template.jinja`, `tokenizer_config.json`; ST "Gemma 4" |
| `gemma-3-d1000003` | Gemma 3 | Gemma 3 (and 2); no system role, the system text goes in a user turn | `google/gemma-3-27b-it` `chat_template.json` (via the `unsloth` mirror); ST "Gemma 2" |
| `qwen-3-thinking-d1000004` | Qwen 3 (thinking) | Qwen 3 hybrid, 3.5, 3.8: the reply opens inside `<think>` | `Qwen/Qwen3-8B` `tokenizer_config.json`, `Qwen/Qwen3.5-27B` and `Qwen/Qwen3.8-27B` `chat_template.jinja` |
| `chatml-d1000005` | ChatML | Qwen 2.5, Qwen 3 Instruct-2507, and the many fine-tunes trained on ChatML | `Qwen/Qwen2.5-7B-Instruct`, `Qwen/Qwen3-30B-A3B-Instruct-2507` `tokenizer_config.json`; ST "ChatML" |
| `llama-3-d1000006` | Llama 3 | Llama 3, 3.1, 3.3 and their fine-tunes | `meta-llama/Llama-3.1-8B-Instruct`, `Llama-3.3-70B-Instruct` `tokenizer_config.json` (via `unsloth`); ST "Llama 3 Instruct" |
| `llama-4-d100000e` | Llama 4 | Llama 4 Scout, Maverick and their fine-tunes | `meta-llama/Llama-4-Scout-17B-16E-Instruct` `chat_template.jinja` (via `unsloth`) |
| `mistral-v7-tekken-d1000007` | Mistral V7-Tekken | Mistral Small 3.x, Ministral 3 Instruct | `mistralai/Mistral-Small-3.1-24B-Instruct-2503` `chat_template.json`, `Ministral-3-8B-Instruct-2512` `chat_template.jinja`; ST "Mistral V7-Tekken" |
| `mistral-v7-tekken-thinking-d100000f` | Mistral V7-Tekken (thinking) | Magistral Small, Ministral 3 Reasoning: thoughts in `[THINK]…[/THINK]` | `mistralai/Magistral-Small-2506` (via `unsloth`), `Ministral-3-8B-Reasoning-2512` `chat_template.jinja` |
| `mistral-v3-tekken-nemo-d1000008` | Mistral V3-Tekken (Nemo) | Mistral Nemo 12B and its fine-tunes | `mistralai/Mistral-Nemo-Instruct-2407` `tokenizer_config.json`; ST "Mistral V3-Tekken" |
| `mistral-v2-v3-7b-mixtral-d1000009` | Mistral V2 & V3 (7B, Mixtral) | Mistral 7B v0.2/v0.3, Mixtral | `mistralai/Mistral-7B-Instruct-v0.3` `tokenizer_config.json`; ST "Mistral V2 & V3" |
| `glm-4-5-4-6-d100000a` | GLM-4.5 / 4.6 | GLM-4.5, 4.5-Air, 4.6 | `zai-org/GLM-4.5-Air`, `zai-org/GLM-4.6` `chat_template.jinja` |
| `deepseek-v3-r1-d1000010` | DeepSeek V3 / R1 | DeepSeek V3, V3.1, R1: the system text stands at the top before `<｜User｜>` | `deepseek-ai/DeepSeek-V3-0324`, `DeepSeek-R1-0528`, `DeepSeek-V3.1` `tokenizer_config.json` |
| `deepseek-r1-distills-d100000b` | DeepSeek R1 (distills) | DeepSeek-R1 distilled Llama/Qwen models: no system role, the reply opens inside `<think>` | `deepseek-ai/DeepSeek-R1-Distill-Llama-8B` `tokenizer_config.json` |
| `command-r-d1000011` | Command R / Command A | Cohere Command R, R+, R7B, Command A | `CohereLabs/c4ai-command-r-08-2024`, `c4ai-command-a-03-2025` `tokenizer_config.json` (via `unsloth`) |
| `command-a-reasoning-d1000012` | Command A (reasoning) | Cohere Command A Reasoning: thoughts in `<\|START_THINKING\|>…<\|END_THINKING\|>` | `CohereLabs/c4ai-command-a-03-2025`, `command-a-reasoning-08-2025` |
| `phi-4-d1000013` | Phi-4 | Microsoft Phi-4, Phi-4-mini: `<\|im_start\|>role<\|im_sep\|>…<\|im_end\|>` | `microsoft/phi-4` `tokenizer_config.json` |
| `phi-4-reasoning-d1000014` | Phi-4 (reasoning) | Microsoft Phi-4-reasoning: the reply opens inside `<think>` | `microsoft/Phi-4-reasoning` `tokenizer_config.json` |
| `phi-3-d1000015` | Phi-3 | Microsoft Phi-3, Phi-3.5 | `microsoft/Phi-3.5-mini-instruct` `tokenizer_config.json` |
| `alpaca-d100000c` | Alpaca | older Alpaca-format fine-tunes | the Stanford Alpaca prompt; ST "Alpaca" |
| `metharme-pygmalion-d100000d` | Metharme (Pygmalion) | Pygmalion-2, Metharme and their merges | `PygmalionAI/pygmalion-2-13b` README; ST "Metharme" |

Where a template deviates from the official one because the format cannot say it:

- **Qwen 3, DeepSeek R1** — with thinking off the reply opens with `<think>\n</think>`; Qwen's template writes
  `<think>\n\n</think>\n\n`.
- **GLM-4.5 / 4.6** — the prompt should open with `[gMASK]<sop>`, which no field can put at the very start; earlier replies
  go back without the empty `<think></think>` the template gives them.
- **Gemma 3, Mistral V3-Tekken, V2 & V3, DeepSeek R1** — no system role: the story string is a user turn of its own rather
  than the head of the first one.

## System prompts (`sysprompts/`)

| File | Name | What it is |
| --- | --- | --- |
| `roleplay-d2000001` | Roleplay | the immersive default: {{char}} in an open-ended roleplay, never acting for {{user}} |
| `narrator-d2000002` | Narrator | a literary narrator: third person, past tense, every character and the world |
| `game-master-d2000003` | Game master | a text adventure: second person, consequences, the next move left to {{user}} |
| `assistant-d2000004` | Assistant | a plain helpful assistant |

## Prompt presets (`prompts/`)

| File | Name | What it is |
| --- | --- | --- |
| `roleplay-d3000001` | Roleplay | SillyTavern's default story string: system, world info, description, personality, scenario, persona; `***` before the examples and the chat |
| `sectioned-d3000002` | Sectioned | the same under Markdown headings, the examples inside it — clearer to a modern instruct model |

Both show a reply's thought folded over it and leave earlier thoughts out of the prompt.

## Sampler presets (`samplers/`)

All but Deterministic keep one seed per chat; a reply may be up to 1024 tokens, room for a thought before it.

| File | Name | Based on |
| --- | --- | --- |
| `balanced-d4000001` | Balanced | temperature 1, Min-P 0.05, DRY 0.8, a light repetition penalty — a safe start for current models |
| `creative-d4000002` | Creative | temperature 1.1, Min-P 0.03, XTC 0.1/0.5 and DRY 0.8, as XTC's and DRY's author suggests |
| `precise-d4000003` | Precise | temperature 0.6, Top-P 0.95, Top-K 40: steady, for small models or rules-heavy cards |
| `min-p-d4000004` | Min-P | SillyTavern's Universal-Light: temperature 1.25 applied first, Min-P 0.1, nothing else |
| `deterministic-d4000005` | Deterministic | SillyTavern's Deterministic: greedy (Top-K 1), a fixed seed — for comparing prompts |

## Scenarios (`graphs/`)

| File | Name | Default | Trigger | What it does |
| --- | --- | --- | --- | --- |
| `summary-d5000001` | Summary | On | Context past 50% | Replaces the middle messages (skipping the first 2 and the last 4) with a concise past-tense summary (`Precise`, thinking off, reply length 2048, temperature 0.65) under tag `summary` |
| `scene-report-d5000002` | Scene report | Off | After a reply | Updates a three-line `Clothing` / `Pose/location` / `Mood` note from the previous `scene` record and the latest three raw messages (`Precise`, thinking off, reply length 256, temperature 0.65), inserted under tag `scene` |
| `impersonate-d5000003` | Impersonate | System | By hand | Writes `{{user}}`'s next message into the composer draft from the character, persona, world and history (`Thinking: Off`) |
