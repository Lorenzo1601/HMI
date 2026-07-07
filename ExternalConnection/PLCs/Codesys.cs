using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.ExternalConnection.PLCs
{
    internal class Codesys : PLC, IMachineConnection
    {
        public Codesys(string IpAddress, int IpPort) : base(IpAddress, IpPort)
        {
        }
        public Codesys(string IpAddress, int IpPort, string ConnectionUsername, string ConnectionPassword) : base(IpAddress, IpPort, ConnectionUsername, ConnectionPassword)
        {
        }
    }
}
