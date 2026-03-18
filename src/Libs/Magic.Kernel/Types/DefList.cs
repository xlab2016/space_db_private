using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Types
{
    /// <summary>Runtime list type created when AGI code assigns an empty array literal: <c>var x := [];</c>.
    /// The compiler emits <c>push json:[]; push string:list; def; pop slot</c> which resolves here.</summary>
    public class DefList : IDefType
    {
        public long? Index { get; set; }
        public string Name { get; set; } = "";
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        /// <summary>The underlying list of items.</summary>
        public List<object?> Items { get; set; } = new List<object?>();

        public virtual Task<object?> Await() => Task.FromResult<object?>(this);

        public virtual Task<object?> AwaitObjAsync() => Task.FromResult<object?>(this);

        public virtual Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            switch (name.ToLowerInvariant())
            {
                case "add":
                case "push":
                case "append":
                    if (args != null && args.Length > 0)
                        Items.Add(args[0]);
                    return Task.FromResult<object?>(this);

                case "remove":
                case "pop":
                    if (Items.Count > 0)
                    {
                        var last = Items[Items.Count - 1];
                        Items.RemoveAt(Items.Count - 1);
                        return Task.FromResult<object?>(last);
                    }
                    return Task.FromResult<object?>(null);

                case "count":
                case "length":
                case "size":
                    return Task.FromResult<object?>((long)Items.Count);

                case "get":
                    if (args != null && args.Length > 0 && args[0] is long idx)
                        return Task.FromResult<object?>(idx >= 0 && idx < Items.Count ? Items[(int)idx] : null);
                    return Task.FromResult<object?>(null);

                case "clear":
                    Items.Clear();
                    return Task.FromResult<object?>(this);

                default:
                    throw new Interpretation.CallUnknownMethodException(name, this);
            }
        }

        public virtual Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult((true, (object?)null, (object?)null));
    }
}
