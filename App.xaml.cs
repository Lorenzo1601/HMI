using System;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using HMI.ExternalConnection;
using HMI.ExternalConnection.PLCs;
using HMI.ServerServices;

namespace HMI
{
    public partial class App : Application
    {
        public static IMachineConnection Connessione { get; private set; }

        // Manteniamo un riferimento al contesto di SignalR per poter "spingere" i dati ai client
        public static IHubContext<HmiHub> SignalRContext { get; private set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool isServer = true; // Leggi da config

            if (isServer)
            {
                // 1. Mi collego fisicamente al PLC
                Connessione = new Siemens("192.168.0.1", 102);
                await Connessione.ConnectAsync();

                // 2. AVVIO IL SERVER SIGNALR IN BACKGROUND
                AvviaServerSignalR();

                // 3. Quando il PLC cambia un dato, lo giro a tutti i Client SignalR!
                Connessione.OnDataChanged += async (sender, args) =>
                {
                    if (SignalRContext != null)
                    {
                        // Manda il dato a tutti i pannelli Client connessi
                        await SignalRContext.Clients.All.SendAsync("RiceviNuovoDato", args.VariableName, args.NewValue);
                    }
                };
            }
            else
            {
                // Se sono un Client, mi collego al server HMI
                Connessione = new ClientPanelConnection("192.168.0.50");
                await Connessione.ConnectAsync();
            }
        }

        private void AvviaServerSignalR()
        {
            try
            {
                var builder = WebApplication.CreateBuilder();

                // Aggiungo il servizio SignalR
                builder.Services.AddSignalR();

                var webApp = builder.Build();

                // Mappo l'Hub all'indirizzo /hmihub
                webApp.MapHub<HmiHub>("/hmihub");

                // Salvo il context così posso usarlo in App.xaml.cs per spingere i dati
                SignalRContext = webApp.Services.GetRequiredService<IHubContext<HmiHub>>();

                // Faccio partire il server web sulla porta 5000 (Non blocca la UI di WPF)
                // Ascolta su tutte le interfacce di rete (0.0.0.0)
                webApp.RunAsync("http://0.0.0.0:5000");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Impossibile avviare il Server SignalR: " + ex.Message);
            }
        }
    }
}