using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HMI.Models;

namespace HMI.ExternalConnection
{
    public class MultiPlcConnection : IMachineConnection
    {
        // Contiene tutti i nostri PLC, identificati da un nome (es. "PLC1", "Linea2")
        private readonly Dictionary<string, IMachineConnection> _plcs = new Dictionary<string, IMachineConnection>();

        public bool IsConnected { get; private set; }

        public event EventHandler<DataChangedEventArgs> OnDataChanged;
        public event EventHandler ConnectionLost;

        // Metodo per aggiungere i PLC al nostro gestore
        public void AddPlc(string nome, IMachineConnection PlcConnection)
        {
            _plcs[nome] = PlcConnection;

            // Ri-inoltriamo gli eventi dei singoli PLC verso l'alto (aggiungendo il prefisso)
            PlcConnection.OnDataChanged += (sender, e) =>
            {
                OnDataChanged?.Invoke(this, new DataChangedEventArgs
                {
                    VariableName = $"{nome}.{e.VariableName}", // Es: "PLC1.Motore"
                    NewValue = e.NewValue
                });
            };

            PlcConnection.ConnectionLost += (sender, e) => ConnectionLost?.Invoke(this, EventArgs.Empty);
        }

        public async Task<bool> ConnectAsync()
        {
            bool tuttoOk = true;
            // Connettiamo tutti i PLC in parallelo
            foreach (var plc in _plcs.Values)
            {
                bool success = await plc.ConnectAsync();
                if (!success) tuttoOk = false;
            }
            IsConnected = tuttoOk;
            return tuttoOk;
        }

        public async Task DisconnectAsync()
        {
            foreach (var plc in _plcs.Values)
            {
                await plc.DisconnectAsync();
            }
            IsConnected = false;
        }

        // --- LA MAGIA DEL ROUTING ---

        // Helper per separare "PLC1.Motore" in "PLC1" e "Motore"
        private (string NomePlc, string VariabilePlc) EstraiPlcEVariabile(string variabileCompleta)
        {
            var parti = variabileCompleta.Split('.');
            if (parti.Length < 2) throw new ArgumentException("Formato variabile non valido. Usa NomePlc.Variabile");
            return (parti[0], string.Join(".", parti[1..])); // (NomePlc, Variabile)
        }

        public async Task<object> ReadVariableAsync(string variableName)
        {
            var (nomePlc, variabileVera) = EstraiPlcEVariabile(variableName);

            if (_plcs.TryGetValue(nomePlc, out var plc))
            {
                return await plc.ReadVariableAsync(variabileVera);
            }
            throw new Exception($"PLC {nomePlc} non trovato.");
        }

        public async Task<T> ReadClassAsync<T>(string nomePlc, int db, int startByteAdr = 0) where T : class, new()//Al momento solo per driver siemens
        {
            if (_plcs.TryGetValue(nomePlc, out var plc))
            {
                return await plc.ReadClassAsync<T>(db, startByteAdr);
            }
            throw new Exception($"PLC {nomePlc} non trovato.");
        }

        public async Task<bool> WriteVariableAsync(string variableName, object value)
        {
            var (nomePlc, variabileVera) = EstraiPlcEVariabile(variableName);

            if (_plcs.TryGetValue(nomePlc, out var plc))
            {
                return await plc.WriteVariableAsync(variabileVera, value);
            }
            return false;
        }
    }
}