using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.Models
{
    internal class DatabaseConnectionModel
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

    }
}
