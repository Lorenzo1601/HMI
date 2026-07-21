using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace HMI.ServerServices;

public sealed class HmiHub : Hub
{
    public async Task<object?> ReadVariableFromClient(string variableName)
    {
        if (App.Connection is null)
        {
            throw new HubException("Il pannello attivo non è collegato ai PLC.");
        }
        return await App.Connection.ReadVariableAsync(variableName);
    }

    public async Task<bool> WriteVariableFromClient(string variableName, object value)
    {
        if (App.Connection is null)
        {
            throw new HubException("Il pannello attivo non è collegato ai PLC.");
        }
        return await App.Connection.WriteVariableAsync(variableName, ConvertJson(value));
    }

    private static object ConvertJson(object value)
    {
        if (value is not JsonElement json)
        {
            return value;
        }
        return json.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when json.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number when json.TryGetDouble(out var number) => number,
            JsonValueKind.String => json.GetString() ?? string.Empty,
            _ => json.ToString()
        };
    }
}
