using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Threading;
using System.Threading.Tasks;

public class PlaywrightInitializerHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly PlaywrightOptions _options;
    private PlaywrightManager? _managerInstance;

    public PlaywrightInitializerHostedService(IServiceProvider services, IOptions<PlaywrightOptions> options)
    {
        _services = services;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Create PlaywrightManager with options and assign to tools
        _managerInstance = await PlaywrightManager.CreateAsync(_options);
        PlaywrightTools.SetManager(_managerInstance);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_managerInstance is not null)
        {
            await _managerInstance.DisposeAsync();
            _managerInstance = null;
        }
    }
}
