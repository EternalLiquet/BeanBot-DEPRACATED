using Microsoft.Extensions.Hosting;

using Serilog;

using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeanBot.Hosting
{
    internal interface IBeanBotApplication
    {
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
    }

    internal sealed class BeanBotHostedService : IHostedService
    {
        private readonly IBeanBotApplication _application;
        private readonly IHostApplicationLifetime _hostLifetime;

        public BeanBotHostedService(
            IBeanBotApplication application,
            IHostApplicationLifetime hostLifetime)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _hostLifetime = hostLifetime ?? throw new ArgumentNullException(nameof(hostLifetime));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _hostLifetime.ApplicationStopping);
                await _application.StartAsync(startupCancellation.Token);
            }
            catch
            {
                try
                {
                    await _application.StopAsync(CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    Log.Error(
                        cleanupException,
                        "BeanBot cleanup failed after application startup did not complete");
                }

                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => _application.StopAsync(cancellationToken);
    }
}
