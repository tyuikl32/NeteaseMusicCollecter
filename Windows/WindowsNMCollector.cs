using NeteaseMusicCollecter.Interfaces;

namespace NeteaseMusicCollecter.Windows
{
    /// <summary>
    /// Windows 上基于客户端日志的网易云音乐播放状态采集器。
    /// <para>
    /// 组合 <see cref="WindowsCollectorSource"/>（跟随 <c>cloudmusic.elog</c>）与
    /// <see cref="WindowsCollectorParser"/>（解析日志行），由 <see cref="BaseNMCollector"/>
    /// 负责按歌曲 ID 去重并抛出 <see cref="INMCollector.SongChanged"/>。
    /// </para>
    /// <para>
    /// 典型用法：
    /// <code>
    /// await using var collector = new WindowsNMCollector();
    ///
    /// collector.SongChanged += (_, e) =>
    ///     Console.WriteLine(
    ///         $"{e.Song.Name} - {string.Join('/', e.Song.Artists.Select(a =&gt; a.Name))}" +
    ///         $"（{e.Song.Album?.Name}，{e.Song.Duration:mm\\:ss}）");
    ///
    /// collector.PlaybackStateChanged += (_, e) =>
    ///     Console.WriteLine(
    ///         $"{(e.State.IsPlaying ? "播放中" : "已暂停")} {e.State.Position:mm\\:ss}" +
    ///         $"/{e.State.Duration:mm\\:ss} [{e.State.Quality?.Level}]");
    ///
    /// await collector.InitializeAsync();
    /// await collector.StartAsync();
    ///
    /// // 进度不必等事件，随时读取即可（按经过时间推进）
    /// Console.WriteLine(collector.CurrentState.Position);
    /// </code>
    /// </para>
    /// <para>
    /// 限制：完全依赖客户端写本地日志，日志文案一旦变化（换版本）规则就会失效；
    /// 客户端未运行时不会产生任何事件，此时 <see cref="StartAsync"/> 只会在冷启动回放中
    /// 发现"最近没有在播放"而保持静默。
    /// </para>
    /// <remarks>
    /// 冷启动补发的第一首歌是在 <see cref="BaseNMCollector.Status"/> 变为
    /// <see cref="CollectorStatus.Running"/> 之前抛出的（<see cref="BaseNMCollector.StartAsync"/>
    /// 要等数据源启动完成才置状态）。因此处理 <see cref="INMCollector.SongChanged"/> 时
    /// 不要以"状态必须是 Running"为前提。
    /// </remarks>
    /// </summary>
    public sealed class WindowsNMCollector : BaseNMCollector
    {
        private readonly WindowsCollectorSource _source;
        private readonly WindowsCollectorParser _parser;

        /// <summary>
        /// 创建采集器。
        /// </summary>
        /// <param name="elogPath">
        /// 日志文件路径；传 <c>null</c> 使用 <c>%LOCALAPPDATA%\NetEase\CloudMusic\cloudmusic.elog</c>。
        /// </param>
        /// <param name="pollInterval">日志轮询间隔，默认 250 ms。</param>
        /// <param name="preRollBytes">
        /// 冷启动时向前扫描的最大字节数，默认 16 MB；传 0 关闭回放。
        /// </param>
        public WindowsNMCollector(
            string? elogPath = null,
            TimeSpan? pollInterval = null,
            int preRollBytes = WindowsCollectorSource.DefaultPreRollBytes)
        {
            _source = new WindowsCollectorSource(elogPath, pollInterval, preRollBytes);
            _parser = new WindowsCollectorParser();
        }

        /// <summary>实际使用的日志文件路径。</summary>
        public string ELogPath => _source.ELogPath;

        /// <inheritdoc />
        protected override ICollectorSource Source => _source;

        /// <inheritdoc />
        protected override ICollectorParser Parser => _parser;
    }
}
