using System;
using System.Collections.Generic;
using System.Text;
using HMI.Models;

namespace HMI.Function
{
    internal class AlarmsManager
    {
        private List<Models.AlarmsModel> _Alarms = new List<Models.AlarmsModel>();

        public AlarmsManager() { }

        public void AddAlarm(Models.AlarmsModel alarm)
        {
            _Alarms.Add(alarm);
        }

        public List<Models.AlarmsModel> GetAlarms()
        {
            return _Alarms;
        }

        public void ClearAlarms()
        {
            _Alarms.Clear();
        }

    }
}
