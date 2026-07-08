using System;
using System.Threading.Tasks;
using HMI.Models;
using S7.Net;

namespace HMI.ExternalConnection.PLCs
{
    internal class Siemens : PLC, IMachineConnection
    {
        public bool IsConnected { get; private set; }

        public event EventHandler<DataChangedEventArgs> OnDataChanged;
        public event EventHandler ConnectionLost;

        private CpuType _connectionType;
        private short _rack;
        private short _slot;
        private Plc _plc;
        public enum DataType
        {
            Input = 129,
            Output = 130,
            Memory = 131,
            DataBlock = 132,
            Timer = 29,
            Counter = 28
        }


        // Ho aggiunto Rack e Slot con valori di default 0 e 1 (per S7-1200/1500)
        public Siemens(string IpAddress, int IpPort, CpuType plcType, short rack = 0, short slot = 1)
            : base(IpAddress, IpPort)
        {
            _connectionType = plcType;
            _rack = rack;
            _slot = slot;
        }

        public Siemens(string IpAddress, int IpPort, string ConnectionUsername, string ConnectionPassword, CpuType plcType, short rack = 0, short slot = 1)
            : base(IpAddress, IpPort, ConnectionUsername, ConnectionPassword)
        {
            _connectionType = plcType;
            _rack = rack;
            _slot = slot;
        }

        public async Task<bool> ConnectAsync() //Collegamento al PLC Siemens
        {
            _plc = new Plc(_connectionType, this.IpAddress, _rack, _slot);

            try
            {
                await _plc.OpenAsync();

                IsConnected = _plc.IsConnected;
                return IsConnected;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                Console.WriteLine($"Errore PLC Siemens ({this.IpAddress}): {ex.Message}");
                return false;
            }
        }

        public Task DisconnectAsync() 
        {
            if (_plc != null && _plc.IsConnected)
            {
                _plc.Close();
            }

            IsConnected = false;

            return Task.CompletedTask;
        }

        public async Task<object> ReadVariableAsync(string variableName)
        {
            // Logica di lettura specifica per Siemens
            return 0;
        }

        public async Task<bool> WriteVariableAsync(string variableName, object value)
        {
            // Logica di scrittura specifica per Siemens
            return true;
        }
    }
}