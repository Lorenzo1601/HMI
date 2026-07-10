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
        public enum ErrorCode
        {
            NoError = 0,
            WrongCPU_Type = 1,
            ConnectionError = 2,
            IPAddressNotAvailable,
            WrongVarFormat = 10,
            WrongNumberReceivedBytes = 11,
            SendData = 20,
            ReadData = 30,
            WriteData = 50
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
            if (_plc == null || !IsConnected)
            {
                throw new InvalidOperationException($"Impossibile leggere la variabile '{variableName}': PLC non connesso.");
            }

#pragma warning disable CS8603 // Possibile restituzione di riferimento Null.
            return await Task.Run(() =>
            {
                try
                {
                    // S7.Net permette di leggere una variabile passando direttamente l'indirizzo testuale (es. "DB1.DBW20") [cite: 5567]
                    // Il metodo restituisce direttamente un object [cite: 5565]
                    object result = _plc.Read(variableName);

                    if (result == null)
                    {
                        Console.WriteLine($"[Lettura] La variabile '{variableName}' ha restituito null.");
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Errore durante la lettura dal PLC Siemens ({this.IpAddress}): {ex.Message}");
                    return null;
                }
            });
#pragma warning restore CS8603 // Possibile restituzione di riferimento Null.
        }

        public async Task<T> ReadClassAsync<T>(int db, int startByteAdr = 0) where T : class, new()
        {
            if (_plc == null || !IsConnected)
            {
                throw new InvalidOperationException($"Impossibile leggere il DB{db}: PLC non connesso.");
            }

#pragma warning disable CS8603 // Possibile restituzione di riferimento Null.
            return await Task.Run(() =>
            {
                try
                {
                    // Creiamo una nuova istanza vuota della classe C# passata
                    T targetClass = new T();

                    // S7.Net legge dal PLC e riempie automaticamente le proprietà della classe
                    _plc.ReadClass(targetClass, db, startByteAdr);

                    return targetClass;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Errore in lettura blocco DB{db} dal PLC Siemens: {ex.Message}");
                    return null;
                }
            });
#pragma warning restore CS8603 // Possibile restituzione di riferimento Null.
        }

        public async Task<bool> WriteVariableAsync(string variableName, object value)
        {
            // Verifica lo stato della connessione prima di procedere
            if (_plc == null || !IsConnected)
            {
                Console.WriteLine($"Impossibile scrivere '{variableName}': Il PLC Siemens non è connesso.");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    // Il metodo Write ora è void. 
                    // Non restituisce nulla, ma se c'è un problema solleva un'eccezione.
                    _plc.Write(variableName, value);

                    // Se il codice arriva a questa riga senza lanciare eccezioni, 
                    // significa che la scrittura ha avuto successo.

                    // Notifica l'HMI che il valore è cambiato localmente
                    OnDataChanged?.Invoke(this, new DataChangedEventArgs
                    {
                        VariableName = variableName,
                        NewValue = value
                    });

                    return true; // Scrittura completata con successo
                }
                catch (Exception ex)
                {
                    // Se la scrittura fallisce, finiamo qui dentro
                    Console.WriteLine($"Eccezione durante la scrittura sul PLC Siemens ({this.IpAddress}): {ex.Message}");
                    return false;
                }
            });
        }
    }
}