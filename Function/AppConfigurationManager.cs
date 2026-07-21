using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Windows;
/*
 * Classe per la gestione della configurazione dell'applicazione
 * Legge e scrive un file di configurazione in formato INI
 * Contiene le informazioni necessarie per connettersi ai PLC e al pannello HMI
 */
namespace HMI.Function
{
    public class AppConfigurationManager
    {
        public AppConfigurationManager() { }

        public List<int> LoadConfiguration()
        {
            if (File.Exists("Settings.ini"))
            {
                using StreamReader reader = new StreamReader("Settings.ini");
                string? line = reader.ReadLine();
                if (line is not null && line.Split(':') is { Length: > 1 } parts && int.TryParse(parts[1].Trim(), out int priority))
                {
                    return new List<int> { priority };
                }

                return new List<int> { 1 };
            }
            else
            {
                try
                {
                    StreamWriter sw = new StreamWriter("Settings.ini");
                    sw.WriteLine("Priority:1"); 
                    sw.Close();
                    return new List<int> { 1 };
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Errore durante la creazione del file di configurazione: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
                    return new List<int> { 1 };
                }
            }
        }
    }
}
