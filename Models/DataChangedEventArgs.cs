using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.Models
{
    public class DataChangedEventArgs : EventArgs
    {
        public string VariableName { get; set; } = string.Empty;
        public object NewValue { get; set; } = new();
    }
}
