using System;
using System.Collections.Generic;
using System.Text;
using HMI.Models;

namespace HMI.ExternalConnection.PLCs
{
    internal class Codesys : PLC, IMachineConnection
    {
        public bool IsConnected { get; private set; }
        public event EventHandler<DataChangedEventArgs>? OnDataChanged;
        public event EventHandler? ConnectionLost;

        public Codesys(string IpAddress, int IpPort) : base(IpAddress, IpPort)
        {
        }

        public Codesys(string IpAddress, int IpPort, string ConnectionUsername, string ConnectionPassword) : base(IpAddress, IpPort, ConnectionUsername, ConnectionPassword)
        {
        }

        public async Task<bool> ConnectAsync()
        {
            // Qui in futuro metterai il codice per collegarti al Codesys
            IsConnected = true;
            return IsConnected;
        }

        public async Task DisconnectAsync()
        {
            // Qui in futuro metterai il codice per scollegarti
            IsConnected = false;
        }

        public async Task<object?> ReadVariableAsync(string variableName)
        {
            // Qui in futuro metterai il codice di lettura
            return 0;
        }

        public async Task<bool> WriteVariableAsync(string variableName, object value)
        {
            // Qui in futuro metterai il codice di scrittura
            OnDataChanged?.Invoke(this, new DataChangedEventArgs { VariableName = variableName, NewValue = value });
            return true;
        }

        private void RaiseConnectionLost() => ConnectionLost?.Invoke(this, EventArgs.Empty);
    }
}
