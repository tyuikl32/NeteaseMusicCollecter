using System;
using System.IO;
using System.Text;
using NeteaseMusicCollecter.Interfaces;

namespace NeteaseMusicCollecter.Windows
{
    /// <summary>
    /// 以只读方式跟随网易云音乐的日志文件 <c>cloudmusic.elog</c>，把新增的日志行连同其发生时刻推给上层。
    /// <para>
    /// 工作方式：轮询文件长度 → 只读取新增的那段字节 → 反混淆 → 按行切分 → 逐行回调。
    /// 全程只读，不注入、不联网、不接触客户端进程。
    /// </para>
    /// <para>
    /// 启动时会做一次"冷启动回放"：从文件尾部向前找到当前这首歌的起点，只重放这一首相关的日志行
    /// （换歌、起播、暂停/继续、拖动进度），使暂停状态、播放进度、音质信息都能立刻对上，
    /// 同时不会把历史歌曲当成新事件推给上层。
    /// </para>
    /// </summary>
    public sealed class WindowsCollectorSource : ICollectorSource
    {
        /// <summary>冷启动回放默认向前扫描的字节数（16 MB）。</summary>
        public const int DefaultPreRollBytes = 16 * 1024 * 1024;

        /// <summary>冷启动回放最多重放的日志行数，防止异常日志造成启动时的事件风暴。</summary>
        public const int MaxPreRollLines = 256;

        private const int ReadChunkBytes = 64 * 1024;

        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

        private readonly string _elogPath;
        private readonly TimeSpan _pollInterval;
        private readonly int _preRollBytes;
        private readonly byte[] _readBuffer = new byte[ReadChunkBytes];

        private NMElogLineBuffer? _lineBuffer;
        private CancellationTokenSource? _loopCancellation;
        private Task? _loopTask;
        private long _offset;
        private string? _lastReportedError;
        private bool _initialized;
        private bool _disposed;

        /// <summary>
        /// 创建数据源。
        /// </summary>
        /// <param name="elogPath">
        /// 日志文件路径；传入 <c>null</c> 时使用
        /// <c>%LOCALAPPDATA%\NetEase\CloudMusic\cloudmusic.elog</c>。
        /// </param>
        /// <param name="pollInterval">轮询间隔，默认 250 ms。</param>
        /// <param name="preRollBytes">
        /// 冷启动回放向前扫描的最大字节数，传 0 可关闭回放（此时只有后续新写的日志才会产生事件）。
        /// </param>
        public WindowsCollectorSource(
            string? elogPath = null,
            TimeSpan? pollInterval = null,
            int preRollBytes = DefaultPreRollBytes)
        {
            if (preRollBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(preRollBytes));

            if (pollInterval.HasValue && pollInterval.Value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(pollInterval));

            _elogPath = string.IsNullOrWhiteSpace(elogPath)
                ? NMElogFormat.ResolveDefaultELogPath()
                : elogPath!;

            _pollInterval = pollInterval ?? DefaultPollInterval;
            _preRollBytes = preRollBytes;
        }

        /// <summary>实际使用的日志文件路径。</summary>
        public string ELogPath => _elogPath;

        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!File.Exists(_elogPath))
            {
                throw new FileNotFoundException(
                    $"未找到网易云音乐日志文件，请确认客户端已安装并至少运行过一次：{_elogPath}",
                    _elogPath);
            }

            _initialized = true;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StartAsync(
            Func<LogLine, Task> onDataReceived,
            Func<Exception, Task> onError,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(onDataReceived);
            ArgumentNullException.ThrowIfNull(onError);
            ThrowIfDisposed();

            if (!_initialized)
                throw new InvalidOperationException("请先调用 InitializeAsync。");

            if (_loopTask is not null)
                throw new InvalidOperationException("数据源已经启动，请先调用 StopAsync。");

            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lineBuffer = new NMElogLineBuffer();

            // 先快照长度，再把到该长度为止的内容用于回放：两者之间新写入的字节留给下面的增量读取，
            // 既不遗漏也不重复。
            _offset = GetFileLength();

            IReadOnlyList<LogLine> preRoll = await Task
                .Run(() => CollectPreRollLines(_offset), CancellationToken.None)
                .ConfigureAwait(false);

            foreach (LogLine line in preRoll)
                await onDataReceived(line).ConfigureAwait(false);

            _loopTask = Task.Run(
                () => RunAsync(onDataReceived, onError, _loopCancellation.Token),
                CancellationToken.None);
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokenSource? cancellation = _loopCancellation;
            Task? loop = _loopTask;

            _loopCancellation = null;
            _loopTask = null;

            if (cancellation is null)
                return;

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经释放，忽略。
            }

            try
            {
                if (loop is not null)
                    await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止本身被取消，或循环以取消结束，都属于正常停止。
            }
            finally
            {
                cancellation.Dispose();
                _lineBuffer?.Reset();
                _lineBuffer = null;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            await StopAsync().ConfigureAwait(false);
        }

        private async Task RunAsync(
            Func<LogLine, Task> onDataReceived,
            Func<Exception, Task> onError,
            CancellationToken cancellationToken)
        {
            try
            {
                using PeriodicTimer timer = new PeriodicTimer(_pollInterval);

                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await DrainAsync(onDataReceived, cancellationToken).ConfigureAwait(false);
                        _lastReportedError = null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 客户端写日志时短暂占用文件属于正常现象，报一次后继续轮询。
                        await ReportErrorAsync(onError, ex).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
        }

        private async Task DrainAsync(
            Func<LogLine, Task> onDataReceived,
            CancellationToken cancellationToken)
        {
            long length = GetFileLength();

            if (length < _offset)
            {
                // 日志被清空或重建：从头开始，并丢掉上一个文件残留的半行。
                _offset = 0;
                _lineBuffer!.Reset();
            }

            if (length == _offset)
                return;

            using FileStream stream = OpenRead();
            stream.Seek(_offset, SeekOrigin.Begin);

            while (true)
            {
                int read = await stream
                    .ReadAsync(_readBuffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                    return;

                NMElogFormat.DecodeInPlace(_readBuffer.AsSpan(0, read));
                _lineBuffer!.Append(_readBuffer.AsSpan(0, read));
                _offset += read;

                foreach (string line in _lineBuffer.DrainLines())
                    await onDataReceived(ToLogLine(line)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 从文件尾部向前收集"当前正在播放的这首歌"相关的日志行，按时间先后返回。
        /// <para>
        /// 规则：先找最靠近文件末尾的歌曲事件确定当前歌曲，再向前找出它的完整信息行
        /// （<c>checkPlayPrivilege</c> / <c>playOneTrackInPlayingList</c>）作为起点；
        /// 沿途的暂停/继续/拖动进度都会一起收进来，回放后状态就能对上。
        /// 若先遇到客户端退出，说明当前没有在播放，返回空。
        /// </para>
        /// </summary>
        private IReadOnlyList<LogLine> CollectPreRollLines(long fileLength)
        {
            if (fileLength <= 0 || _preRollBytes <= 0)
                return Array.Empty<LogLine>();

            int windowLength = (int)Math.Min(fileLength, _preRollBytes);
            long windowStart = fileLength - windowLength;

            byte[] decoded = new byte[windowLength];

            try
            {
                using FileStream stream = OpenRead();
                stream.Seek(windowStart, SeekOrigin.Begin);
                stream.ReadExactly(decoded);
            }
            catch (EndOfStreamException)
            {
                // 文件在读取前被截短，本次放弃回放，交给增量轮询自然补齐。
                return Array.Empty<LogLine>();
            }
            catch (IOException)
            {
                return Array.Empty<LogLine>();
            }

            NMElogFormat.DecodeInPlace(decoded);

            List<string> collected = CollectBackwards(decoded, windowStart > 0);

            if (collected.Count == 0)
                return Array.Empty<LogLine>();

            collected.Reverse();

            int skip = Math.Max(0, collected.Count - MaxPreRollLines);

            List<LogLine> lines = new(collected.Count - skip);

            for (int i = skip; i < collected.Count; i++)
                lines.Add(ToLogLine(collected[i]));

            return lines;
        }

        /// <summary>在已解码的字节窗口内自后向前扫描，返回当前歌曲相关的行（新 → 旧）。</summary>
        private static List<string> CollectBackwards(byte[] decoded, bool headIsPartial)
        {
            List<string> collected = new();
            string? targetSongId = null;

            int end = decoded.Length;

            while (end > 0)
            {
                int cursor = end - 1;

                while (cursor >= 0 && decoded[cursor] != (byte)'\n')
                    cursor--;

                int lineStart = cursor + 1;
                int lineLength = end - lineStart;

                // 窗口首行往往是从中途截断的，跳过它，避免从半截 JSON 里误判。
                if (cursor < 0 && headIsPartial)
                    break;

                end = cursor < 0 ? 0 : cursor;

                if (lineLength <= 0)
                    continue;

                string line = Encoding.UTF8.GetString(decoded, lineStart, lineLength).Trim();

                if (line.Length == 0)
                    continue;

                if (NMElogFormat.IsExit(line))
                {
                    // 还没有任何当前歌曲的线索就遇到退出：客户端已经关了，没有在播放。
                    if (targetSongId is null && collected.Count == 0)
                        return new List<string>();

                    // 否则这是更早一次会话的退出，与当前歌曲无关。
                    break;
                }

                if (NMElogFormat.IsSongIdentityEvent(line))
                {
                    if (!NMElogFormat.TryGetSongId(line, out string songId))
                        continue;

                    if (targetSongId is null)
                    {
                        targetSongId = songId;
                    }
                    else if (!string.Equals(songId, targetSongId, StringComparison.Ordinal))
                    {
                        // 已经越过当前歌曲，走到了上一首。
                        break;
                    }

                    collected.Add(line);

                    // 找到这首歌的起点，不必再往前找。
                    if (NMElogFormat.IsRichTrackEvent(line))
                        break;

                    continue;
                }

                if (NMElogFormat.IsPlaybackEvent(line))
                    collected.Add(line);
            }

            return collected;
        }

        private static LogLine ToLogLine(string text)
        {
            return NMElogFormat.TryGetTimestamp(text, out DateTimeOffset timestamp)
                ? new LogLine(text, timestamp)
                : new LogLine(text, null);
        }

        private FileStream OpenRead()
        {
            return new FileStream(
                _elogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadChunkBytes,
                FileOptions.SequentialScan);
        }

        private long GetFileLength()
        {
            try
            {
                return new FileInfo(_elogPath).Length;
            }
            catch (FileNotFoundException)
            {
                // 客户端可能正在重启（先删后建），当作空文件，等它出现后从头读。
                return 0;
            }
            catch (DirectoryNotFoundException)
            {
                return 0;
            }
        }

        private async Task ReportErrorAsync(Func<Exception, Task> onError, Exception exception)
        {
            string message = exception.GetType().Name + ": " + exception.Message;

            if (string.Equals(message, _lastReportedError, StringComparison.Ordinal))
                return;

            _lastReportedError = message;
            await onError(exception).ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WindowsCollectorSource));
        }
    }
}
