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
                StreamReader reader = new StreamReader("Settings.ini");
                int Priority = int.Parse(reader.ReadLine().Split(':')[1].Trim());
                return new List<int> { Priority };
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
