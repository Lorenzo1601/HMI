using System.Text.Json.Serialization;

namespace HMI.Models;

public sealed class HmiProject
{
    public int SchemaVersion { get; set; } = 4;
    public string Name { get; set; } = "Nuovo progetto";
    public double CanvasWidth { get; set; } = 1280;
    public double CanvasHeight { get; set; } = 720;
    public string StartupPageId { get; set; } = string.Empty;
    public List<PageFolderDefinition> PageFolders { get; set; } = [];
    public List<HmiPageDefinition> Pages { get; set; } = [];
    public List<PlcConnectionDefinition> PlcConnections { get; set; } = [];
    public List<TagFolderDefinition> TagFolders { get; set; } = [];
    public List<TagDefinition> Tags { get; set; } = [];
    public List<RecipeBookDefinition> RecipeBooks { get; set; } = [];
    public List<AlarmDefinition> Alarms { get; set; } = [];
    public List<AlarmFolderDefinition> AlarmFolders { get; set; } = [];
    public List<ProjectAssetDefinition> Assets { get; set; } = [];
    public RedundancySettings Redundancy { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();

    public void Normalize()
    {
        SchemaVersion = Math.Max(SchemaVersion, 4);
        PageFolders ??= [];
        Pages ??= [];
        PlcConnections ??= [];
        TagFolders ??= [];
        Tags ??= [];
        RecipeBooks ??= [];
        Alarms ??= [];
        AlarmFolders ??= [];
        Assets ??= [];
        Redundancy ??= new RedundancySettings();
        Redundancy.Panels ??= [];
        Database ??= new DatabaseSettings();
        Database.TagLogging ??= [];
        Database.RetentionDays = Math.Clamp(Database.RetentionDays, 1, 3650);
        Security ??= new SecuritySettings();
        Security.Users ??= [];
        Security.MaximumAccessLevel = Math.Clamp(Security.MaximumAccessLevel, 1, 1000);
        Security.AnonymousAccessLevel = Math.Clamp(Security.AnonymousAccessLevel, 0, Security.MaximumAccessLevel);
        Security.MinimumPasswordLength = Math.Clamp(Security.MinimumPasswordLength, 8, 128);
        Security.MaximumFailedLoginAttempts = Math.Clamp(Security.MaximumFailedLoginAttempts, 1, 20);
        Security.LoginLockoutMinutes = Math.Clamp(Security.LoginLockoutMinutes, 1, 24 * 60);
        Security.SessionHistoryRetentionDays = Math.Clamp(Security.SessionHistoryRetentionDays, 1, 3650);
        Security.AutomaticLogoutMinutes = Math.Clamp(Security.AutomaticLogoutMinutes, 0, 24 * 60);

        var userIds = new HashSet<string>(StringComparer.Ordinal);
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Security.Users.Count; index++)
        {
            var user = Security.Users[index];
            if (string.IsNullOrWhiteSpace(user.Id) || !userIds.Add(user.Id))
            {
                user.Id = Guid.NewGuid().ToString("N");
                userIds.Add(user.Id);
            }

            var baseUsername = string.IsNullOrWhiteSpace(user.Username) ? $"utente{index + 1}" : user.Username.Trim();
            var uniqueUsername = baseUsername;
            var suffix = 2;
            while (!usernames.Add(uniqueUsername))
            {
                uniqueUsername = $"{baseUsername}_{suffix++}";
            }
            user.Username = uniqueUsername;
            user.DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? uniqueUsername : user.DisplayName.Trim();
            user.AccessLevel = Math.Clamp(user.AccessLevel, 0, Security.MaximumAccessLevel);
            user.PasswordAlgorithm = string.IsNullOrWhiteSpace(user.PasswordAlgorithm)
                ? UserDefinition.DefaultPasswordAlgorithm
                : user.PasswordAlgorithm.Trim();
            user.PasswordIterations = Math.Clamp(user.PasswordIterations, 10_000, 2_000_000);
            user.PasswordSalt = user.PasswordSalt?.Trim() ?? string.Empty;
            user.PasswordHash = user.PasswordHash?.Trim() ?? string.Empty;
            user.FailedLoginAttempts = Math.Clamp(user.FailedLoginAttempts, 0, Security.MaximumFailedLoginAttempts);
            if (user.LockedUntilUtc is DateTime lockedUntilUtc)
            {
                user.LockedUntilUtc = lockedUntilUtc.Kind switch
                {
                    DateTimeKind.Utc => lockedUntilUtc,
                    DateTimeKind.Local => lockedUntilUtc.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(lockedUntilUtc, DateTimeKind.Utc)
                };
                if (user.LockedUntilUtc <= DateTime.UtcNow)
                {
                    user.LockedUntilUtc = null;
                    user.FailedLoginAttempts = 0;
                }
                else
                {
                    user.FailedLoginAttempts = Security.MaximumFailedLoginAttempts;
                }
            }
        }

        if (Pages.Count == 0)
        {
            var page = new HmiPageDefinition { Name = "Pagina 1" };
            Pages.Add(page);
            StartupPageId = page.Id;
        }

        if (Pages.All(page => page.Type != HmiPageType.Standard))
        {
            var page = new HmiPageDefinition { Name = "Pagina runtime" };
            Pages.Insert(0, page);
            StartupPageId = page.Id;
        }

        if (string.IsNullOrWhiteSpace(StartupPageId) ||
            Pages.All(page => page.Id != StartupPageId || page.Type != HmiPageType.Standard))
        {
            StartupPageId = Pages.First(page => page.Type == HmiPageType.Standard).Id;
        }

        foreach (var page in Pages)
        {
            page.Widgets ??= [];
            if (page.Width <= 0)
            {
                page.Width = CanvasWidth;
            }
            if (page.Height <= 0)
            {
                page.Height = CanvasHeight;
            }
            foreach (var widget in page.Widgets)
            {
                widget.Animation ??= new HmiAnimationDefinition();
                widget.ChartSeries ??= [];
                if (widget.Type == HmiWidgetType.Indicator)
                {
                    widget.Animation.Enabled = true;
                    if (string.IsNullOrWhiteSpace(widget.Animation.TagId))
                    {
                        widget.Animation.TagId = widget.TagId;
                    }
                    widget.TagId = widget.Animation.TagId;
                }
                widget.RequiredAccessLevel = Math.Clamp(widget.RequiredAccessLevel, 0, Security.MaximumAccessLevel);
                if (widget.Type is HmiWidgetType.LoginButton or HmiWidgetType.LogoutButton)
                {
                    widget.RequiredAccessLevel = 0;
                }
                widget.HistoryMaxRows = Math.Clamp(widget.HistoryMaxRows, 10, 10000);
                widget.HistoryHours = Math.Clamp(widget.HistoryHours, 1, 24 * 365);
                widget.AlarmHistoryRetentionDays = Math.Clamp(widget.AlarmHistoryRetentionDays, 1, 3650);
                if (widget.Type == HmiWidgetType.TrendChart && widget.ChartSeries.Count == 0 && !string.IsNullOrWhiteSpace(widget.TagId))
                {
                    var legacyTag = Tags.FirstOrDefault(tag => tag.Id == widget.TagId);
                    widget.ChartSeries.Add(new ChartSeriesDefinition
                    {
                        TagId = widget.TagId,
                        DisplayName = legacyTag?.Name ?? "Serie 1",
                        Color = string.IsNullOrWhiteSpace(widget.Foreground) ? "#28C2B8" : widget.Foreground
                    });
                }
                var chartSeriesIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var series in widget.ChartSeries)
                {
                    if (string.IsNullOrWhiteSpace(series.Id) || !chartSeriesIds.Add(series.Id))
                    {
                        series.Id = Guid.NewGuid().ToString("N");
                        chartSeriesIds.Add(series.Id);
                    }
                    var tag = Tags.FirstOrDefault(item => item.Id == series.TagId);
                    series.DisplayName = string.IsNullOrWhiteSpace(series.DisplayName) ? tag?.Name ?? "Serie" : series.DisplayName.Trim();
                    series.Color = string.IsNullOrWhiteSpace(series.Color) ? "#28C2B8" : series.Color.Trim();
                }
                if (widget.Type == HmiWidgetType.TrendChart)
                {
                    widget.TagId = widget.ChartSeries.FirstOrDefault()?.TagId ?? string.Empty;
                }
            }
        }

        foreach (var book in RecipeBooks)
        {
            book.TagIds ??= [];
            book.Recipes ??= [];
            foreach (var recipe in book.Recipes)
            {
                recipe.Values ??= [];
            }
        }
    }

    public static HmiProject CreateStarter()
    {
        var simulator = new PlcConnectionDefinition
        {
            Name = "PLC_SIM",
            Driver = PlcDriver.Simulator,
            Host = "locale",
            Port = 0
        };

        var runningTag = new TagDefinition
        {
            Name = "Motore_Attivo",
            PlcId = simulator.Id,
            Address = "Motor.Running",
            DataType = TagDataType.Bool,
            Access = TagAccess.ReadWrite,
            Description = "Stato marcia motore"
        };
        var speedTag = new TagDefinition
        {
            Name = "Velocita_Motore",
            PlcId = simulator.Id,
            Address = "Motor.Speed",
            DataType = TagDataType.Real,
            Access = TagAccess.ReadWrite,
            Description = "Velocità istantanea"
        };
        var temperatureTag = new TagDefinition
        {
            Name = "Temperatura",
            PlcId = simulator.Id,
            Address = "Process.Temperature",
            DataType = TagDataType.Real,
            Access = TagAccess.Read,
            Description = "Temperatura processo"
        };

        var processFolder = new TagFolderDefinition { Name = "Processo" };
        var motorFolder = new TagFolderDefinition { Name = "Motore", ParentFolderId = processFolder.Id };
        runningTag.FolderId = motorFolder.Id;
        speedTag.FolderId = motorFolder.Id;
        temperatureTag.FolderId = processFolder.Id;

        var motorRecipes = new RecipeBookDefinition
        {
            Name = "Ricette motore",
            TagIds = [speedTag.Id],
            Recipes =
            [
                new RecipeSetDefinition { Name = "Produzione standard", Values = { [speedTag.Id] = "1380" } },
                new RecipeSetDefinition { Name = "Produzione lenta", Values = { [speedTag.Id] = "900" } }
            ]
        };

        var highTemperatureAlarm = new AlarmDefinition
        {
            Name = "Temperatura elevata",
            TagId = temperatureTag.Id,
            Condition = AlarmCondition.GreaterThan,
            TriggerValue = "67",
            Severity = AlarmSeverity.Warning,
            Message = "Temperatura processo superiore al limite",
            RequiresAcknowledgement = true
        };

        var overview = new HmiPageDefinition { Name = "Panoramica", Background = "#101821" };
        var detail = new HmiPageDefinition { Name = "Dettaglio motore", Background = "#101821" };
        var mainTemplate = new HmiPageDefinition
        {
            Name = "Template principale",
            Background = "Transparent",
            Type = HmiPageType.Template
        };
        overview.TemplatePageId = mainTemplate.Id;
        detail.TemplatePageId = mainTemplate.Id;

        overview.Widgets.AddRange([
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Label,
                Name = "Titolo",
                Text = "LINEA DI PRODUZIONE 01",
                X = 54,
                Y = 42,
                Width = 500,
                Height = 56,
                FontSize = 30,
                Foreground = "#F1F5F9",
                Background = "Transparent"
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Indicator,
                Name = "Stato motore",
                Text = string.Empty,
                TagId = runningTag.Id,
                X = 58,
                Y = 142,
                Width = 86,
                Height = 86,
                Background = "Transparent",
                Animation = new HmiAnimationDefinition
                {
                    Enabled = true,
                    TagId = runningTag.Id,
                    Condition = AlarmCondition.True,
                    ActiveBackground = "#22C78A",
                    InactiveBackground = "#526273"
                }
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.ValueDisplay,
                Name = "Velocità",
                Text = "VELOCITÀ",
                TagId = speedTag.Id,
                X = 346,
                Y = 142,
                Width = 260,
                Height = 150,
                Suffix = " rpm",
                Decimals = 0
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.ValueDisplay,
                Name = "Temperatura",
                Text = "TEMPERATURA",
                TagId = temperatureTag.Id,
                X = 634,
                Y = 142,
                Width = 260,
                Height = 150,
                Suffix = " °C",
                Decimals = 1
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Button,
                Name = "Avvio motore",
                Text = "AVVIA MOTORE",
                TagId = runningTag.Id,
                WriteValue = "true",
                X = 58,
                Y = 330,
                Width = 260,
                Height = 68,
                Background = "#12B981"
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Button,
                Name = "Arresto motore",
                Text = "ARRESTA MOTORE",
                TagId = runningTag.Id,
                WriteValue = "false",
                X = 346,
                Y = 330,
                Width = 260,
                Height = 68,
                Background = "#EF5B5B"
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Navigation,
                Name = "Vai a dettaglio",
                Text = "APRI DETTAGLIO  →",
                TargetPageId = detail.Id,
                X = 922,
                Y = 588,
                Width = 280,
                Height = 68,
                Background = "#227CFF"
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.AlarmViewer,
                Name = "Allarmi attivi",
                Text = "ALLARMI ATTIVI",
                X = 58,
                Y = 440,
                Width = 548,
                Height = 190,
                Background = "#17212C"
            }
        ]);

        detail.Widgets.AddRange([
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Label,
                Name = "Titolo dettaglio",
                Text = "DETTAGLIO MOTORE",
                X = 54,
                Y = 42,
                Width = 500,
                Height = 56,
                FontSize = 30,
                Foreground = "#F1F5F9",
                Background = "Transparent"
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.NumericInput,
                Name = "Setpoint velocità",
                Text = "SETPOINT VELOCITÀ",
                TagId = speedTag.Id,
                X = 58,
                Y = 142,
                Width = 320,
                Height = 112,
                Suffix = " rpm",
                Decimals = 0
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Navigation,
                Name = "Torna alla panoramica",
                Text = "←  PANORAMICA",
                TargetPageId = overview.Id,
                X = 58,
                Y = 588,
                Width = 250,
                Height = 68,
                Background = "#334155"
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.RecipeManager,
                Name = "Gestione ricette",
                Text = "RICETTE MOTORE",
                RecipeBookId = motorRecipes.Id,
                X = 430,
                Y = 142,
                Width = 650,
                Height = 360,
                Background = "#17212C"
            }
        ]);

        mainTemplate.Widgets.AddRange([
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Navigation,
                Name = "Menu panoramica",
                Text = "PANORAMICA",
                TargetPageId = overview.Id,
                X = 430,
                Y = 664,
                Width = 190,
                Height = 46,
                Background = "#263646"
            },
            new HmiWidgetDefinition
            {
                Type = HmiWidgetType.Navigation,
                Name = "Menu dettaglio",
                Text = "DETTAGLIO",
                TargetPageId = detail.Id,
                X = 630,
                Y = 664,
                Width = 190,
                Height = 46,
                Background = "#263646"
            }
        ]);

        return new HmiProject
        {
            Name = "HMI Linea 01",
            StartupPageId = overview.Id,
            Pages = [overview, detail, mainTemplate],
            PlcConnections = [simulator],
            TagFolders = [processFolder, motorFolder],
            Tags = [runningTag, speedTag, temperatureTag],
            RecipeBooks = [motorRecipes],
            Alarms = [highTemperatureAlarm],
            Redundancy = new RedundancySettings
            {
                Panels =
                [
                    new RedundantPanelDefinition { Name = "Pannello principale", Host = "192.168.0.50", Priority = 1, IsLocal = true },
                    new RedundantPanelDefinition { Name = "Pannello backup", Host = "192.168.0.51", Priority = 2 }
                ]
            }
        };
    }
}

public sealed class HmiPageDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nuova pagina";
    public string Background { get; set; } = "#101821";
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 720;
    public string FolderId { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HmiPageType Type { get; set; } = HmiPageType.Standard;
    public string TemplatePageId { get; set; } = string.Empty;
    public List<HmiWidgetDefinition> Widgets { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class PageFolderDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nuova cartella";
    public string ParentFolderId { get; set; } = string.Empty;
}

public sealed class HmiWidgetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HmiWidgetType Type { get; set; }
    public string Name { get; set; } = "Oggetto";
    public string Text { get; set; } = "Testo";
    public double X { get; set; } = 40;
    public double Y { get; set; } = 40;
    public double Width { get; set; } = 220;
    public double Height { get; set; } = 72;
    public double FontSize { get; set; } = 16;
    public bool ShowDescription { get; set; } = true;
    public bool ShowBackground { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HmiTextAlignment TextAlignment { get; set; } = HmiTextAlignment.Default;
    public string Foreground { get; set; } = "#F8FAFC";
    public string Background { get; set; } = "#253244";
    public int RequiredAccessLevel { get; set; }
    public string TagId { get; set; } = string.Empty;
    public string TargetPageId { get; set; } = string.Empty;
    public string RecipeBookId { get; set; } = string.Empty;
    public string ImageAssetId { get; set; } = string.Empty;
    public bool UseImageAsContent { get; set; }
    public string ImageStretch { get; set; } = "Uniform";
    public HmiAnimationDefinition Animation { get; set; } = new();
    public string WriteValue { get; set; } = "true";
    public string Suffix { get; set; } = string.Empty;
    public int Decimals { get; set; } = 1;
    public string HistoryDatabaseName { get; set; } = string.Empty;
    public string HistoryTableName { get; set; } = string.Empty;
    public int HistoryHours { get; set; } = 24;
    public int HistoryMaxRows { get; set; } = 500;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ChartDataSource ChartSource { get; set; } = ChartDataSource.LivePlc;
    public List<ChartSeriesDefinition> ChartSeries { get; set; } = [];
    public int AlarmHistoryRetentionDays { get; set; } = 90;
}

public sealed class ChartSeriesDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TagId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "Serie";
    public string Color { get; set; } = "#28C2B8";

    public override string ToString() => DisplayName;
}

public sealed class PlcConnectionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "PLC_1";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlcDriver Driver { get; set; } = PlcDriver.SiemensS7;
    public string Host { get; set; } = "192.168.0.10";
    public int Port { get; set; } = 102;
    public string CpuType { get; set; } = "S71500";
    public short Rack { get; set; }
    public short Slot { get; set; } = 1;

    public override string ToString() => Name;
}

public sealed class TagDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "NuovoTag";
    public string PlcId { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    public string Address { get; set; } = "DB1.DBX0.0";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TagDataType DataType { get; set; } = TagDataType.Bool;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TagAccess Access { get; set; } = TagAccess.ReadWrite;
    public int PollIntervalMs { get; set; } = 500;
    public string Description { get; set; } = string.Empty;

    public override string ToString() => Name;
}

public sealed class TagFolderDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nuova cartella";
    public string ParentFolderId { get; set; } = string.Empty;
}

public sealed class RecipeBookDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nuovo ricettario";
    public List<string> TagIds { get; set; } = [];
    public List<RecipeSetDefinition> Recipes { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class RecipeSetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nuova ricetta";
    public Dictionary<string, string> Values { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class AlarmDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nuovo allarme";
    public string TagId { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlarmCondition Condition { get; set; } = AlarmCondition.True;
    public string TriggerValue { get; set; } = "true";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlarmSeverity Severity { get; set; } = AlarmSeverity.Warning;
    public string Message { get; set; } = "Allarme attivo";
    public bool RequiresAcknowledgement { get; set; } = true;
}

public sealed class AlarmFolderDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nuova cartella";
    public string ParentFolderId { get; set; } = string.Empty;
}

public sealed class ProjectAssetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Immagine";
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "image/png";
    public string DataBase64 { get; set; } = string.Empty;

    public override string ToString() => Name;
}

public sealed class HmiAnimationDefinition
{
    public bool Enabled { get; set; }
    public string TagId { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlarmCondition Condition { get; set; } = AlarmCondition.True;
    public string CompareValue { get; set; } = "true";
    public string ActiveBackground { get; set; } = "#12B981";
    public string InactiveBackground { get; set; } = "#253244";
    public string ActiveForeground { get; set; } = "#F8FAFC";
    public string InactiveForeground { get; set; } = "#8FA0B3";
}

public sealed class RedundancySettings
{
    public bool Enabled { get; set; }
    public int FailoverDelayMs { get; set; } = 2000;
    public int HealthCheckIntervalMs { get; set; } = 5000;
    public List<RedundantPanelDefinition> Panels { get; set; } = [];
}

public sealed class RedundantPanelDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Pannello HMI";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5000;
    public int Priority { get; set; } = 1;
    public bool IsLocal { get; set; }
}

public sealed class DatabaseSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string Username { get; set; } = "root";
    public string Password { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = "hmi";
    public string TableName { get; set; } = "tag_history";
    public int RetentionDays { get; set; } = 90;
    public List<DatabaseTagLoggingDefinition> TagLogging { get; set; } = [];
}

public sealed class DatabaseTagLoggingDefinition
{
    public string TagId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DatabaseLoggingMode Mode { get; set; } = DatabaseLoggingMode.OnChange;
    public int IntervalMs { get; set; } = 1000;
}

public sealed class SecuritySettings
{
    public bool Enabled { get; set; }
    public bool RequireLoginAtStartup { get; set; }
    public int AnonymousAccessLevel { get; set; }
    public int MaximumAccessLevel { get; set; } = 100;
    public int MinimumPasswordLength { get; set; } = 8;
    public int MaximumFailedLoginAttempts { get; set; } = 5;
    public int LoginLockoutMinutes { get; set; } = 5;
    public int AutomaticLogoutMinutes { get; set; }
    public int SessionHistoryRetentionDays { get; set; } = 365;
    public List<UserDefinition> Users { get; set; } = [];
}

public sealed class UserDefinition
{
    public const string DefaultPasswordAlgorithm = "PBKDF2-SHA256";
    public const int DefaultPasswordIterations = 210_000;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int AccessLevel { get; set; }
    public bool IsActive { get; set; } = true;
    public string PasswordAlgorithm { get; set; } = DefaultPasswordAlgorithm;
    public int PasswordIterations { get; set; } = DefaultPasswordIterations;
    public string PasswordSalt { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PasswordChangedAtUtc { get; set; }

    [JsonIgnore]
    public string StatusLabel => IsActive ? "Attivo" : "Disabilitato";

    [JsonIgnore]
    public string RuntimeSummary =>
        $"{(string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName)} · {Username} · livello {AccessLevel} · {StatusLabel}";

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

public enum HmiWidgetType
{
    Label,
    Button,
    ValueDisplay,
    Indicator,
    NumericInput,
    Navigation,
    RecipeManager,
    AlarmViewer,
    AlarmHistoryViewer,
    DataHistoryViewer,
    TrendChart,
    Image,
    PopupButton,
    PopupClose,
    RuntimeExit,
    UserManager,
    LoginButton,
    LogoutButton
}

public enum HmiPageType
{
    Standard,
    Template,
    Popup
}

public enum PlcDriver
{
    Simulator,
    SiemensS7,
    Codesys
}

public enum TagDataType
{
    Bool,
    Int,
    DInt,
    Real,
    String
}

public enum TagAccess
{
    Read,
    Write,
    ReadWrite
}

public enum AlarmCondition
{
    True,
    False,
    Equals,
    NotEquals,
    GreaterThan,
    LessThan
}

public enum AlarmSeverity
{
    Information,
    Warning,
    Critical
}

public enum DatabaseLoggingMode
{
    OnChange,
    Timed
}

public enum ChartDataSource
{
    LivePlc,
    HistoricalDatabase
}

public enum HmiTextAlignment
{
    Default,
    Left,
    Center,
    Right
}
