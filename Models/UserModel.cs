using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.Models
{
    internal class UserModel
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }
}



/*
 * Definisce il modello di dati per un utente, con proprietà per il nome utente, la password e il ruolo dell'utente.
 */