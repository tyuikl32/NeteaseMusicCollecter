using System;
using System.Collections.Generic;
using System.Text;

namespace NeteaseMusicCollecter.Interfaces
{
    public interface ICollectorSource : IAsyncDisposable
    {
        Task InitializeAsync(
            CancellationToken cancellationToken = default
        );

        /// <param name="onDataReceived">
        /// 每收到一行日志回调一次，携带该行在日志中发生的时间。
        /// </param>
        /// <param name="onError">出现可恢复错误时回调。</param>
        Task StartAsync(
            Func<LogLine, Task> onDataReceived,
            Func<Exception, Task> onError,
            CancellationToken cancellationToken = default
        );

        Task StopAsync(
            CancellationToken cancellationToken = default
        );
    }
}
