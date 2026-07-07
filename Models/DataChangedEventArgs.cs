using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.Models
{
    public class DataChangedEventArgs : EventArgs
    {
        public string VariableName { get; set; }
        public object NewValue { get; set; }
    }
}
