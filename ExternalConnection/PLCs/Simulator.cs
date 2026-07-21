using System.Collections.Concurrent;
using HMI.Models;

namespace HMI.ExternalConnection.PLCs;

internal sealed class Simulator : PLC, IMachineConnection
{
    private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public Simulator() : base("locale", 0)
    {
        _values["Motor.Running"] = false;
        _values["Motor.Speed"] = 1380d;
    }

    public bool IsConnected { get; private set; }
    public event EventHandler<DataChangedEventArgs>? OnDataChanged;
    public event EventHandler? ConnectionLost;

    public Task<bool> ConnectAsync()
    {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<object?> ReadVariableAsync(string variableName)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Simulatore non connesso.");
        }

        if (variableName.Equals("Process.Temperature", StringComparison.OrdinalIgnoreCase))
        {
            var seconds = (DateTime.UtcNow - _startedAt).TotalSeconds;
            _values[variableName] = 64d + Math.Sin(seconds / 3d) * 4.5d;
        }

        return Task.FromResult<object?>(_values.GetOrAdd(variableName, 0d));
    }

    public Task<bool> WriteVariableAsync(string variableName, object value)
    {
        if (!IsConnected)
        {
            return Task.FromResult(false);
        }

        _values[variableName] = value;
        OnDataChanged?.Invoke(this, new DataChangedEventArgs
        {
            VariableName = variableName,
            NewValue = value
        });
        return Task.FromResult(true);
    }

    private void RaiseConnectionLost() => ConnectionLost?.Invoke(this, EventArgs.Empty);
}
