using System;
using System.Collections.Generic;

namespace Magic.Kernel
{
    public sealed class MemoryContext
    {
        private readonly Stack<HashSet<long>> _localScopeStack = new Stack<HashSet<long>>();

        /// <summary>Global memory (то, что сформировано до entrypoint и наследуется всеми задачами).</summary>
        public Dictionary<long, object> Global { get; set; } = new Dictionary<long, object>();

        /// <summary>Inherited memory (LocalMemory верхнего ExecutionUnit / задачи).</summary>
        public Dictionary<long, object> Inherited { get; set; } = new Dictionary<long, object>();

        /// <summary>Local memory текущей задачи/ExecutionUnit. Индексы те же, что и в Inherited, но слой отдельный.</summary>
        public Dictionary<long, object> Local { get; set; } = new Dictionary<long, object>();

        /// <summary>Push a new local scope (call before entering a call). Cells written to Local in this scope will be cleared on PopLocalScopeAndClear.</summary>
        public void PushLocalScope()
        {
            _localScopeStack.Push(new HashSet<long>());
        }

        /// <summary>Drop all local scopes without clearing cells (e.g. when starting a new run and Local is cleared separately).</summary>
        public void ClearLocalScopes()
        {
            _localScopeStack.Clear();
        }

        /// <summary>Remove from Local all cells written in the current scope and pop the scope. Call on ret from call.</summary>
        public void PopLocalScopeAndClear()
        {
            if (_localScopeStack.Count == 0)
                return;
            var scope = _localScopeStack.Pop();
            foreach (var index in scope)
            {
                if (!Local.TryGetValue(index, out var oldVal))
                    continue;

                if (oldVal is IWeakDisposable weak)
                {
                    try { weak.WeakDispose(); } catch { /* best-effort */ }
                }

                if (oldVal is IDisposable d)
                {
                    try { d.Dispose(); } catch { /* best-effort */ }
                }

                Local.Remove(index);
            }
        }

        public bool TryRead(MemoryAddress memoryCell, bool isExecutingEntryPoint, out object? value)
        {
            value = null;
            if (!memoryCell.Index.HasValue)
                return false;

            var index = memoryCell.Index.Value;

            if (memoryCell.IsGlobal)
            {
                if (Local.TryGetValue(index, out value))
                    return true;

                if (Global.TryGetValue(index, out value))
                    return true;

                return false;
            }

            if (isExecutingEntryPoint)
                return Global.TryGetValue(index, out value);

            if (Local.TryGetValue(index, out value))
                return true;

            if (Inherited.TryGetValue(index, out value))
                return true;

            if (Global.TryGetValue(index, out value))
                return true;

            return false;
        }

        public object Read(MemoryAddress memoryCell, bool isExecutingEntryPoint)
        {
            if (!memoryCell.Index.HasValue)
                throw new InvalidOperationException("Memory address index is not specified.");

            if (!TryRead(memoryCell, isExecutingEntryPoint, out var value))
                throw new InvalidOperationException($"Memory[{memoryCell.Index.Value}] is not set.");

            return value!;
        }

        public void Write(MemoryAddress memoryCell, object? value, bool isExecutingEntryPoint, bool hasRuntime)
        {
            if (!memoryCell.Index.HasValue)
                throw new InvalidOperationException("Memory address index is not specified.");

            var index = memoryCell.Index.Value;
            var writeToLocal = !memoryCell.IsGlobal && !isExecutingEntryPoint && hasRuntime;
            var target = (memoryCell.IsGlobal || isExecutingEntryPoint || !hasRuntime) ? Global : Local;

            if (target.TryGetValue(index, out var oldVal))
            {
                if (oldVal is IWeakDisposable weak)
                {
                    try { weak.WeakDispose(); } catch { /* best-effort */ }
                }

                if (oldVal is IDisposable d)
                {
                    try { d.Dispose(); } catch { /* best-effort */ }
                }
            }

            target[index] = value!;
            if (writeToLocal && _localScopeStack.Count > 0)
                _localScopeStack.Peek().Add(index);
        }
    }
}
