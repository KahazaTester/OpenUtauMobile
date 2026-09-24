# Repository invariants

## Git safety
- Start each task by inspecting `git status --short`.
- Preserve all pre-existing uncommitted changes.
- If pending changes overlap files required by the task, or their ownership is
  unclear, stop and notify the user before editing.
- Unrelated pending changes do not block the task; do not modify, stash,
  discard, or clean them.
- Do not maintain any rollback files or scripts. Instead, ask the user to make a backup commit before high risk or unclear changes. Use git to manage rollbacks.
- Place plans documents in `/plans` where git can ignore them. Do not place plans in the source tree or in the `docs` directory.

## Boundaries and style
- Forbid modifications to `OpenUtau.Core/Ustx`, which could break compatibility with universal USTX files.
- Avoid unnecessary changes to upstream-derived `OpenUtau.Core`; keep application orchestration in the Mobile layer. Always ask for permission before modifying upstream-derived code.
- `OpenUtau.Core/Util/Preferences.cs#OpenUtau Mobile特定选项` can be modified. 
- Do not hand-edit upstream-copy `OpenUtau.Plugin.Builtin` for feature work. For upstream synchronization, follow `docs/UPSTREAM_SYNC.md`, including its compatibility-review requirements.
- Follow `.editorconfig`. Write new code comments in Simplified Chinese; preserve upstream comments.

## Verification
- Before `dotnet build`, set `$env:AVALONIA_TELEMETRY_OPTOUT='1';` (or the equivalent environment variable in another shell).
- Do not add new unit tests unless explicitly requested or the task requires them.
- Run existing relevant tests when available and appropriate.
- Do not introduce a new test suite solely for verification.

## Context
- Do not preload repository documentation or historical notes. Read source, docs, and repository-local skills only when relevant to the current task.
- Current source is the primary source of truth; docs are references, not startup context.
- Load repository-local skills when their scope matches the task. Historical migration notes are not active instructions.

## TsnVoice implementation
- TsnVoice 参考实现在本仓库之外（编辑器扩展：托管桥接 + C++20 原生核心 + ONNX Runtime，ABI 版本 8）。不要把它的编辑器 SDK 依赖引入本仓库。
- TsnVoice 按 Vogen 模式实现为一等引擎（经用户明确批准为例外）：引擎本体位于 `OpenUtau.Core/TsnVoice/`（命名空间 `OpenUtau.Core.TsnVoice`），含歌手、加载器、安装器、渲染器、五语言音素器、参数与托管推理管线。允许的 Core 改动仅限三处：`Ustx/USinger.cs` 新增 `USingerType.TsnVoice = 0x8`（只增不改，不影响既有 USTX 兼容）、`Render/Renderers.cs` 内建注册 `TSNVOICE`、`SingerManager.cs` 拼接 `TsnVoiceSingerLoader.FindAllSingers()`。其余 Core/Builtin 文件仍禁止为此修改；UI 编排（文件选择、安装入口、文案）留在 `OpenUtauMobile/`。
- 引擎参数必须与参考实现保持一致：ALP（`alpha`，`-100..100`，默认 `0`）、HUS（`huskiness`，`-1..1`，默认 `0`）、语言 `ja_JP/zh_CN/zh_TW/en_US/ko_KR`、延续音符 `-`、音高采样步长 `5ms`、输出 `48kHz` 单声道（混音前重采样到 `44100Hz`）、支持的 voice format `0/1/2/5`。
- `encryption_mode=1` 需要可选的 libSodium 后端，移动端不支持：遇到时报可复现的明确错误，禁止静默回退、禁止伪造时值或 F0（与上游 DEVELOPMENT.md 一致）。英语 LTS 回退的模型数据移植尚未完成，词典未收录词同样报明确错误。
- 推理统一走应用现有后端 `Onnx.getInferenceSession`（`OnnxRunnerChoice.Default`：CPU 默认，Windows DirectML、macOS CoreML、Linux CUDA、Android NNAPI 可选，算子级回退 CPU）。原生核心固定 CPU，默认配置下行为与原生一致。不新增原生 `.so`，不引入新的 16KB 页面对齐风险；`android-arm64` 为首要验证目标。
- 发音词典从 `DataPath/Dictionaries/TsnVoice` 解析，允许用户覆盖；缺失时音素器必须抛出指引明确的错误，不得静默使用错误 G2P。
- VoiSona 语音包与目录不在上游三目录同步单元内（`OpenUtau.Core/`、`OpenUtau.Plugin.Builtin/`、`native/upstream_cpp/`）；新增第三方来源时同步更新 `THIRD_PARTY_NOTICES.md`。用户须自行确保所用 `.tsnvoice` 已获授权。
