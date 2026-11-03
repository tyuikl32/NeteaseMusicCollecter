using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NeteaseMusicCollecter.Windows
{
    /// <summary>
    /// <c>cloudmusic.elog</c> 的格式知识：默认位置、逐字节反混淆、事件标记。
    /// <para>
    /// Source 与 Parser 共用本类，使"日志文案"只在同一处出现：客户端一旦改动文案，
    /// 只需修改这里的标记常量。
    /// </para>
    /// </summary>
    internal static class NMElogFormat
    {
        /// <summary>播放相关日志行的统一前缀。</summary>
        public const string PlayingTag = "【playing】";

        /// <summary>
        /// 客户端退出（注意它属于 <c>【app】</c> 而不是 <c>【playing】</c>）。
        /// </summary>
        public const string ExitAppMarker = "【app】,{\"actionId\":\"exitApp\"}";

        /// <summary>
        /// 切歌前检查播放权限：携带完整歌曲 JSON（<c>id</c> / <c>name</c> / <c>artists</c>）。
        /// 这是最可靠的切歌信号，几乎每次起播都会出现。
        /// </summary>
        public const string CheckPlayPrivilegeMarker = "【playing】,\"checkPlayPrivilege\"";

        /// <summary>
        /// 点播播放列表中的某一首：携带完整 <c>track</c> JSON（信息在 <c>track</c> 子对象里）。
        /// </summary>
        public const string PlayOneTrackMarker = "【playing】,\"playOneTrackInPlayingList\"";

        /// <summary>
        /// 音频资源加载完成、开始播放：只携带 <c>songId</c>，没有歌名。
        /// </summary>
        public const string NativeSongLoadedMarker = "【playing】,\"native播放资源load完成，开始播放\"";

        /// <summary>播放状态切换：后接 1（播放）或 2（暂停）。本项目不用于切歌判断。</summary>
        public const string NativePlayStateMarker = "【playing】,\"native播放state\"";

        /// <summary>拖动进度条：后接秒数。</summary>
        public const string SetPlayingPositionMarker = "【playing】,\"setPlayingPosition\"";

        /// <summary>暂停：后接 playId 与 songId。</summary>
        public const string PauseMarker = "【playing】,\"pause\"";

        /// <summary>继续播放：后接 playId 与 songId。</summary>
        public const string ResumeMarker = "【playing】,\"resume\"";

        /// <summary>
        /// 反混淆查找表，索引为原始字节、值为明文字节。
        /// <para>
        /// 原始算法（TypeScript 参考实现的逐字节映射）：
        /// <c>hexDigit = (b/16 ^ (b%16 + 8)) % 16</c>，
        /// <c>out = hexDigit*16 + (b/64)*4 + (~(b/16) &amp; 3)</c>。
        /// </para>
        /// </summary>
        private static readonly byte[] DecodeTable = CreateDecodeTable();

        /// <summary>
        /// 行头：<c>[pid:tid:MMDD/HHMMSS:msSinceBoot:LEVEL:module(line)] [yyyy-MM-dd HH:mm:ss]</c>。
        /// 第 1 组是"开机以来毫秒数"的小数部分来源，第 2 组是精确到秒的墙钟时间。
        /// 两者拼起来就能得到毫秒精度的事件时刻。
        /// </summary>
        private static readonly Regex HeaderPattern = new(
            @"^\[\d+:\d+:\d{4}/\d{6}:(\d+):[A-Z]+:[^\]]*\]\s+\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>解析 <c>%LOCALAPPDATA%\NetEase\CloudMusic\cloudmusic.elog</c>。</summary>
        public static string ResolveDefaultELogPath()
        {
            string localAppData =
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (string.IsNullOrEmpty(localAppData))
                localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;

            return Path.Combine(localAppData, "NetEase", "CloudMusic", "cloudmusic.elog");
        }

        /// <summary>
        /// 原地反混淆一段缓冲。映射逐字节无关顺序，因此读写同一块内存是安全的。
        /// </summary>
        public static void DecodeInPlace(Span<byte> buffer)
        {
            byte[] table = DecodeTable;

            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = table[buffer[i]];
        }

        /// <summary>客户端是否已退出。</summary>
        public static bool IsExit(string line)
        {
            return line.Contains(ExitAppMarker, StringComparison.Ordinal);
        }

        /// <summary>是否携带完整歌曲信息的切歌事件。</summary>
        public static bool IsRichTrackEvent(string line)
        {
            return line.Contains(CheckPlayPrivilegeMarker, StringComparison.Ordinal)
                || line.Contains(PlayOneTrackMarker, StringComparison.Ordinal);
        }

        /// <summary>是否只携带 songId 的播放事件。</summary>
        public static bool IsIdOnlyTrackEvent(string line)
        {
            return line.Contains(NativeSongLoadedMarker, StringComparison.Ordinal)
                || line.Contains(PauseMarker, StringComparison.Ordinal)
                || line.Contains(ResumeMarker, StringComparison.Ordinal);
        }

        /// <summary>是否任何"能确定当前歌曲"的事件。</summary>
        public static bool IsSongIdentityEvent(string line)
        {
            return IsRichTrackEvent(line) || IsIdOnlyTrackEvent(line);
        }

        /// <summary>是否播放状态/进度事件（不含换歌信息）。</summary>
        public static bool IsPlaybackEvent(string line)
        {
            return line.Contains(NativePlayStateMarker, StringComparison.Ordinal)
                || line.Contains(SetPlayingPositionMarker, StringComparison.Ordinal);
        }

        /// <summary>
        /// 还原这一行在客户端写下的时刻，精确到毫秒。
        /// <para>
        /// 日志自带的日期只到秒，而行头里另有一个"开机以来毫秒数"，它的后三位正是当时的
        /// 毫秒小数部分，两者相加即可。实测相邻行的误差 92% 为 0，个别为 ±1 秒
        /// （客户端自身排队写盘所致），但不会像"用本次开机时刻反推"那样在跨启动时错到天级。
        /// </para>
        /// </summary>
        public static bool TryGetTimestamp(string line, out DateTimeOffset timestamp)
        {
            timestamp = default;

            Match match = HeaderPattern.Match(line);

            if (!match.Success)
                return false;

            if (!DateTime.TryParseExact(
                    match.Groups[2].Value,
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out DateTime seconds))
                return false;

            if (!long.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long millisecondsSinceBoot))
                return false;

            timestamp = new DateTimeOffset(seconds)
                .AddMilliseconds(millisecondsSinceBoot % 1000);

            return true;
        }

        /// <summary>
        /// 尽力从一行日志中取出歌曲 ID。完整解析由 Parser 负责，这里只服务于
        /// "冷启动回放"的按 ID 归组，因此取的是各事件里最先出现的那个 ID。
        /// </summary>
        public static bool TryGetSongId(string line, out string songId)
        {
            songId = string.Empty;

            if (IsRichTrackEvent(line))
                return TryReadQuotedValueAfter(line, "\"id\":\"", out songId);

            if (line.Contains(NativeSongLoadedMarker, StringComparison.Ordinal))
                return TryReadQuotedValueAfter(line, "\"songId\":\"", out songId);

            if (line.Contains(PauseMarker, StringComparison.Ordinal)
                || line.Contains(ResumeMarker, StringComparison.Ordinal))
                return TryReadLastQuotedValue(line, out songId);

            return false;
        }

        private static byte[] CreateDecodeTable()
        {
            byte[] table = new byte[256];

            for (int value = 0; value < 256; value++)
            {
                int high = value >> 4;
                int low = value & 0x0F;

                // 与参考实现逐字对应：b/16、b%16、b/64 在该范围内分别等于 high、low、high>>2。
                int hexDigit = (high ^ (low + 8)) % 16;

                table[value] = (byte)((hexDigit * 16) + ((high >> 2) * 4) + (~high & 3));
            }

            return table;
        }

        private static bool TryReadQuotedValueAfter(string line, string prefix, out string value)
        {
            value = string.Empty;

            int start = line.IndexOf(prefix, StringComparison.Ordinal);

            if (start < 0)
                return false;

            start += prefix.Length;
            int end = line.IndexOf('"', start);

            if (end <= start)
                return false;

            return TryAcceptId(line.AsSpan(start, end - start), out value);
        }

        private static bool TryReadLastQuotedValue(string line, out string value)
        {
            value = string.Empty;

            int end = line.LastIndexOf('"');

            if (end <= 0)
                return false;

            int start = line.LastIndexOf('"', end - 1);

            if (start < 0 || start >= end)
                return false;

            return TryAcceptId(line.AsSpan(start + 1, end - start - 1), out value);
        }

        private static bool TryAcceptId(ReadOnlySpan<char> candidate, out string value)
        {
            value = string.Empty;

            if (candidate.IsEmpty || candidate.Length > 32)
                return false;

            foreach (char c in candidate)
            {
                if (!char.IsAsciiDigit(c))
                    return false;
            }

            value = candidate.ToString();
            return true;
        }
    }
}
