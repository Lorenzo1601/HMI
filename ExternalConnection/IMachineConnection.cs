using System;
using System.Collections.Generic;
using System.Text;
using HMI.Models;

namespace HMI.ExternalConnection
{
    internal interface IMachineConnection
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

            // Evento scatenato quando il valore di una variabile cambia (utile per aggiornare la UI)
            event EventHandler<DataChangedEventArgs> OnDataChanged;
        }
    }
}
