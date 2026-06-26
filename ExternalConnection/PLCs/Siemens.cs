using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.ExternalConnection.PLCs
{
    internal class Siemens : PLC
    {
        public Siemens(string IpAddress, int IpPort) : base(IpAddress, IpPort)
        {
        }
        public Siemens(string IpAddress, int IpPort, string ConnectionUsername, string ConnectionPassword) : base(IpAddress, IpPort, ConnectionUsername, ConnectionPassword)
        {
        }

    }
}
