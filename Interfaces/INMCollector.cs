using System;
using System.Collections.Generic;
using System.Text;

namespace NeteaseMusicCollecter.Interfaces
{
    public interface INMCollector : IAsyncDisposable
    {
        CollectorStatus Status { get; }

        PlaybackState CurrentState { get; }

        Task InitializeAsync(
            CancellationToken cancellationToken = default
        );

        Task StartAsync(
            CancellationToken cancellationToken = default
        );

        Task StopAsync(
            CancellationToken cancellationToken = default
        );

        event EventHandler<SongChangedEventArgs>? SongChanged;

        event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

        event EventHandler<CollectorStatus>? StatusChanged;

        event EventHandler<Exception>? ErrorOccurred;
    }
}
