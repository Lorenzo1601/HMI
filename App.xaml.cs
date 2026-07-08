using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using HMI.ExternalConnection;
using HMI.ExternalConnection.PLCs;
using HMI.ServerServices; // Assicurati che questo sia il namespace dove hai creato HmiHub.cs

namespace HMI
{
    public partial class App : Application
    {
        // Variabili globali accessibili da tutte le tue finestre (es. App.Connessione.WriteVariableAsync...)
        public static IMachineConnection Connection { get; private set; }
        public static IHubContext<HmiHub> SignalRContext { get; private set; }

        // --- CONFIGURAZIONE PANNELLO E RIDONDANZA ---
        private static bool _isAttualmenteServer = false;

        // 1 = Master (Parte come Server)
        // 2 = Primo Backup (Parte come Client, se cade la rete subentra in 0 secondi)
        // 3 = Secondo Backup (Parte come Client, se cade la rete aspetta 5 secondi prima di agire)
        public static int MiaPriorita = 1; // IN PRODUZIONE: Leggilo da un file config (.json o .ini)

        // Indirizzi IP dei pannelli HMI (per la rete locale tra PC)
        private static string _ipServerPrincipale = "192.168.0.50";
        private static string _ipServerSecondario = "192.168.0.51";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Definisco il ruolo iniziale in base alla priorità configurata per questa macchina
            _isAttualmenteServer = (MiaPriorita == 1);

            OpenConnection();
        }

        public static async void OpenConnection(string ipServerForzato = null)
        {
            // 1. Pulisco eventuali eventi vecchi in caso di switch/failover
            if (Connection != null)
            {
                Connection.ConnectionLost -= FailOverConnection;
            }

            // 2. CONFIGURAZIONE IN BASE AL RUOLO ATTUALE
            if (_isAttualmenteServer)
            {
                // ==========================================
                // MODALITÀ SERVER: Mi connetto ai veri PLC
                // ==========================================
                var gestorePlc = new MultiPlcConnection();

                // Aggiungi tutti i PLC del tuo impianto
                gestorePlc.AggiungiPlc("PLC_1", new Siemens("192.168.0.10", 102));
                gestorePlc.AggiungiPlc("PLC_2", new Codesys("192.168.0.20", 1202));

                Connection = gestorePlc;

                // Avvio il server SignalR invisibile per accettare le connessioni degli altri pannelli
                AvviaServerSignalR();

                // Inoltro ai client SignalR ogni cambio dato proveniente dai PLC
                Connection.OnDataChanged += async (sender, args) =>
                {
                    if (SignalRContext != null)
                    {
                        // Manda il pacchetto a tutti i pannelli HMI collegati
                        await SignalRContext.Clients.All.SendAsync("RiceviNuovoDato", args.VariableName, args.NewValue);
                    }
                };
            }
            else
            {
                // ==========================================
                // MODALITÀ CLIENT: Mi connetto al Server HMI
                // ==========================================
                string ipTarget = ipServerForzato ?? _ipServerPrincipale;
                Connection = new ClientPanelConnection(ipTarget);
            }

            // 3. MI ISCRIVO ALL'EVENTO DI DISCONNESSIONE E AVVIO
            Connection.ConnectionLost += FailOverConnection;

            bool successo = await Connection.ConnectAsync();
            if (!successo && _isAttualmenteServer)
            {
                MessageBox.Show("Attenzione: Impossibile raggiungere uno o più PLC all'avvio.");
            }
        }

        // ==========================================
        // GESTIONE FAILOVER (RIDONDANZA E PRIORITÀ)
        // ==========================================
        private static async void FailOverConnection(object sender, EventArgs e)
        {
            if (_isAttualmenteServer)
            {
                // Se ero già il server, vuol dire che si è tranciato il cavo fisico con il PLC
                MessageBox.Show("Allarme: Collegamento fisico con i PLC interrotto!");
                return;
            }

            // Se sono qui, ero un Client e ho perso il collegamento col pannello Server principale.

            if (MiaPriorita == 2)
            {
                // Sono il primo backup: divento subito io il Server e mi collego ai PLC
                _isAttualmenteServer = true;
                OpenConnection();
            }
            else if (MiaPriorita == 3)
            {
                // Sono il secondo backup: aspetto 5 secondi per dare il tempo al Pannello 2 di fare lo switch
                await Task.Delay(5000);

                // Faccio un "ping" di prova verso il Pannello 2 per vedere se lui è diventato il Server
                var testConnection = new ClientPanelConnection(_ipServerSecondario);
                bool pannello2Risponde = await testConnection.ConnectAsync();

                if (pannello2Risponde)
                {
                    // Ok, il Pannello 2 ha preso le redini dell'impianto. Mi collego a lui come Client.
                    await testConnection.DisconnectAsync();
                    OpenConnection(_ipServerSecondario);
                }
                else
                {
                    // Sia il Pannello 1 che il Pannello 2 sono morti. Divento io il Server!
                    _isAttualmenteServer = true;
                    OpenConnection();
                }
            }
        }

        // ==========================================
        // AVVIO DEL SERVER SIGNALR (KESTREL WEB SERVER)
        // ==========================================
        private static void AvviaServerSignalR()
        {
            // Evita di avviare due volte il server se c'è un failover anomalo
            if (SignalRContext != null) return;

            try
            {
                var builder = WebApplication.CreateBuilder();

                // Aggiunge la libreria SignalR ai servizi disponibili
                builder.Services.AddSignalR();

                var webApp = builder.Build();

                // Associo l'indirizzo "/hmihub" alla nostra classe HmiHub
                webApp.MapHub<HmiHub>("/hmihub");

                // Conservo il "Context", ovvero la frusta per comandare SignalR dal di fuori dell'Hub (per il push dei dati)
                SignalRContext = webApp.Services.GetRequiredService<IHubContext<HmiHub>>();

                // Avvio il server in background sulla porta 5000 (0.0.0.0 significa "ascolta su tutte le schede di rete")
                webApp.RunAsync("http://0.0.0.0:5000");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Errore grave nell'avvio del Server SignalR: " + ex.Message);
            }
        }
    }
}