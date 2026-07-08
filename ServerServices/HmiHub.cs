using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.SignalR;

namespace HMI.ServerServices
{
    public class HmiHub : Hub
    {
        // Questo metodo può essere chiamato dai Client se vogliono scrivere un dato
        public async Task ScriviVariabileDaClient(string variableName, object value)
        {
            // Qui dirai alla tua connessione PLC (App.Connessione) di scrivere il dato fisico!
            await App.Connessione.WriteVariableAsync(variableName, value);
        }
    }
}
