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
        private static bool _monitoringMasterConnection = false;

        // --- CONFIGURAZIONE PANNELLO E RIDONDANZA ---
        private static bool _isAttualmenteServer = false;

        // 1 = Master (Parte come Server)
        // 2 = Primo Backup (Parte come Client, se cade la rete subentra in 0 secondi)
        // 3 = Secondo Backup (Parte come Client, se cade la rete aspetta 5 secondi prima di agire)
        public static int Priority = 1; // IN PRODUZIONE: Leggilo da un file config (.json o .ini)

        private static readonly string[] _listaPannelliIp = new string[]
        {
            "192.168.0.50", // Priorità 1 (Master)
            "192.168.0.51", // Priorità 2
            "192.168.0.52", // Priorità 3
            "192.168.0.53", // Priorità 4
            "192.168.0.54"  // Priorità 5
        };

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Definisco il ruolo iniziale in base alla priorità configurata per questa macchina
            _isAttualmenteServer = (Priority == 1);

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
                string ipTarget = ipServerForzato ?? _listaPannelliIp[0];
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
            if(_isAttualmenteServer)
            {
                MessageBox.Show("Allarme: Collegamento fisico con i PLC interrotto!");
                return;
            }

            // Calcolo quanto devo aspettare in base a chi sono.
            // Priorità 2 aspetta 0 ms. Priorità 3 aspetta 5000 ms. Priorità 4 aspetta 10000 ms, ecc.
            int tempoDiAttesa = (Priority - 2) * 5000;

            if (tempoDiAttesa > 0)
            {
                await Task.Delay(tempoDiAttesa);
            }

            bool foundSuperiorServer = false;

            // Faccio un ciclo per testare SOLO i pannelli più importanti di me.
            // Se io ho Priorità 4, testerò l'indice 0, poi l'1, poi il 2.
            for (int i = 0; i < Priority - 1; i++)
            {
                string testingIp = _listaPannelliIp[i];

                var testConnection = new ClientPanelConnection(testingIp);
                bool answer = await testConnection.ConnectAsync();

                if (answer)
                {
                    // Ottimo! Qualcuno di più importante di me è diventato il Server.
                    await testConnection.DisconnectAsync();
                    OpenConnection(testingIp);
                    foundSuperiorServer = true;
                    break; // Esco dal ciclo for, ho finito!
                }
            }

            // Se ho provato tutti quelli sopra di me e NESSUNO ha risposto... tocca a me!
            if (!foundSuperiorServer)
            {
                _isAttualmenteServer = true;
                OpenConnection();
            }

            // In ogni caso, inizio a spiare se un giorno tornerà il Master (Priorità 1)
            MonitoringMasterConnection();
        }

        // ==========================================
        // FAILBACK: ATTESA DEL RITORNO DEL MASTER
        // ==========================================
        private static async void MonitoringMasterConnection()
        {
            if (Priority == 1 || _monitoringMasterConnection) return;

            _monitoringMasterConnection = true;

            while (true)
            {
                await Task.Delay(5000); // Controllo ogni 5 secondi

                try
                {
                    // Verifico se c'è un pannello vivo che ha una priorità MIGLIORE della mia
                    for (int i = 0; i < Priority - 1; i++)
                    {
                        string testingIp = _listaPannelliIp[i];
                        var testConnection = new ClientPanelConnection(testingIp);

                        if (await testConnection.ConnectAsync())
                        {
                            // Evviva, un fratello maggiore è tornato online!
                            await testConnection.DisconnectAsync();

                            _isAttualmenteServer = false;

                            if (Connection != null)
                            {
                                Connection.ConnectionLost -= FailOverConnection;
                                await Connection.DisconnectAsync();
                            }

                            // Mi ricollego al pannello più importante che ho trovato
                            OpenConnection(testingIp);

                            _monitoringMasterConnection = false;
                            return; // Esco definitivamente dalla funzione (il while si rompe)
                        }
                    }
                }
                catch
                {
                    // Ignoro gli errori e riprovo al prossimo giro
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