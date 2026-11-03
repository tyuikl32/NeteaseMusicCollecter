using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using NeteaseMusicCollecter.Interfaces;

namespace NeteaseMusicCollecter.Windows
{
    /// <summary>
    /// 解析 <c>cloudmusic.elog</c> 中一行已解码的日志文本。
    /// <para>
    /// 产出分两部分：能确定歌曲时给出 <see cref="SongInfo"/>，能确定播放动作时给出
    /// <see cref="PlaybackAction"/>。位置的具体换算不在这里做（那是采集器的责任），
    /// 解析器只回答"这一行说明发生了什么"。
    /// </para>
    /// <para>
    /// 各类事件能提供的信息并不相同：
    /// <list type="bullet">
    /// <item><c>checkPlayPrivilege</c> / <c>playOneTrackInPlayingList</c>：完整歌曲信息
    /// （歌名、歌手、专辑、封面、时长），但还没有音质；</item>
    /// <item><c>native播放资源load完成</c>：只有 songId、时长和音质（码率/档位/格式），
    /// 因此歌名歌手为空，音质也只在此时才补上；</item>
    /// <item><c>pause</c> / <c>resume</c>：只有 songId；</item>
    /// <item><c>native播放state</c>：只表示播放/暂停，不含歌曲；</item>
    /// <item><c>setPlayingPosition</c>：只表示拖动到的位置。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class WindowsCollectorParser : ICollectorParser
    {
        /// <inheritdoc />
        public CollectorUpdate? Parse(LogLine line)
        {
            string? text = line.Text;

            if (string.IsNullOrWhiteSpace(text))
                return null;

            text = text.Trim();

            // 客户端退出：不再有播放。
            if (text.Contains(NMElogFormat.ExitAppMarker, StringComparison.Ordinal))
                return new CollectorUpdate(null, PlaybackAction.Stopped());

            // 快速排除：网络/缓存/崩溃上报等模块的日志一定不含该前缀。
            if (!text.Contains(NMElogFormat.PlayingTag, StringComparison.Ordinal))
                return null;

            if (text.Contains(NMElogFormat.CheckPlayPrivilegeMarker, StringComparison.Ordinal))
                return ParseSongWithJson(text, songIsNested: false);

            if (text.Contains(NMElogFormat.PlayOneTrackMarker, StringComparison.Ordinal))
                return ParseSongWithJson(text, songIsNested: true);

            if (text.Contains(NMElogFormat.NativeSongLoadedMarker, StringComparison.Ordinal))
                return ParseLoadedTrack(text);

            if (text.Contains(NMElogFormat.PauseMarker, StringComparison.Ordinal)
                || text.Contains(NMElogFormat.ResumeMarker, StringComparison.Ordinal))
                return ParseIdOnlySong(text);

            if (text.Contains(NMElogFormat.NativePlayStateMarker, StringComparison.Ordinal))
                return ParsePlayState(text);

            if (text.Contains(NMElogFormat.SetPlayingPositionMarker, StringComparison.Ordinal))
                return ParseSeek(text);

            return null;
        }

        /// <summary>
        /// 带完整歌曲 JSON 的换歌事件。<paramref name="songIsNested"/> 为 true 时
        /// （<c>playOneTrackInPlayingList</c>）歌曲信息在 <c>track</c> 子对象里。
        /// </summary>
        private static CollectorUpdate? ParseSongWithJson(string text, bool songIsNested)
        {
            JsonDocument? document = TryParseJson(text);

            if (document is not null)
            {
                using (document)
                {
                    JsonElement song = document.RootElement;

                    if (songIsNested)
                    {
                        if (song.ValueKind != JsonValueKind.Object
                            || !song.TryGetProperty("track", out JsonElement track)
                            || track.ValueKind != JsonValueKind.Object)
                            return FallbackToId(text, withPlayback: true);

                        song = track;
                    }

                    string? id = ReadString(song, "id");

                    if (!string.IsNullOrEmpty(id))
                    {
                        return new CollectorUpdate(
                            new SongInfo(
                                id,
                                ReadString(song, "name") ?? string.Empty,
                                ReadArtists(song),
                                ReadAlbum(song),
                                ReadDuration(song, "duration")),
                            PlaybackAction.TrackStarted(TimeSpan.Zero));
                    }
                }
            }

            return FallbackToId(text, withPlayback: true);
        }

        /// <summary>
        /// "资源加载完成、开始播放"：补上时长与音质，并视为开始播放。
        /// </summary>
        private static CollectorUpdate? ParseLoadedTrack(string text)
        {
            JsonDocument? document = TryParseJson(text);

            if (document is not null)
            {
                using (document)
                {
                    JsonElement payload = document.RootElement;
                    string? id = ReadString(payload, "songId") ?? ReadString(payload, "id");

                    if (!string.IsNullOrEmpty(id))
                    {
                        SongInfo song = new(
                            id,
                            string.Empty,
                            Array.Empty<ArtistInfo>(),
                            null,
                            ReadDuration(payload, "songDuration"));

                        return new CollectorUpdate(song, PlaybackAction.Resumed(ReadQuality(payload)));
                    }
                }
            }

            return FallbackToId(text, withPlayback: false);
        }

        /// <summary>只有 songId 的事件（<c>pause</c> / <c>resume</c>）。</summary>
        private static CollectorUpdate? ParseIdOnlySong(string text)
        {
            return FallbackToId(text, withPlayback: false);
        }

        /// <summary><c>native播放state</c>：1=播放，2=暂停。</summary>
        private static CollectorUpdate? ParsePlayState(string text)
        {
            ReadOnlySpan<char> argument = ReadArgument(text, NMElogFormat.NativePlayStateMarker);

            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int state))
                return null;

            return state switch
            {
                1 => new CollectorUpdate(null, PlaybackAction.Resumed()),
                2 => new CollectorUpdate(null, PlaybackAction.Paused()),
                _ => null
            };
        }

        /// <summary><c>setPlayingPosition</c>：拖动进度条，后接秒数。</summary>
        private static CollectorUpdate? ParseSeek(string text)
        {
            ReadOnlySpan<char> argument = ReadArgument(text, NMElogFormat.SetPlayingPositionMarker);

            if (!double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                return null;

            if (double.IsNaN(seconds) || seconds < 0 || seconds > 86400)
                return null;

            return new CollectorUpdate(null, PlaybackAction.Seeked(TimeSpan.FromSeconds(seconds)));
        }

        /// <summary>JSON 不可用时退化到只有 songId 的结果，至少保证事件不丢。</summary>
        private static CollectorUpdate? FallbackToId(string text, bool withPlayback)
        {
            if (!NMElogFormat.TryGetSongId(text, out string songId))
                return null;

            SongInfo song = new(
                songId,
                string.Empty,
                Array.Empty<ArtistInfo>(),
                null,
                TimeSpan.Zero);

            return new CollectorUpdate(
                song,
                withPlayback ? PlaybackAction.TrackStarted(TimeSpan.Zero) : null);
        }

        private static JsonDocument? TryParseJson(string text)
        {
            int open = text.IndexOf('{');

            if (open < 0)
                return null;

            int close = text.LastIndexOf('}');

            if (close <= open)
                return null;

            try
            {
                return JsonDocument.Parse(text.Substring(open, close - open + 1));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>取事件标记后面的第一个参数（形如 <c>,2,"playId"</c> 中的 <c>2</c>）。</summary>
        private static ReadOnlySpan<char> ReadArgument(string text, string marker)
        {
            int start = text.IndexOf(marker, StringComparison.Ordinal);

            if (start < 0)
                return ReadOnlySpan<char>.Empty;

            start += marker.Length;

            if (start >= text.Length || text[start] != ',')
                return ReadOnlySpan<char>.Empty;

            start++;
            int end = start;

            while (end < text.Length && text[end] != ',')
                end++;

            return text.AsSpan(start, end - start);
        }

        private static IReadOnlyList<ArtistInfo> ReadArtists(JsonElement song)
        {
            if (!song.TryGetProperty("artists", out JsonElement artists)
                || artists.ValueKind != JsonValueKind.Array)
                return Array.Empty<ArtistInfo>();

            List<ArtistInfo> names = new(artists.GetArrayLength());

            foreach (JsonElement artist in artists.EnumerateArray())
            {
                string? name = ReadString(artist, "name");

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                names.Add(new ArtistInfo(ReadString(artist, "id") ?? string.Empty, name));
            }

            return names;
        }

        private static AlbumInfo? ReadAlbum(JsonElement song)
        {
            if (!song.TryGetProperty("album", out JsonElement album)
                || album.ValueKind != JsonValueKind.Object)
                return null;

            string? cover = ReadString(album, "cover") ?? ReadString(album, "picUrl");

            return new AlbumInfo(
                ReadString(album, "id") ?? string.Empty,
                ReadString(album, "name") ?? string.Empty,
                string.IsNullOrWhiteSpace(cover) ? null : cover);
        }

        private static AudioQualityInfo? ReadQuality(JsonElement payload)
        {
            string? level = ReadString(payload, "level");
            string? format = ReadString(payload, "audioFormat");
            int bitrate = ReadInt32(payload, "bitrate");

            if (string.IsNullOrEmpty(level) && string.IsNullOrEmpty(format) && bitrate <= 0)
                return null;

            return new AudioQualityInfo(level, bitrate, format);
        }

        /// <summary>时长字段在换歌事件里是数字（毫秒），在加载完成事件里是字符串。</summary>
        private static TimeSpan ReadDuration(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return TimeSpan.Zero;

            double milliseconds;

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (!value.TryGetDouble(out milliseconds))
                    return TimeSpan.Zero;
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                if (!double.TryParse(
                        value.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out milliseconds))
                    return TimeSpan.Zero;
            }
            else
            {
                return TimeSpan.Zero;
            }

            return milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;
        }

        private static int ReadInt32(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return 0;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;

            return 0;
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(propertyName, out JsonElement value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }
    }
}
