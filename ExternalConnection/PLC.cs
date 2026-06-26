using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.ExternalConnection
{
    internal class PLC
    {
        protected string ConnectionUsername { get; set; } = string.Empty;
        protected string ConnectionPassword { get; set; } = string.Empty;
        protected string IpAddress { get; set; } = string.Empty;
        protected int IpPort { get; set; } = 102;

        protected PLC(string IpAddress, int IpPort)
        {
            this.IpAddress = IpAddress;
            this.IpPort = IpPort;
        }

        protected PLC(string IpAddress, int IpPort, string ConnectionUsername, string ConnectionPassword)
        {
            this.IpAddress = IpAddress;
            this.IpPort = IpPort;
            this.ConnectionUsername = ConnectionUsername;
            this.ConnectionPassword = ConnectionPassword;
        }

    }
}
