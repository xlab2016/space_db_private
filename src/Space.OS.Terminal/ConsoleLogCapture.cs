using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Space.OS.Terminal;

public static class ConsoleLogCapture
{
    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly BlockingCollection<string> InputQueue = new(new ConcurrentQueue<string>());
    private static readonly object Sync = new();
    private static bool _installed;

    public static void Install()
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            var originalOut = Console.Out;
            var originalError = Console.Error;
            Console.SetOut(new TeeWriter(originalOut, Enqueue));
            Console.SetError(new TeeWriter(originalError, Enqueue));
            Console.SetIn(new QueueReader(InputQueue));

            if (!HasQueueListener(Trace.Listeners))
                Trace.Listeners.Add(new QueueTraceListener(Enqueue));

            _installed = true;
        }
    }

    private static bool HasQueueListener(TraceListenerCollection listeners)
    {
        foreach (TraceListener l in listeners)
        {
            if (l is QueueTraceListener)
                return true;
        }

        return false;
    }

    public static List<string> Drain()
    {
        var list = new List<string>();
        while (Queue.TryDequeue(out var line))
        {
            list.Add(line);
        }

        return list;
    }

    public static void SubmitInput(string? line)
    {
        InputQueue.Add(line ?? string.Empty);
    }

    private static void Enqueue(string? line)
    {
        if (line == null)
            return;

        Queue.Enqueue(line);
    }

    /// <summary>Пишет в очередь логов (Debug/Trace); полная строка за вызов.</summary>
    private sealed class QueueTraceListener : TraceListener
    {
        private readonly Action<string?> _sink;

        public QueueTraceListener(Action<string?> sink) => _sink = sink;

        public override void Write(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            _sink(message);
        }

        public override void WriteLine(string? message) => _sink(message ?? string.Empty);
    }

    /// <summary>
    /// Потокобезопасный tee: interpreter/runtime пишут с thread-pool, иначе StringBuilder ломается и строки пропадают.
    /// </summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _original;
        private readonly Action<string?> _onLine;
        private readonly StringBuilder _buffer = new();
        private readonly object _bufferLock = new();

        public TeeWriter(TextWriter original, Action<string?> onLine)
        {
            _original = original;
            _onLine = onLine;
        }

        public override Encoding Encoding => _original.Encoding;

        public override void Write(char value)
        {
            _original.Write(value);
            lock (_bufferLock)
            {
                AppendChar(value);
            }
        }

        public override void Write(string? value)
        {
            _original.Write(value);
            lock (_bufferLock)
            {
                AppendString(value);
            }
        }

        public override void WriteLine()
        {
            _original.WriteLine();
            lock (_bufferLock)
            {
                FlushBufferUnlocked();
                _onLine(string.Empty);
            }
        }

        public override void WriteLine(string? value)
        {
            _original.WriteLine(value);
            lock (_bufferLock)
            {
                AppendString(value);
                FlushBufferUnlocked();
            }
        }

        public override void Flush()
        {
            _original.Flush();
            lock (_bufferLock)
            {
                FlushBufferUnlocked();
            }
        }

        private void AppendChar(char value)
        {
            if (value == '\r')
                return;
            if (value == '\n')
            {
                FlushBufferUnlocked();
                return;
            }

            _buffer.Append(value);
        }

        private void AppendString(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            foreach (var ch in value)
            {
                if (ch == '\r')
                    continue;
                if (ch == '\n')
                {
                    FlushBufferUnlocked();
                    continue;
                }

                _buffer.Append(ch);
            }
        }

        private void FlushBufferUnlocked()
        {
            if (_buffer.Length == 0)
                return;

            _onLine(_buffer.ToString());
            _buffer.Clear();
        }
    }

    /// <summary>
    /// Блокирующий reader для Console.ReadLine(): интерпретатор ждёт, пока UI отправит строку в очередь.
    /// </summary>
    private sealed class QueueReader : TextReader
    {
        private readonly BlockingCollection<string> _queue;

        public QueueReader(BlockingCollection<string> queue)
        {
            _queue = queue;
        }

        public override string? ReadLine()
        {
            return _queue.Take();
        }
    }
}
