using Opc.Ua;

namespace HMI.ExternalConnection
{
    public class OpcUaConfig
    {
        public string ServerUrl { get; set; } = "opc.tcp://192.168.1.100:4840";
        public string ApplicationName { get; set; } = "MyHMI_OPCUA_Client";

        public MessageSecurityMode SecurityMode { get; set; } = MessageSecurityMode.None;

        // CORREZIONE QUI: Usa SecurityPolicies invece di SecurityPolicyUris
        public string SecurityPolicyUri { get; set; } = SecurityPolicies.None;

        public bool UseAnonymous { get; set; } = true;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public bool AutoAcceptUntrustedCertificates { get; set; } = true;

        public uint SessionTimeout { get; set; } = 60000;
        public int KeepAliveInterval { get; set; } = 5000;
        public int PublishingInterval { get; set; } = 500;
    }
}