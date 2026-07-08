using System;
using System.Collections.Generic;
using System.Text;
using HMI.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace HMI.ExternalConnection
{
    internal class ClientPanelConnection : IMachineConnection
    {
        private readonly string _serverHmiIp;
        private HubConnection _hubConnection;
        public bool IsConnected { get; private set; }
        public event EventHandler<DataChangedEventArgs> OnDataChanged;
        public event EventHandler ConnectionLost;

        public ClientPanelConnection(string serverHmiIp)
        {
            _serverHmiIp = serverHmiIp;

            // 1. Configuro la connessione SignalR puntando al pannello Server
            _hubConnection = new HubConnectionBuilder()
                .WithUrl($"http://{_serverHmiIp}:5000/hmihub")
                .WithAutomaticReconnect() // Opzionale: tenta di riconnettersi da solo
                .Build();

            // 2. 
            // Dico a SignalR cosa fare se SI ACCORGE da solo che la rete è caduta
            _hubConnection.Closed += async (error) =>
            {
                this.IsConnected = false;
                // Scateno il MIO evento per avvisare il Failover!
                ConnectionLost?.Invoke(this, EventArgs.Empty);
            };

            // 3. Ascolto i dati in arrivo dal Server (se cambiano)
            _hubConnection.On<string, object>("RiceviNuovoDato", (nomeVariabile, valore) =>
            {
                OnDataChanged?.Invoke(this, new DataChangedEventArgs { VariableName = nomeVariabile, NewValue = valore });
            });
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                // Avvio la connessione. 
                // NON SERVE far partire nessun "MonitoraConnessioneRete()"!
                await _hubConnection.StartAsync();
                this.IsConnected = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            await _hubConnection.StopAsync();
            this.IsConnected = false;
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


    }
}
