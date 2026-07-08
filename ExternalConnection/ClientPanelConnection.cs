using System;
using System.Collections.Generic;
using System.Text;
using HMI.Models;

namespace HMI.ExternalConnection
{
    internal class ClientPanelConnection : IMachineConnection
    {
        private readonly string _serverHmiIp;
        public bool IsConnected { get; private set; }
        public event EventHandler<DataChangedEventArgs> OnDataChanged;
        public event EventHandler ConnectionLost;

        public ClientPanelConnection(string serverHmiIp)
        {
            _serverHmiIp = serverHmiIp;
        }

        public async Task<bool> ConnectAsync()
        {
            // Qui ti connetti via rete informatica al pannello SERVER, NON al PLC.
            // Es: await _signalRConnection.StartAsync();
            IsConnected = true;
            return IsConnected;
        }

        public async Task DisconnectAsync()
        {
            // Disconnessione dal server HMI
            IsConnected = false;
        }

        public async Task<object> ReadVariableAsync(string variableName)
        {
            // Invia una richiesta HTTP/SignalR al pannello Server per farsi dare il valore
            // Es: return await _httpClient.GetAsync($"http://{_serverHmiIp}/api/read/{variableName}");
            return 0;
        }

        public async Task<bool> WriteVariableAsync(string variableName, object value)
        {
            // Invia una richiesta HTTP/SignalR al pannello Server per scrivergli il valore. 
            // Sarà poi il Server a scriverlo fisicamente nel PLC.
            return true;
        }

        private void CheckServerStatus()
        {
            // ... logica fittizia in cui ti accorgi che il Server HMI è spento
            bool serverMorto = true;

            if (serverMorto)
            {
                this.IsConnected = false;
                // Avviso l'applicazione che ho perso il Server
                ConnectionLost?.Invoke(this, EventArgs.Empty);
            }
        }

    }
}
