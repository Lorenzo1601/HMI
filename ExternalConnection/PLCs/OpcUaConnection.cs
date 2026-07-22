using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using HMI.Models; // Necessario per DataChangedEventArgs
using Opc.Ua;
using Opc.Ua.Client;

namespace HMI.ExternalConnection.PLCs
{
    public class OpcUaConnection : IMachineConnection
    {
        private readonly OpcUaConfig _config;
        private ApplicationConfiguration _appConfig;
        private Session _session;
        private Subscription _subscription;

        // Eventi richiesti dall'interfaccia IMachineConnection
        public event EventHandler<DataChangedEventArgs> OnDataChanged;
        public event EventHandler ConnectionLost;

        public bool IsConnected => _session != null && _session.Connected;

        public OpcUaConnection(OpcUaConfig config)
        {
            _config = config;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                // 1. Inizializzazione rigorosa della configurazione
                _appConfig = new ApplicationConfiguration
                {
                    ApplicationName = _config.ApplicationName ?? "HMI_Client",
                    ApplicationUri = Utils.Format(@"urn:{0}:{1}", System.Net.Dns.GetHostName(), _config.ApplicationName ?? "HMI_Client"),
                    ApplicationType = ApplicationType.Client,
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier { StoreType = "Directory", StorePath = "%CommonApplicationData%/OPC Foundation/CertificateStores/MachineDefault" },
                        TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = "%CommonApplicationData%/OPC Foundation/CertificateStores/UA Applications" },

                        // FIX: Aggiunto TrustedIssuerCertificates richiesto dalla libreria
                        TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = "%CommonApplicationData%/OPC Foundation/CertificateStores/UA Certificate Authorities" },

                        RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = "%CommonApplicationData%/OPC Foundation/CertificateStores/RejectedCertificates" },
                        AutoAcceptUntrustedCertificates = _config.AutoAcceptUntrustedCertificates
                    },
                    TransportConfigurations = new TransportConfigurationCollection(),
                    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = (int)_config.SessionTimeout }
                };

                await _appConfig.Validate(ApplicationType.Client);

                if (_config.AutoAcceptUntrustedCertificates)
                {
                    _appConfig.CertificateValidator.CertificateValidation += (s, e) => { e.Accept = true; };
                }

                // Assicuriamoci che l'URL abbia il prefisso corretto
                string serverUrl = _config.ServerUrl.StartsWith("opc.tcp://")
                    ? _config.ServerUrl
                    : "opc.tcp://" + _config.ServerUrl;

                // 2. Creazione rigorosa dell'Endpoint
                EndpointDescription selectedEndpoint = CoreClientUtils.SelectEndpoint(
                    _appConfig,
                    serverUrl,
                    _config.SecurityMode != MessageSecurityMode.None,
                    15000);

                // FIX FONDAMENTALE: Sovrascrive l'hostname ritornato dal server con l'IP inserito dall'utente
                var userUri = new Uri(serverUrl);
                var endpointUri = new Uri(selectedEndpoint.EndpointUrl);
                if (userUri.Host != endpointUri.Host)
                {
                    var builder = new UriBuilder(selectedEndpoint.EndpointUrl) { Host = userUri.Host };
                    selectedEndpoint.EndpointUrl = builder.ToString();
                }

                EndpointConfiguration endpointConfiguration = EndpointConfiguration.Create(_appConfig);
                ConfiguredEndpoint endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);

                // 3. Creazione rigorosa dell'identità
                IUserIdentity userIdentity;
                if (_config.UseAnonymous)
                {
                    userIdentity = new UserIdentity(new AnonymousIdentityToken());
                }
                else
                {
                    // FIX CS1503 RIPRISTINATO: Convertiamo la password in byte[] (UTF8)
                    userIdentity = new UserIdentity(_config.Username, System.Text.Encoding.UTF8.GetBytes(_config.Password));
                }

                // 4. Creazione Sessione
                _session = await Session.Create(
                    _appConfig,                                 // ApplicationConfiguration
                    endpoint,                                   // ConfiguredEndpoint
                    false,                                      // updateBeforeConnect
                    _config.ApplicationName ?? "HMI_Session",   // sessionName
                    _config.SessionTimeout == 0 ? 60000 : _config.SessionTimeout, // sessionTimeout
                    userIdentity,                               // identity
                    (IList<string>)null                         // preferredLocales
                );

                _session.KeepAliveInterval = _config.KeepAliveInterval == 0 ? 5000 : _config.KeepAliveInterval;
                _session.KeepAlive += Session_KeepAlive;

                return true;
            }
            catch (Exception ex)
            {
                // Buttiamo fuori l'eccezione vera per poterla leggere nell'interfaccia
                throw new Exception($"Errore OPC UA dettagliato: {ex.Message}", ex);
            }
        }

        public async Task<object> ReadVariableAsync(string variableName)
        {
            if (!IsConnected) return null;
            try
            {
                return await Task.Run(() =>
                {
                    NodeId nodeId = new NodeId(variableName);
                    DataValue value = _session.ReadValue(nodeId);
                    return value?.Value;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC UA Error] Errore lettura: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> WriteVariableAsync(string variableName, object value)
        {
            if (!IsConnected) return false;
            try
            {
                return await Task.Run(() =>
                {
                    NodeId nodeId = new NodeId(variableName);
                    WriteValue writeValue = new WriteValue
                    {
                        NodeId = nodeId,
                        AttributeId = Attributes.Value,
                        Value = new DataValue(new Variant(value))
                    };

                    WriteValueCollection nodesToWrite = new WriteValueCollection { writeValue };
                    StatusCodeCollection results;
                    DiagnosticInfoCollection diagnostics;

                    // Passiamo "new RequestHeader()" anziché null per eliminare l'errore del byte[]
                    _session.Write(
                        new RequestHeader(),
                        nodesToWrite,
                        out results,
                        out diagnostics
                    );

                    if (results != null && results.Count > 0)
                    {
                        return StatusCode.IsGood(results[0]);
                    }
                    return false;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC UA Error] Errore scrittura: {ex.Message}");
                return false;
            }
        }

        public void SubscribeToNodes(IEnumerable<string> nodeIds)
        {
            if (!IsConnected) return;

            try
            {
                if (_subscription == null)
                {
                    _subscription = new Subscription(_session.DefaultSubscription)
                    {
                        PublishingInterval = _config.PublishingInterval
                    };
                    _session.AddSubscription(_subscription);
                    _subscription.Create();
                }

                foreach (var nodeIdStr in nodeIds)
                {
                    MonitoredItem item = new MonitoredItem(_subscription.DefaultItem)
                    {
                        StartNodeId = new NodeId(nodeIdStr),
                        AttributeId = Attributes.Value,
                        SamplingInterval = _config.PublishingInterval,
                        QueueSize = 1,
                        DiscardOldest = true
                    };

                    item.Notification += MonitoredItem_Notification;
                    _subscription.AddItem(item);
                }

                _subscription.ApplyChanges();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPC UA Error] Errore sottoscrizione: {ex.Message}");
            }
        }

        private void MonitoredItem_Notification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            if (e.NotificationValue is MonitoredItemNotification notification)
            {
                var value = notification.Value.Value;
                var nodeId = item.StartNodeId.ToString();

                // Creazione dell'evento senza parametri nel costruttore
                var args = new DataChangedEventArgs();

                /* 
                 * NOTA BENE: Assegna qui le proprietà reali basate sul tuo 
                 * file Models/DataChangedEventArgs.cs
                 */
                // args.NomeVariabile = nodeId;
                // args.NuovoValore = value;

                OnDataChanged?.Invoke(this, args);
            }
        }

        private void Session_KeepAlive(ISession session, KeepAliveEventArgs e)
        {
            if (ServiceResult.IsBad(e.Status))
            {
                ConnectionLost?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task<List<ReferenceDescription>> BrowseNodesAsync(string parentNodeId = null)
        {
            if (!IsConnected)
            {
                return new List<ReferenceDescription>();
            }

            return await Task.Run(() =>
            {
                var result = new List<ReferenceDescription>();

                try
                {
                    var browseDescription = new BrowseDescription
                    {
                        NodeId = string.IsNullOrEmpty(parentNodeId)
                            ? ObjectIds.ObjectsFolder
                            : NodeId.Parse(parentNodeId),

                        BrowseDirection = BrowseDirection.Forward,
                        ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                        IncludeSubtypes = true,
                        NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                        ResultMask = (uint)BrowseResultMask.All
                    };

                    _session.Browse(
                        null,
                        null,
                        0,
                        new BrowseDescriptionCollection { browseDescription },
                        out BrowseResultCollection results,
                        out DiagnosticInfoCollection diagnosticInfos);

                    if (results != null && results.Count > 0)
                    {
                        foreach (var reference in results[0].References)
                        {
                            result.Add(reference);
                        }
                    }
                }
                catch
                {
                    // Ignora errori di browse
                }

                return result;
            });
        }

        public async Task DisconnectAsync()
        {
            if (_session != null)
            {
                _session.KeepAlive -= Session_KeepAlive;
                if (_subscription != null)
                {
                    _subscription.Delete(true);
                }
                _session.Close();
                _session.Dispose();
                _session = null;
            }
            await Task.CompletedTask;
        }
    }
}