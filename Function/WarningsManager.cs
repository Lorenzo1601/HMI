using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.Function
{
    internal class WarningsManager
    {
        private List<Models.WarningsModel> _warnings = new List<Models.WarningsModel>();

        public WarningsManager() { }

        public void AddWarning(Models.WarningsModel warning)
        {
            _warnings.Add(warning);
        }

        public List<Models.WarningsModel> GetWarnings()
        {
            return _warnings;
        }

        public void ClearWarnings()
        {
            _warnings.Clear();
        }
    }
}
