using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ref.Async
{
    public interface ISource
    {
        Task<byte[]> FetchAsync(string key, CancellationToken token);
    }

    public sealed class CachedSource : ISource, IAsyncDisposable
    {
        private readonly ISource _inner;
        private readonly ConcurrentDictionary<string, byte[]> _cache = new ConcurrentDictionary<string, byte[]>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(4);
        private int _misses;

        public CachedSource(ISource inner) => _inner = inner;

        public int Misses => Volatile.Read(ref _misses);

        public async Task<byte[]> FetchAsync(string key, CancellationToken token)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_cache.TryGetValue(key, out cached)) return cached;
                Interlocked.Increment(ref _misses);
                var data = await _inner.FetchAsync(key, token).ConfigureAwait(false);
                _cache[key] = data;
                return data;
            }
            finally
            {
                _gate.Release();
            }
        }

        // Synchronous fast path without a state machine allocation.
        public ValueTask<int> SizeAsync(string key, CancellationToken token)
        {
            if (_cache.TryGetValue(key, out var cached)) return new ValueTask<int>(cached.Length);
            return SlowSizeAsync(key, token);
        }

        private async ValueTask<int> SlowSizeAsync(string key, CancellationToken token)
        {
            var data = await FetchAsync(key, token).ConfigureAwait(false);
            return data.Length;
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            _gate.Dispose();
            _cache.Clear();
        }
    }

    public static class Batch
    {
        public static async Task<IReadOnlyList<int>> SizesAsync(ISource source, IEnumerable<string> keys, CancellationToken token = default)
        {
            var tasks = new List<Task<byte[]>>();
            foreach (var key in keys) tasks.Add(source.FetchAsync(key, token));
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var sizes = new int[results.Length];
            for (int i = 0; i < results.Length; i++) sizes[i] = results[i].Length;
            return sizes;
        }

        public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> action, int attempts, TimeSpan delay, CancellationToken token)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await action(token).ConfigureAwait(false);
                }
                catch (IOException) when (attempt < attempts)
                {
                    await Task.Delay(delay * attempt, token).ConfigureAwait(false);
                }
            }
        }

        public static async Task CopyAsync(Stream from, Stream to, IProgress<long> progress, CancellationToken token)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await from.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
        }

        public static async void FireAndForget(Task task, Action<Exception> onError)
        {
            try { await task; }
            catch (Exception ex) { onError(ex); }
        }

        public static Task<int> AlreadyDone() => Task.FromResult(42);

        public static async Task<string> WithTimeoutAsync(Task<string> task, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource();
            var winner = await Task.WhenAny(task, Task.Delay(timeout, cts.Token));
            if (winner != task) throw new TimeoutException();
            cts.Cancel();
            return await task;
        }
    }
}
