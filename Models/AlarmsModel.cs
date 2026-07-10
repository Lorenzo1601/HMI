using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.Models
{
    internal class AlarmsModel
    {
        public string PlcName { get; set; } = string.Empty;
        public string AlarmName { get; set; } = string.Empty;
        public string AlarmDescription { get; set; } = string.Empty;
        public DateTime AlarmOnTimeStamp { get; set; } = DateTime.MinValue;
        public DateTime AlarmOffTimeStamp { get; set; } = DateTime.MinValue;
    }
}
