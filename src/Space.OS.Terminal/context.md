# Space.OS.Terminal — контекст

WPF: workspace, AvalonEdit `.agi`, AGI debugger, vault, monitor. Refs: `Magic.Kernel`, `Magic.Kernel.Terminal`.

## Debugger UI (`MainWindow.xaml` / `.xaml.cs`)
**Tabs:** `TabControl` — **Code** (`.agi`) и **Asm** (read-only листинг AGIASM из `ExecutableUnit.ToAgiasmText(source)`; справа от инструкций подсказки `//` с исходной строкой AGI). Подсветка Asm: `MagicAgiasmColorizer`; margin брейкпоинтов как у AGI.

**Hotkeys** (code editor, без модификаторов):
- **Code:** F5 Debug, F9 Continue, **F10** — шаг по **строке AGI** (`RequestStepOverLine`), **F11** — заход в вызов: на текущей строке AGI пропускает VM-шаги до `Call`/`ACall`/`CallObj`, затем пауза на первой инструкции callee (`RequestStepInto`).
- **Asm:** F10 и F11 — шаг по **одной инструкции** (`RequestStepInstruction`).
- **Attach (`_attachDebugSession != null`):** **F10 всегда = инструкция** (как на Asm), даже если активен таб Code — иначе при процедурном вызове казалось, что нужно два F10 (первый делал line-step, не совпадающий с ожиданием по ASM).

Toolbar icons; `SetDebugToolbarPaused` — **Stop** enabled while `_activeDebugSession` / `_attachDebugSession` alive (not only on line pause).

**Breakpoints:** AGI (`_agiBreakpoints`) + ASM по номеру строки листинга (`_asmBreakpoints`); `session.ReplaceBreakpointsFrom(agi, asm)`. Событие паузы: `PausedAtDebugLocation(sourceLine, asmListingLine)` (не только `PausedAtLine`).

**Session:** `BeginRun()` before interpret, `EndRun()` in `finally`. API: `RequestContinue`, `RequestStepOverLine`, `RequestStepInstruction`, `RequestStop` (no legacy `Continue`).

**Attach Stop:** only `RequestStop()` — not immediate `Detach` (else `DebugSession` cleared before cancel). Detach runs from `OnInterpreterAttachRegistryUnregistered` when the runtime task exits.

**Memory/Stack:** `DebuggerMemoryRow` / `DebuggerStackRow` + `InspectTreeRoots` → row details `TreeView`; formatting in `InterpreterDebugSnapshot.cs` (`TryBuildInspectRoot`, `BuildInspectTree`, sensitive keys, `ClawStreamDevice` bindings). Vault: `Vault.GetDebugSections()`.

## Kernel (`Magic.Kernel`)
- **Asm listing:** после компиляции `ExecutableUnitAsmListing.StampListingLineNumbers`; у `Command` поле `AsmListingLine` (не в `.agic` JSON). Сериализация: `ExecutableUnitTextSerializer.Serialize(unit, agiSource?)`.
- **Pause:** `MaybeDebugPauseBeforeExecuteAsync` / `MaybeDebugPauseAfterInstructionAsync`; UI получает «текущую» позицию после шага с учётом уже сдвинутого IP (`GetDebugLinesForNextInstruction` и т.п.). При паузе **после** инструкции + `StepInstruction`: one-shot пропуск повторной остановки на том же AGI/ASM breakpoint (`_debugOneShotSkip*`, `_debugPausedAfterInstruction` в `ApplyDebugResumeAction`).
- **ContinueCancellationToken.ThrowIfCancellationRequested** каждую инструкцию при установленной сессии.
- **Await + Stop:** `ExecuteAwait*` uses `WaitAsync(DebugContinueCancellationToken)`.
- **Exit:** `InterpreteAsync` / `InterpreteFromEntryAsync` `finally` → `CloseOwnedDefStreamsAsync` (memory + stack + `ClawStreamDevice.IsHostedByInterpreter`).
- **`StreamDevice.CloseAsync`:** forwards to `Generalizations` (inner claw).
- **`DefStream`:** registry key `long`; `UnregisterFromStreamRegistry()` in `CloseAsync` (claw + stream wrapper). `ClawStreamDevice.CloseAsync` → `CleanupListenerAfterServerEnded()`.

## Monitor (`TerminalMonitorService` + `MainWindow`)
- Streams dedup by **object reference**, not name+type.
- While debug/attach + monitor tab visible: periodic `RefreshMonitor` (~2s via console timer) + on breakpoint pause.
- **Exes:** only records with `!EndedAtUtc` (finished debug/startup rows hidden).

## Paths
| UI / monitor / Asm tab | `Space.OS.Terminal/MainWindow.xaml(.cs)`, `MagicAgiasmColorizer.cs` |
| Inspect | `Magic.Kernel/Interpretation/InterpreterDebugSnapshot.cs`, `Interpreter.DebugSnapshot.cs` |
| Session | `InterpreterDebugSession.cs` |
| Loop / cleanup / debug step | `Interpreter.cs` |
| Asm listing / compile | `ExecutableUnitAsmListing.cs`, `ExecutableUnitTextSerializer.cs`, `ExecutableUnit.cs`, `Compiler.cs`, `Command.cs` |
| Vault | `Magic.Kernel/Devices/Store/Vault.cs` |
| Claw sample | `design/Space/samples/claw/client_claw.agi` |

`dotnet build src/Space.OS.Terminal/Space.OS.Terminal.csproj` — 2026-03-28.
