using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace HMI.ServerServices;

public sealed class HmiSignalRServer
{
    private WebApplication? _application;
    private IHubContext<HmiHub>? _hub;

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        if (_application is not null)
        {
            return;
        }
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.AddSignalR();
        _application = builder.Build();
        _application.MapHub<HmiHub>("/hmihub");
        _hub = _application.Services.GetRequiredService<IHubContext<HmiHub>>();
        await _application.StartAsync(cancellationToken);
    }

    public async Task BroadcastAsync(string variableName, object value)
    {
        if (_hub is not null)
        {
            await _hub.Clients.All.SendAsync("RiceviNuovoDato", variableName, value);
        }
    }

    public async Task StopAsync()
    {
        if (_application is null)
        {
            return;
        }
        await _application.StopAsync();
        await _application.DisposeAsync();
        _application = null;
        _hub = null;
    }
}
