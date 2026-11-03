using System;
using System.Collections.Generic;

namespace NeteaseMusicCollecter
{
    /// <summary>歌手。</summary>
    public sealed record ArtistInfo(string Id, string Name);

    /// <summary>专辑。<see cref="CoverUrl"/> 可能为空。</summary>
    public sealed record AlbumInfo(string Id, string Name, string? CoverUrl);

    /// <summary>
    /// 音质信息。只有"资源加载完成"事件才带这些字段，因此首次切歌时为 <c>null</c>。
    /// </summary>
    /// <param name="Level">音质档位，如 <c>lossless</c>、<c>exhigh</c>、<c>standard</c>。</param>
    /// <param name="Bitrate">码率，单位 bps；未知为 0。</param>
    /// <param name="Format">音频格式，如 <c>flac</c>、<c>mp3</c>。</param>
    public sealed record AudioQualityInfo(string? Level, int Bitrate, string? Format);

    /// <summary>
    /// 一首歌的静态信息。
    /// </summary>
    /// <param name="Id">歌曲 ID（字符串形式）。</param>
    /// <param name="Name">歌名。仅能拿到 songId 的事件里为空串。</param>
    /// <param name="Artists">歌手列表；未知时为空列表。</param>
    /// <param name="Album">专辑；未知时为 <c>null</c>。</param>
    /// <param name="Duration">
    /// 时长；未知为 <see cref="TimeSpan.Zero"/>（真实歌曲不会时长为零，可据此判断未知）。
    /// </param>
    public sealed record SongInfo(
        string Id,
        string Name,
        IReadOnlyList<ArtistInfo> Artists,
        AlbumInfo? Album,
        TimeSpan Duration);

    /// <summary>歌曲变化通知。</summary>
    /// <param name="DetectedAt">
    /// 事件在日志里发生的时刻（客户端写日志的时间），而不是本进程收到它的时刻。
    /// </param>
    public sealed record SongChangedEventArgs(
        SongInfo Song,
        DateTimeOffset DetectedAt);

    /// <summary>播放动作的种类，由解析器根据日志行判定。</summary>
    public enum PlaybackActionKind
    {
        /// <summary>换歌并从指定位置开始播放。</summary>
        TrackStarted,

        /// <summary>拖动进度条到指定位置，播放/暂停状态不变。</summary>
        Seeked,

        /// <summary>开始或继续播放（含"资源加载完成、真正起播"）。</summary>
        Resumed,

        /// <summary>暂停。</summary>
        Paused,

        /// <summary>客户端退出，播放停止。</summary>
        Stopped
    }

    /// <summary>
    /// 一条日志行所表达的播放动作。解析器只负责"报告发生了什么"，
    /// 具体的时间换算（当前位置）由采集器维护。
    /// </summary>
    public sealed record PlaybackAction
    {
        private PlaybackAction(
            PlaybackActionKind kind,
            TimeSpan? position,
            AudioQualityInfo? quality)
        {
            Kind = kind;
            Position = position;
            Quality = quality;
        }

        public PlaybackActionKind Kind { get; }

        /// <summary>仅 <see cref="PlaybackActionKind.TrackStarted"/> 与 <see cref="PlaybackActionKind.Seeked"/> 有意义。</summary>
        public TimeSpan? Position { get; }

        /// <summary>随动作一并得到的音质信息（来自"资源加载完成"事件）。</summary>
        public AudioQualityInfo? Quality { get; }

        public static PlaybackAction TrackStarted(TimeSpan position) =>
            new(PlaybackActionKind.TrackStarted, position, null);

        public static PlaybackAction Seeked(TimeSpan position) =>
            new(PlaybackActionKind.Seeked, position, null);

        public static PlaybackAction Resumed(AudioQualityInfo? quality = null) =>
            new(PlaybackActionKind.Resumed, null, quality);

        public static PlaybackAction Paused() =>
            new(PlaybackActionKind.Paused, null, null);

        public static PlaybackAction Stopped() =>
            new(PlaybackActionKind.Stopped, null, null);
    }

    /// <summary>
    /// 当前播放状态快照。通过 <see cref="Interfaces.INMCollector.CurrentState"/> 读取时，
    /// <see cref="Position"/> 已经按经过的时间推进。
    /// </summary>
    /// <param name="IsPlaying">是否正在播放。</param>
    /// <param name="Position">当前播放位置（已按实际经过时间推进，并夹在时长以内）。</param>
    /// <param name="Duration">歌曲时长；未知为 <see cref="TimeSpan.Zero"/>。</param>
    /// <param name="Quality">当前音频流的音质；未知为 <c>null</c>。</param>
    public sealed record PlaybackState(
        bool IsPlaying,
        TimeSpan Position,
        TimeSpan Duration,
        AudioQualityInfo? Quality)
    {
        /// <summary>没有任何播放信息的初始状态。</summary>
        public static PlaybackState Idle { get; } =
            new(false, TimeSpan.Zero, TimeSpan.Zero, null);
    }

    /// <summary>播放状态变化通知。</summary>
    public sealed record PlaybackStateChangedEventArgs(
        PlaybackState State,
        DateTimeOffset DetectedAt);

    /// <summary>
    /// 一行已解码的日志。<see cref="Timestamp"/> 由行头还原，精确到毫秒；
    /// 不带时间戳的行（部分模块的日志）为 <c>null</c>。
    /// </summary>
    public sealed record LogLine(string Text, DateTimeOffset? Timestamp);

    /// <summary>一行日志的解析结果：可能只是换歌、只是播放动作，或两者都有。</summary>
    public sealed record CollectorUpdate(SongInfo? Song, PlaybackAction? Playback);

    public enum CollectorStatus
    {
        Idle,
        Initializing,
        Running,
        Stopped,
        Error
    }
}
