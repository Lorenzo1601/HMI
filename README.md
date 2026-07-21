# HMI Studio

HMI Studio è un editor desktop WPF che separa la progettazione grafica dal runtime di macchina.

## Funzioni disponibili

- pagine standard organizzabili in cartelle, pagine template riutilizzabili e pagine popup;
- dimensione e colore di sfondo configurabili per ogni pagina;
- testi, pulsanti, visualizzatori, spie, campi numerici, navigazione, popup, ricette, allarmi, storici, grafici e immagini;
- visualizzatori e campi numerici con testo adattivo, descrizione opzionale e sfondo/bordo disattivabili;
- immagini standalone o usate come contenuto dei pulsanti;
- selezione multipla con `Ctrl+clic`, spostamento di gruppo e comandi per allineare gli oggetti tra loro o centrarli nella pagina;
- modifica di posizione, dimensioni, colori, testo, allineamento del testo e associazioni dal pannello proprietà;
- spia circolare compatta con colore dinamico per stato tag;
- animazioni del colore di sfondo e del testo in base al valore di una tag;
- configurazione di più PLC Siemens S7, Codesys o simulati, con campi coerenti con il driver scelto;
- creazione di tag con indirizzo, tipo, accesso e intervallo di lettura;
- organizzazione di tag e allarmi in cartelle e sottocartelle;
- ricette modificabili in runtime, creabili, rinominabili ed eliminabili, con lettura dal PLC e scrittura verso il PLC;
- allarmi con condizioni, gravità, filtri, data/ora di attivazione e risoluzione e riconoscimento operatore;
- storico allarmi persistente su file, filtrabile e con retention configurabile nell'oggetto;
- storicizzazione MySQL selettiva per tag, su variazione oppure a intervallo temporale;
- consultazione runtime di database, tabelle e record con filtri per testo, data/ora e numero massimo di righe;
- grafico a linea multiserie configurabile per valori PLC live oppure dati storici MySQL, con nome e colore indipendenti per ogni tag;
- palette colori integrata e inserimento manuale dei codici colore;
- ridondanza tra pannelli con priorità, standby, failover e failback;
- gestione utenti con password protette, livelli di accesso per oggetto, blocco temporaneo dopo login errati e logout automatico opzionale;
- oggetti runtime per login, logout e amministrazione utenti, con storico persistente delle sessioni;
- salvataggio e apertura di progetti nel formato JSON `.hmiproject`;
- runtime a schermo intero e senza elementi dell'editor;
- progetto dimostrativo integrato, utilizzabile senza PLC tramite il driver `Simulator`;
- esportazione ZIP di un pacchetto bloccato in sola modalità runtime.

## Flusso di lavoro

1. Nella scheda **PLC**, aggiungere il controllore e scegliere il driver. I parametri mostrati cambiano in base al collegamento selezionato.
2. Nella scheda **TAG**, creare cartelle e variabili e collegarle al PLC.
3. Aggiungere cartelle, pagine e oggetti grafici dalla barra sinistra.
4. Creare una pagina di tipo **Template** per la navigazione e assegnarla alle pagine standard che devono usarla.
5. Inserire nella pagina iniziale o nel template un oggetto **Esci runtime**. Senza questo oggetto il runtime non viene avviato dall'editor.
6. Salvare il progetto e premere **RUNTIME**.

Il runtime non genera automaticamente un menu di pagina: tutta la navigazione è quella progettata tramite pulsanti di navigazione, template e pulsanti popup.

## Utenti e livelli di accesso

Nella scheda **UTENTI** si abilita la sicurezza e si configurano gli account, il livello di accesso, lo stato attivo, la lunghezza minima della password, il blocco dopo tentativi falliti, il logout automatico e la retention dello storico sessioni. Le password non vengono salvate in chiaro: il progetto conserva un hash PBKDF2 con salt univoco.

Ogni oggetto grafico espone il campo **Livello accesso richiesto**. In runtime un operatore con livello insufficiente vede l'oggetto disabilitato e non può eseguirne le azioni. Gli oggetti **Login** e **Logout** gestiscono la sessione; **Gestione utenti** permette a un amministratore autorizzato di creare, modificare, disabilitare ed eliminare account e di consultare login, logout e relativa causa. Sono impedite la rimozione dell'ultimo amministratore attivo e l'auto-elevazione oltre il livello dell'utente corrente.

Nel runtime esportato le modifiche agli utenti vengono conservate nell'area dati locale di Windows, anche quando la cartella applicativa è in sola lettura. Se sono presenti oggetti protetti, il progetto deve prevedere il login iniziale oppure contenere almeno un pulsante **Login**.

## Pagine e popup

Ogni pagina ha tipo, cartella, larghezza, altezza e colore di sfondo propri. Le pagine standard possono utilizzare un template, utile per creare menu, intestazioni e comandi comuni. Un oggetto **Apri popup** può puntare a una pagina di tipo **Popup**: il primo clic la apre e il clic successivo sullo stesso pulsante la chiude.

## Ricette runtime

L'oggetto ricette permette all'operatore di:

- creare, rinominare ed eliminare una ricetta;
- modificare direttamente ogni valore memorizzato;
- leggere i valori correnti dal PLC;
- scrivere tutti i valori della ricetta sul PLC.

Nel pacchetto runtime esportato le modifiche vengono salvate in `runtime.hmiproject`, così restano disponibili dopo il riavvio. La cartella estratta deve quindi essere scrivibile dall'utente Windows.

## Database MySQL

Nella scheda **DATABASE** si impostano server, porta, utente, password, database e tabella. È possibile provare il collegamento, leggere l'elenco dei database, creare un nuovo database e creare/verificare la tabella dello storico. Per ogni tag si sceglie se abilitarne il salvataggio e se registrarla:

- **OnChange**: solo quando cambia il valore;
- **Timed**: all'intervallo configurato in millisecondi.

Il campo di retention elimina i record più vecchi del numero di giorni impostato: la pulizia viene eseguita all'avvio, ogni 24 ore e, su richiesta, tramite il pulsante dedicato. Un errore MySQL non interrompe la comunicazione PLC né il funzionamento dell'interfaccia operatore.

L'oggetto **Storico dati** consente in runtime di scegliere database e tabella, applicare una ricerca testuale, impostare l'intervallo locale con data e ora e limitare le righe restituite. Nell'oggetto **Grafico linea** si possono aggiungere più serie: per ciascuna si selezionano la tag, il nome mostrato nella legenda runtime e il colore della linea. Tutte le serie possono usare i valori PLC live oppure essere lette dalla tabella storica configurata.

## Storico allarmi

L'oggetto **Storico allarmi** salva gli eventi in un file JSON affiancato al progetto. Se la cartella del pacchetto non è scrivibile, usa la cartella dati locale dell'utente Windows. Ogni evento conserva attivazione, risoluzione e riconoscimento; in runtime è possibile filtrare per testo, gravità, stato e intervallo di date. La proprietà di retention dell'oggetto determina per quanti giorni conservare gli allarmi risolti.

## Ridondanza

Nella scheda **RIDONDANZA** si definiscono i pannelli, il relativo indirizzo SignalR e la priorità. Un solo pannello deve essere marcato come locale. Il pannello con priorità più alta si collega direttamente ai PLC; i backup lavorano in standby attraverso il pannello superiore. In caso di perdita del collegamento il backup attende il ritardo configurato, verifica nuovamente i pannelli superiori e, se necessario, si promuove a pannello attivo. Quando un pannello superiore ritorna disponibile viene eseguito il failback.

Ogni pannello ridondante deve ricevere un proprio pacchetto runtime esportato dopo averlo marcato come pannello locale.

## Esportazione runtime

Il comando **Esporta runtime** crea un archivio ZIP contenente eseguibile, dipendenze e copia del progetto. Dopo l'estrazione sul PC cliente si avvia `HMI.exe`. La presenza del manifesto `runtime-package.json` forza l'applicazione ad aprire direttamente il runtime, senza editor e senza possibilità di tornare alla modalità sviluppo.

Il runtime si apre massimizzato, senza bordo, titolo, banner o menu automatici. La finestra principale ignora la normale richiesta di chiusura: deve essere presente un pulsante **Esci runtime** nel progetto. Se il runtime è stato avviato dall'editor, quel pulsante arresta le connessioni e torna allo sviluppo; nel pacchetto esportato chiude invece l'applicazione.

Il pacchetto corrente è framework-dependent e richiede **.NET 10 Desktop Runtime per Windows** sul PC cliente.

## Struttura principale

- `Models/HmiProject.cs`: schema persistente di progetto;
- `Function/ProjectStorageService.cs`: lettura e scrittura dei file `.hmiproject`;
- `Function/HmiRuntimeSession.cs`: connessioni, polling, scrittura tag, ridondanza e logging;
- `Function/MySqlDatabaseService.cs`: configurazione e scrittura dello storico MySQL;
- `Function/AlarmHistoryService.cs`: persistenza e retention dello storico allarmi;
- `Function/RuntimeExportService.cs`: generazione del pacchetto runtime;
- `MainHMI.xaml` e `MainHMI.xaml.cs`: editor visuale e renderer runtime;
- `ExternalConnection/PLCs/Simulator.cs`: driver locale per sviluppo e collaudo.

## Stato dei driver

Il collegamento Siemens S7 usa `S7netplus`. Il driver Codesys presente nel progetto è ancora un segnaposto: per un impianto reale deve essere completato scegliendo il protocollo esposto dal PLC, ad esempio OPC UA o Modbus TCP.
