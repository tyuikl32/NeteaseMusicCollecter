using NeteaseMusicCollecter.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace NeteaseMusicCollecter
{
    public abstract class BaseNMCollector : INMCollector
    {
        protected abstract ICollectorSource Source { get; }
        protected abstract ICollectorParser Parser { get; }

        private CollectorStatus _status = CollectorStatus.Idle;
        private string? _lastSongId;
        private bool _isPlaying;
        private TimeSpan _anchorPosition;
        private DateTimeOffset _anchorAt;
        private TimeSpan _duration;
        private AudioQualityInfo? _quality;

        public CollectorStatus Status => _status;

        public PlaybackState CurrentState
        {
            get
            {
                TimeSpan position = LivePosition(DateTimeOffset.UtcNow);

                if (_duration > TimeSpan.Zero && position > _duration)
                    position = _duration;

                return new PlaybackState(_isPlaying, position, _duration, _quality);
            }
        }

        public event EventHandler<SongChangedEventArgs>? SongChanged;
        public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;
        public event EventHandler<CollectorStatus>? StatusChanged;
        public event EventHandler<Exception>? ErrorOccurred;

        public async Task InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            SetStatus(CollectorStatus.Initializing);

            try
            {
                await Source.InitializeAsync(cancellationToken);
                SetStatus(CollectorStatus.Idle);
            }
            catch (Exception ex)
            {
                SetStatus(CollectorStatus.Error);
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        public async Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            await Source.StartAsync(
                HandleRawDataAsync,
                HandleErrorAsync,
                cancellationToken
            );

            SetStatus(CollectorStatus.Running);
        }

        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            await Source.StopAsync(cancellationToken);
            SetStatus(CollectorStatus.Stopped);
        }

        private Task HandleRawDataAsync(LogLine line)
        {
            try
            {
                CollectorUpdate? update = Parser.Parse(line);

                if (update is null)
                    return Task.CompletedTask;

                DateTimeOffset at = line.Timestamp ?? DateTimeOffset.UtcNow;

                if (update.Song is { } song && !string.IsNullOrWhiteSpace(song.Id))
                    ApplySong(song, at);

                if (update.Playback is { } action)
                    ApplyPlayback(action, at);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex);
            }

            return Task.CompletedTask;
        }

        private void ApplySong(SongInfo song, DateTimeOffset at)
        {
            if (song.Id == _lastSongId)
                return;

            _lastSongId = song.Id;
            _duration = song.Duration;

            SongChanged?.Invoke(
                this,
                new SongChangedEventArgs(song, at)
            );
        }

        private void ApplyPlayback(PlaybackAction action, DateTimeOffset at)
        {
            switch (action.Kind)
            {
                case PlaybackActionKind.TrackStarted:
                    // 换歌：位置归零，音质等"加载完成"再补。
                    _quality = null;
                    _anchorPosition = action.Position ?? TimeSpan.Zero;
                    _anchorAt = at;
                    _isPlaying = true;
                    break;

                case PlaybackActionKind.Seeked:
                    _anchorPosition = action.Position ?? _anchorPosition;
                    _anchorAt = at;
                    break;

                case PlaybackActionKind.Resumed:
                    // 先把已走过的时长结算进锚点，再换锚点时间，位置才连续。
                    _anchorPosition = LivePosition(at);
                    _anchorAt = at;
                    _isPlaying = true;
                    break;

                case PlaybackActionKind.Paused:
                case PlaybackActionKind.Stopped:
                    _anchorPosition = LivePosition(at);
                    _anchorAt = at;
                    _isPlaying = false;
                    break;
            }

            if (action.Quality is { } quality)
                _quality = quality;

            PlaybackStateChanged?.Invoke(
                this,
                new PlaybackStateChangedEventArgs(CurrentState, at)
            );
        }

        /// <summary>按经过的时间推算 <paramref name="at"/> 时刻的位置。</summary>
        private TimeSpan LivePosition(DateTimeOffset at)
        {
            if (!_isPlaying)
                return _anchorPosition;

            TimeSpan position = _anchorPosition + (at - _anchorAt);

            return position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }

        private Task HandleErrorAsync(Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            return Task.CompletedTask;
        }

        private void SetStatus(CollectorStatus status)
        {
            _status = status;
            StatusChanged?.Invoke(this, status);
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
        }
    }
}
