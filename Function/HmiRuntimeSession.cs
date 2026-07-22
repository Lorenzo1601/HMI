using System.Collections.Concurrent;
using System.Globalization;
using HMI.ExternalConnection;
using HMI.ExternalConnection.PLCs;
using HMI.Models;
using HMI.ServerServices;
using S7.Net;

namespace HMI.Function;

public sealed class HmiRuntimeSession
{
    private readonly ConcurrentDictionary<string, object> _values = new();
    private readonly Dictionary<string, DateTime> _lastPoll = [];
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private CancellationTokenSource? _runtimeCancellation;
    private IMachineConnection? _connection;
    private HmiSignalRServer? _signalRServer;
    private HmiProject? _project;
    private MySqlTagLogger? _databaseLogger;
    private RedundantPanelDefinition? _localPanel;
    private bool _usingDirectPlcConnection;
    private bool _stoppingOrSwitching;

    public bool IsConnected => _connection?.IsConnected == true;
    public event EventHandler<TagValueChangedEventArgs>? TagValueChanged;
    public event EventHandler<RedundancyStateChangedEventArgs>? RedundancyStateChanged;

    public async Task<bool> StartAsync(HmiProject project)
    {
        await StopAsync();
        _project = project;
        _runtimeCancellation = new CancellationTokenSource();

        if (project.Redundancy.Enabled && project.Redundancy.Panels.Count > 0)
        {
            _localPanel = project.Redundancy.Panels.FirstOrDefault(panel => panel.IsLocal)
                ?? project.Redundancy.Panels.OrderBy(panel => panel.Priority).First();
            var superiorConnection = await FindSuperiorPanelAsync(_localPanel, _runtimeCancellation.Token);
            if (superiorConnection is not null)
            {
                await UseConnectedConnectionAsync(superiorConnection, false);
                RaiseRedundancyState(RedundancyRuntimeState.Standby,
                    $"Runtime ridondante · standby su pannello superiore · priorità {_localPanel.Priority}");
            }
            else
            {
                await PromoteToActiveAsync(false, _runtimeCancellation.Token);
            }
            _ = RedundancyMonitorLoopAsync(_runtimeCancellation.Token);
        }
        else
        {
            var direct = BuildDirectConnection(project);
            var connected = await direct.ConnectAsync();
            await UseConnectedConnectionAsync(direct, true);
            RaiseRedundancyState(connected ? RedundancyRuntimeState.Active : RedundancyRuntimeState.Offline,
                connected ? "Runtime attivo · PLC collegati" : "Runtime attivo · uno o più PLC non raggiungibili");
        }

        _databaseLogger = new MySqlTagLogger(project);
        if (!await _databaseLogger.StartAsync())
        {
            await _databaseLogger.DisposeAsync();
            _databaseLogger = null;
        }
        _ = PollLoopAsync(_runtimeCancellation.Token);
        return IsConnected;
    }

    public async Task StopAsync()
    {
        _stoppingOrSwitching = true;
        if (_databaseLogger is not null)
        {
            await _databaseLogger.DisposeAsync();
            _databaseLogger = null;
        }
        if (_runtimeCancellation is not null)
        {
            await _runtimeCancellation.CancelAsync();
            _runtimeCancellation.Dispose();
            _runtimeCancellation = null;
        }

        await _connectionLock.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                _connection.OnDataChanged -= Connection_OnDataChanged;
                _connection.ConnectionLost -= Connection_ConnectionLost;
                await _connection.DisconnectAsync();
                _connection = null;
            }
            if (_signalRServer is not null)
            {
                await _signalRServer.StopAsync();
                _signalRServer = null;
            }
            App.Connection = null;
        }
        finally
        {
            _connectionLock.Release();
            _stoppingOrSwitching = false;
        }

        _project = null;
        _localPanel = null;
        _usingDirectPlcConnection = false;
        _lastPoll.Clear();
        _values.Clear();
    }

    public object? GetValue(string tagId) => _values.TryGetValue(tagId, out var value) ? value : null;

    public async Task<bool> WriteAsync(string tagId, string rawValue)
    {
        var project = _project;
        var connection = _connection;
        if (project is null || connection is null)
        {
            return false;
        }
        var tag = project.Tags.FirstOrDefault(item => item.Id == tagId);
        var plc = tag is null ? null : project.PlcConnections.FirstOrDefault(item => item.Id == tag.PlcId);
        if (tag is null || plc is null || tag.Access == TagAccess.Read)
        {
            return false;
        }
        var converted = ConvertValue(rawValue, tag.DataType);
        var success = await connection.WriteVariableAsync($"{plc.Name}.{tag.Address}", converted);
        if (success)
        {
            PublishValue(tag.Id, converted);
        }
        return success;
    }

    private MultiPlcConnection BuildDirectConnection(HmiProject project)
    {
        var multi = new MultiPlcConnection();

        foreach (var definition in project.PlcConnections)
        {
            IMachineConnection connection = definition.Driver switch
            {
                PlcDriver.Simulator => new Simulator(),

                PlcDriver.Codesys => new Codesys(
                    definition.Host,
                    definition.Port),

                PlcDriver.OpcUa => new OpcUaConnection(
                    new OpcUaConfig
                    {
                        ServerUrl = definition.OpcUaServerUrl,
                        UseAnonymous = definition.OpcUaUseAnonymous,
                        Username = definition.OpcUaUsername,
                        Password = definition.OpcUaPassword,
                        AutoAcceptUntrustedCertificates = definition.OpcUaAutoAcceptCertificates
                    }),

                _ => new Siemens(
                    definition.Host,
                    definition.Port,
                    Enum.TryParse<CpuType>(definition.CpuType, true, out var cpuType)
                        ? cpuType
                        : CpuType.S71500,
                    definition.Rack,
                    definition.Slot)
            };

            multi.AddPlc(definition.Name, connection);
        }

        return multi;
    }

    private async Task<ClientPanelConnection?> FindSuperiorPanelAsync(RedundantPanelDefinition localPanel, CancellationToken cancellationToken)
    {
        if (_project is null)
        {
            return null;
        }
        foreach (var panel in _project.Redundancy.Panels.Where(panel => panel.Priority < localPanel.Priority).OrderBy(panel => panel.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new ClientPanelConnection(panel.Host, panel.Port);
            if (await candidate.ConnectAsync())
            {
                return candidate;
            }
            await candidate.DisconnectAsync();
        }
        return null;
    }

    private async Task PromoteToActiveAsync(bool applyDelay, CancellationToken cancellationToken)
    {
        if (_project is null)
        {
            return;
        }
        RaiseRedundancyState(RedundancyRuntimeState.Failover, "Ridondanza · verifica failover in corso…");
        if (applyDelay && _localPanel is not null)
        {
            var delay = Math.Max(0, _project.Redundancy.FailoverDelayMs) * Math.Max(1, _localPanel.Priority - 1);
            await Task.Delay(delay, cancellationToken);
            var superior = await FindSuperiorPanelAsync(_localPanel, cancellationToken);
            if (superior is not null)
            {
                await UseConnectedConnectionAsync(superior, false);
                RaiseRedundancyState(RedundancyRuntimeState.Standby, "Runtime ridondante · pannello superiore nuovamente disponibile");
                return;
            }
        }

        var direct = BuildDirectConnection(_project);
        var connected = await direct.ConnectAsync();
        await UseConnectedConnectionAsync(direct, true);
        if (_localPanel is not null)
        {
            _signalRServer = new HmiSignalRServer();
            try
            {
                await _signalRServer.StartAsync(_localPanel.Port, cancellationToken);
            }
            catch
            {
                await _signalRServer.StopAsync();
                _signalRServer = null;
            }
        }
        RaiseRedundancyState(connected ? RedundancyRuntimeState.Active : RedundancyRuntimeState.Offline,
            connected
                ? $"Runtime ridondante · pannello ATTIVO · priorità {_localPanel?.Priority ?? 1}"
                : "Runtime ridondante attivo · collegamento PLC parziale o assente");
    }

    private async Task UseConnectedConnectionAsync(IMachineConnection connection, bool direct)
    {
        await _connectionLock.WaitAsync();
        _stoppingOrSwitching = true;
        try
        {
            if (_connection is not null && !ReferenceEquals(_connection, connection))
            {
                _connection.OnDataChanged -= Connection_OnDataChanged;
                _connection.ConnectionLost -= Connection_ConnectionLost;
                await _connection.DisconnectAsync();
            }
            if (!direct && _signalRServer is not null)
            {
                await _signalRServer.StopAsync();
                _signalRServer = null;
            }
            _connection = connection;
            _usingDirectPlcConnection = direct;
            _connection.OnDataChanged += Connection_OnDataChanged;
            _connection.ConnectionLost += Connection_ConnectionLost;
            App.Connection = direct ? connection : null;
            _lastPoll.Clear();
        }
        finally
        {
            _stoppingOrSwitching = false;
            _connectionLock.Release();
        }
    }

    private async void Connection_ConnectionLost(object? sender, EventArgs e)
    {
        if (_stoppingOrSwitching || _usingDirectPlcConnection || _runtimeCancellation is null)
        {
            return;
        }
        try
        {
            await PromoteToActiveAsync(true, _runtimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Arresto runtime.
        }
        catch (Exception ex)
        {
            RaiseRedundancyState(RedundancyRuntimeState.Offline, "Failover non riuscito · " + ex.Message);
        }
    }

    private async Task RedundancyMonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_project is not null && _localPanel is not null)
            {
                await Task.Delay(Math.Max(1000, _project.Redundancy.HealthCheckIntervalMs), cancellationToken);
                if (!_usingDirectPlcConnection || _localPanel.Priority <= 1)
                {
                    continue;
                }
                var superior = await FindSuperiorPanelAsync(_localPanel, cancellationToken);
                if (superior is null)
                {
                    continue;
                }
                await UseConnectedConnectionAsync(superior, false);
                RaiseRedundancyState(RedundancyRuntimeState.Standby, "Runtime ridondante · failback sul pannello superiore completato");
            }
        }
        catch (OperationCanceledException)
        {
            // Arresto runtime.
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var project = _project;
                var connection = _connection;
                if (project is null || connection is null)
                {
                    continue;
                }
                foreach (var tag in project.Tags.Where(item => item.Access != TagAccess.Write))
                {
                    var now = DateTime.UtcNow;
                    if (_lastPoll.TryGetValue(tag.Id, out var lastPoll) &&
                        (now - lastPoll).TotalMilliseconds < Math.Max(100, tag.PollIntervalMs))
                    {
                        continue;
                    }
                    _lastPoll[tag.Id] = now;
                    var plc = project.PlcConnections.FirstOrDefault(item => item.Id == tag.PlcId);
                    if (plc is null)
                    {
                        continue;
                    }
                    try
                    {
                        var value = await connection.ReadVariableAsync($"{plc.Name}.{tag.Address}");
                        if (value is not null)
                        {
                            PublishValue(tag.Id, value);
                        }
                    }
                    catch
                    {
                        // Il controllo di ridondanza gestisce la perdita della connessione.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arresto richiesto.
        }
    }

    private void Connection_OnDataChanged(object? sender, DataChangedEventArgs e)
    {
        if (_project is null)
        {
            return;
        }
        if (_usingDirectPlcConnection && _signalRServer is not null)
        {
            _ = _signalRServer.BroadcastAsync(e.VariableName, e.NewValue);
        }
        var separator = e.VariableName.IndexOf('.');
        if (separator < 0)
        {
            return;
        }
        var plcName = e.VariableName[..separator];
        var address = e.VariableName[(separator + 1)..];
        var plc = _project.PlcConnections.FirstOrDefault(item => item.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase));
        var tag = plc is null ? null : _project.Tags.FirstOrDefault(item =>
            item.PlcId == plc.Id && item.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (tag is not null)
        {
            PublishValue(tag.Id, e.NewValue);
        }
    }

    private void PublishValue(string tagId, object value)
    {
        _values[tagId] = value;
        _databaseLogger?.RecordTagValue(tagId, value);
        TagValueChanged?.Invoke(this, new TagValueChangedEventArgs(tagId, value));
    }

    private void RaiseRedundancyState(RedundancyRuntimeState state, string message) =>
        RedundancyStateChanged?.Invoke(this, new RedundancyStateChangedEventArgs(state, message));

    private static object ConvertValue(string rawValue, TagDataType dataType) => dataType switch
    {
        TagDataType.Bool => bool.TryParse(rawValue, out var boolean) ? boolean : rawValue == "1",
        TagDataType.Int => short.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
        TagDataType.DInt => int.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
        TagDataType.Real => double.Parse(rawValue.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => rawValue
    };
}

public sealed class TagValueChangedEventArgs(string tagId, object value) : EventArgs
{
    public string TagId { get; } = tagId;
    public object Value { get; } = value;
}

public enum RedundancyRuntimeState
{
    Active,
    Standby,
    Failover,
    Offline
}

public sealed class RedundancyStateChangedEventArgs(RedundancyRuntimeState state, string message) : EventArgs
{
    public RedundancyRuntimeState State { get; } = state;
    public string Message { get; } = message;
}
