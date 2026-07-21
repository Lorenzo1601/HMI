using System.Text.Json;
using HMI.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace HMI.ExternalConnection;

internal sealed class ClientPanelConnection : IMachineConnection
{
    private readonly HubConnection _hubConnection;

    public ClientPanelConnection(string serverHmiIp, int port = 5000)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl($"http://{serverHmiIp}:{port}/hmihub")
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.Closed += _ =>
        {
            IsConnected = false;
            ConnectionLost?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        };
        _hubConnection.Reconnected += _ =>
        {
            IsConnected = true;
            return Task.CompletedTask;
        };
        _hubConnection.On<string, JsonElement>("RiceviNuovoDato", (name, value) =>
        {
            OnDataChanged?.Invoke(this, new DataChangedEventArgs { VariableName = name, NewValue = ConvertJson(value) ?? new object() });
        });
    }

    public bool IsConnected { get; private set; }
    public event EventHandler<DataChangedEventArgs>? OnDataChanged;
    public event EventHandler? ConnectionLost;

    public async Task<bool> ConnectAsync()
    {
        try
        {
            await _hubConnection.StartAsync();
            IsConnected = true;
            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_hubConnection.State != HubConnectionState.Disconnected)
        {
            await _hubConnection.StopAsync();
        }
        IsConnected = false;
    }

    public async Task<object?> ReadVariableAsync(string variableName)
    {
        if (!IsConnected)
        {
            return null;
        }
        var value = await _hubConnection.InvokeAsync<JsonElement>("ReadVariableFromClient", variableName);
        return ConvertJson(value);
    }

    public async Task<bool> WriteVariableAsync(string variableName, object value)
    {
        if (!IsConnected)
        {
            return false;
        }
        return await _hubConnection.InvokeAsync<bool>("WriteVariableFromClient", variableName, value);
    }

    private static object? ConvertJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.ToString()
    };
}
