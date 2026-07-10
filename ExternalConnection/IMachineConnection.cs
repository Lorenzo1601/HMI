using System;
using System.Collections.Generic;
using System.Text;
using HMI.Models;

namespace HMI.ExternalConnection
{

        public interface IMachineConnection
        {
            // Proprietà di stato
            bool IsConnected { get; }

            // Metodi di connessione/disconnessione
            Task<bool> ConnectAsync();
            Task DisconnectAsync();

            // Metodi di lettura/scrittura
            Task<object> ReadVariableAsync(string variableName);
            Task<bool> WriteVariableAsync(string variableName, object value);
            Task<T> ReadClassAsync<T>(int db, int startByteAdr = 0) where T : class, new()
            {
                throw new NotSupportedException($"Il driver {this.GetType().Name} non supporta la lettura a blocchi (ReadClassAsync).");
            }

        // Evento scatenato quando il valore di una variabile cambia (utile per aggiornare la UI)
        event EventHandler<DataChangedEventArgs> OnDataChanged;

            event EventHandler ConnectionLost;
        }
    
}
