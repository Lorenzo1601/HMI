using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.Models
{
    internal class WarningsModel
    {
        public string PlcName { get; set; } = string.Empty;
        public string WarningName { get; set; } = string.Empty;
        public string WarningDescription { get; set; } = string.Empty;
        public DateTime WarningOnTimeStamp { get; set; } = DateTime.MinValue;
        public DateTime WarningOffTimeStamp { get; set; } = DateTime.MinValue;
    }
}
