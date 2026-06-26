using System;
using System.Collections.Generic;
using System.Text;

namespace HMI.Models
{
    internal class RecipesModel
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> TagName { get; set; } = new List<string>();
        public List<double> Ingredients { get; set; } = new List<double>();

    }
}


/*
 * Descrive i dati di una ricetta, inclusi il nome, la descrizione, i tag associati e gli ingredienti necessari.
 * necessario per la gestione delle ricette all'interno dell'applicazione.
 */