using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using HMI.Models;
using MySqlConnector;

namespace HMI.Function;

public sealed partial class MySqlDatabaseService
{
    public async Task TestConnectionAsync(DatabaseSettings settings)
    {
        await using var connection = new MySqlConnection(BuildConnectionString(settings, false));
        await connection.OpenAsync();
    }

    public async Task<List<string>> GetDatabasesAsync(DatabaseSettings settings)
    {
        var result = new List<string>();
        await using var connection = new MySqlConnection(BuildConnectionString(settings, false));
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA " +
            "WHERE SCHEMA_NAME NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys') ORDER BY SCHEMA_NAME",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public async Task CreateDatabaseAsync(DatabaseSettings settings)
    {
        ValidateIdentifier(settings.DatabaseName, "database");
        await using var connection = new MySqlConnection(BuildConnectionString(settings, false));
        await connection.OpenAsync();
        var sql = $"CREATE DATABASE IF NOT EXISTS `{settings.DatabaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task CreateHistoryTableAsync(DatabaseSettings settings)
    {
        ValidateIdentifier(settings.DatabaseName, "database");
        ValidateIdentifier(settings.TableName, "tabella");
        await using var connection = new MySqlConnection(BuildConnectionString(settings, true));
        await connection.OpenAsync();
        var sql = $"""
            CREATE TABLE IF NOT EXISTS `{settings.TableName}` (
                `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                `tag_id` VARCHAR(64) NOT NULL,
                `tag_name` VARCHAR(255) NOT NULL,
                `value_text` TEXT NULL,
                `recorded_at_utc` DATETIME(6) NOT NULL,
                PRIMARY KEY (`id`),
                INDEX `idx_recorded_at` (`recorded_at_utc`),
                INDEX `idx_tag_time` (`tag_id`, `recorded_at_utc`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        await EnsureIndexAsync(connection, settings.TableName, "idx_recorded_at", "`recorded_at_utc`");
        await EnsureIndexAsync(connection, settings.TableName, "idx_tag_time", "`tag_id`, `recorded_at_utc`");
    }

    public async Task<List<string>> GetTablesAsync(DatabaseSettings settings, string databaseName)
    {
        ValidateIdentifier(databaseName, "database");
        var tables = new List<string>();
        await using var connection = new MySqlConnection(BuildConnectionString(settings, databaseName));
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SHOW TABLES", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    public async Task<DataTable> QueryTableAsync(DatabaseSettings settings, string databaseName, string tableName, HistoryQueryOptions options)
    {
        ValidateIdentifier(databaseName, "database");
        ValidateIdentifier(tableName, "tabella");
        await using var connection = new MySqlConnection(BuildConnectionString(settings, databaseName));
        await connection.OpenAsync();
        var columns = await GetColumnsAsync(connection, tableName);
        var conditions = new List<string>();
        await using var command = connection.CreateCommand();
        if (columns.Contains("recorded_at_utc") && options.FromUtc is not null)
        {
            conditions.Add("`recorded_at_utc` >= @fromUtc");
            command.Parameters.AddWithValue("@fromUtc", options.FromUtc.Value);
        }
        if (columns.Contains("recorded_at_utc") && options.ToUtc is not null)
        {
            conditions.Add("`recorded_at_utc` < @toUtc");
            command.Parameters.AddWithValue("@toUtc", options.ToUtc.Value);
        }
        if (!string.IsNullOrWhiteSpace(options.SearchText))
        {
            var searchable = await GetSearchableColumnsAsync(connection, tableName);
            if (searchable.Count > 0)
            {
                conditions.Add("(" + string.Join(" OR ", searchable.Select(column => $"CAST({QuoteIdentifier(column)} AS CHAR) LIKE @search")) + ")");
                command.Parameters.AddWithValue("@search", "%" + options.SearchText.Trim() + "%");
            }
        }
        var orderColumn = columns.Contains("recorded_at_utc") ? "recorded_at_utc" : columns.Contains("id") ? "id" : columns.FirstOrDefault();
        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        var order = orderColumn is null ? string.Empty : $" ORDER BY {QuoteIdentifier(orderColumn)} DESC";
        command.CommandText = $"SELECT * FROM `{tableName}`{where}{order} LIMIT {Math.Clamp(options.MaxRows, 1, 10000)}";
        await using var reader = await command.ExecuteReaderAsync();
        var table = new DataTable(tableName);
        await Task.Run(() => table.Load(reader));
        return table;
    }

    public async Task<List<HistoryDataPoint>> GetHistoryPointsAsync(
        DatabaseSettings settings,
        string databaseName,
        string tableName,
        string tagId,
        string tagName,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints)
    {
        ValidateIdentifier(databaseName, "database");
        ValidateIdentifier(tableName, "tabella");
        await using var connection = new MySqlConnection(BuildConnectionString(settings, databaseName));
        await connection.OpenAsync();
        var columns = await GetColumnsAsync(connection, tableName);
        if (!columns.Contains("recorded_at_utc") || !columns.Contains("value_text"))
        {
            throw new InvalidOperationException("La tabella non contiene le colonne recorded_at_utc e value_text richieste dal grafico storico.");
        }
        await using var command = connection.CreateCommand();
        var conditions = new List<string> { "`recorded_at_utc` >= @fromUtc", "`recorded_at_utc` <= @toUtc" };
        command.Parameters.AddWithValue("@fromUtc", fromUtc);
        command.Parameters.AddWithValue("@toUtc", toUtc);
        if (columns.Contains("tag_id") && !string.IsNullOrWhiteSpace(tagId))
        {
            conditions.Add("`tag_id` = @tagId");
            command.Parameters.AddWithValue("@tagId", tagId);
        }
        else if (columns.Contains("tag_name") && !string.IsNullOrWhiteSpace(tagName))
        {
            conditions.Add("`tag_name` = @tagName");
            command.Parameters.AddWithValue("@tagName", tagName);
        }
        command.CommandText = $"SELECT `recorded_at_utc`, `value_text` FROM `{tableName}` WHERE {string.Join(" AND ", conditions)} ORDER BY `recorded_at_utc` DESC LIMIT {Math.Clamp(maxPoints, 10, 10000)}";
        var points = new List<HistoryDataPoint>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var rawValue = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
            if (double.TryParse(rawValue.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue))
            {
                points.Add(new HistoryDataPoint(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), numericValue));
            }
        }
        points.Reverse();
        return points;
    }

    public async Task<int> DeleteOldRecordsAsync(
        DatabaseSettings settings,
        string databaseName,
        string tableName,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(databaseName, "database");
        ValidateIdentifier(tableName, "tabella");
        await using var connection = new MySqlConnection(BuildConnectionString(settings, databaseName));
        await connection.OpenAsync(cancellationToken);
        var columns = await GetColumnsAsync(connection, tableName, cancellationToken);
        if (!columns.Contains("recorded_at_utc"))
        {
            throw new InvalidOperationException("La tabella selezionata non contiene la colonna recorded_at_utc.");
        }
        var total = 0;
        var days = Math.Clamp(retentionDays, 1, 3650);
        int deleted;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = new MySqlCommand($"DELETE FROM `{tableName}` WHERE `recorded_at_utc` < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL {days} DAY) LIMIT 5000", connection);
            deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == 5000);
        return total;
    }

    public static string BuildConnectionString(DatabaseSettings settings, bool includeDatabase)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = settings.Host,
            Port = (uint)Math.Clamp(settings.Port, 1, 65535),
            UserID = settings.Username,
            Password = settings.Password,
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 10,
            Pooling = true
        };
        if (includeDatabase)
        {
            builder.Database = settings.DatabaseName;
        }
        return builder.ConnectionString;
    }

    private static string BuildConnectionString(DatabaseSettings settings, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(BuildConnectionString(settings, false)) { Database = databaseName };
        return builder.ConnectionString;
    }

    private static async Task EnsureIndexAsync(MySqlConnection connection, string tableName, string indexName, string indexedColumns)
    {
        ValidateIdentifier(tableName, "tabella");
        ValidateIdentifier(indexName, "indice");
        await using var checkCommand = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = @tableName AND index_name = @indexName",
            connection);
        checkCommand.Parameters.AddWithValue("@tableName", tableName);
        checkCommand.Parameters.AddWithValue("@indexName", indexName);
        if (Convert.ToInt32(await checkCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        try
        {
            await using var createCommand = new MySqlCommand($"ALTER TABLE `{tableName}` ADD INDEX `{indexName}` ({indexedColumns})", connection);
            await createCommand.ExecuteNonQueryAsync();
        }
        catch (MySqlException exception) when (exception.Number == 1061)
        {
            // Un altro runtime può aver creato lo stesso indice tra la verifica e l'ALTER TABLE.
        }
    }

    private static async Task<HashSet<string>> GetColumnsAsync(
        MySqlConnection connection,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(tableName, "tabella");
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new MySqlCommand($"SHOW COLUMNS FROM `{tableName}`", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }

    private static async Task<List<string>> GetSearchableColumnsAsync(MySqlConnection connection, string tableName)
    {
        ValidateIdentifier(tableName, "tabella");
        const string sql = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @tableName
              AND DATA_TYPE IN ('char', 'varchar', 'text', 'tinytext', 'mediumtext', 'longtext', 'enum', 'set',
                                'tinyint', 'smallint', 'mediumint', 'int', 'integer', 'bigint', 'decimal', 'numeric',
                                'float', 'double', 'real', 'bit', 'bool', 'boolean')
            ORDER BY ORDINAL_POSITION
            """;
        var columns = new List<string>();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }

    private static string QuoteIdentifier(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";

    public static void ValidateIdentifier(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifier().IsMatch(value))
        {
            throw new ArgumentException($"Il nome {label} può contenere solo lettere, numeri e underscore.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SafeIdentifier();
}

public sealed class MySqlTagLogger : IAsyncDisposable
{
    private readonly HmiProject _project;
    private readonly Channel<LogEntry> _queue = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, object> _latestValues = new();
    private readonly ConcurrentDictionary<string, string> _lastChangedValues = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTimedWrite = new();
    private CancellationTokenSource? _cancellation;
    private Task? _writerTask;
    private Task? _timedTask;
    private Task? _retentionTask;

    public MySqlTagLogger(HmiProject project)
    {
        _project = project;
    }

    public async Task<bool> StartAsync()
    {
        if (!_project.Database.Enabled)
        {
            return false;
        }
        try
        {
            await new MySqlDatabaseService().CreateHistoryTableAsync(_project.Database);
        }
        catch
        {
            return false;
        }
        try
        {
            await new MySqlDatabaseService().DeleteOldRecordsAsync(
                _project.Database,
                _project.Database.DatabaseName,
                _project.Database.TableName,
                _project.Database.RetentionDays);
        }
        catch
        {
            // La retention non deve impedire l'avvio della storicizzazione.
        }
        _cancellation = new CancellationTokenSource();
        _writerTask = WriterLoopAsync(_cancellation.Token);
        _timedTask = TimedLoopAsync(_cancellation.Token);
        _retentionTask = RetentionLoopAsync(_cancellation.Token);
        return true;
    }

    public void RecordTagValue(string tagId, object value)
    {
        _latestValues[tagId] = value;
        var configuration = _project.Database.TagLogging.FirstOrDefault(item => item.TagId == tagId && item.Enabled);
        if (configuration is null || configuration.Mode != DatabaseLoggingMode.OnChange)
        {
            return;
        }
        var serialized = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (_lastChangedValues.TryGetValue(tagId, out var previous) && previous == serialized)
        {
            return;
        }
        _lastChangedValues[tagId] = serialized;
        _queue.Writer.TryWrite(new LogEntry(tagId, serialized, DateTime.UtcNow));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null)
        {
            return;
        }
        await _cancellation.CancelAsync();
        _queue.Writer.TryComplete();
        try
        {
            await Task.WhenAll(new[] { _timedTask, _writerTask, _retentionTask }.Where(task => task is not null).Cast<Task>());
        }
        catch (OperationCanceledException)
        {
            // Arresto richiesto.
        }
        _cancellation.Dispose();
        _cancellation = null;
        _writerTask = null;
        _timedTask = null;
        _retentionTask = null;
    }

    private async Task RetentionLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await new MySqlDatabaseService().DeleteOldRecordsAsync(
                        _project.Database,
                        _project.Database.DatabaseName,
                        _project.Database.TableName,
                        _project.Database.RetentionDays,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // La retention verrà ritentata al ciclo successivo senza interrompere il logging.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Arresto runtime richiesto.
        }
    }

    private async Task TimedLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = DateTime.UtcNow;
            foreach (var configuration in _project.Database.TagLogging.Where(item => item.Enabled && item.Mode == DatabaseLoggingMode.Timed))
            {
                if (!_latestValues.TryGetValue(configuration.TagId, out var value))
                {
                    continue;
                }
                if (_lastTimedWrite.TryGetValue(configuration.TagId, out var lastWrite) &&
                    (now - lastWrite).TotalMilliseconds < Math.Max(100, configuration.IntervalMs))
                {
                    continue;
                }
                _lastTimedWrite[configuration.TagId] = now;
                _queue.Writer.TryWrite(new LogEntry(
                    configuration.TagId,
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    now));
            }
        }
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            var tag = _project.Tags.FirstOrDefault(item => item.Id == entry.TagId);
            if (tag is null)
            {
                continue;
            }
            try
            {
                MySqlDatabaseService.ValidateIdentifier(_project.Database.TableName, "tabella");
                await using var connection = new MySqlConnection(MySqlDatabaseService.BuildConnectionString(_project.Database, true));
                await connection.OpenAsync(cancellationToken);
                var sql = $"INSERT INTO `{_project.Database.TableName}` (`tag_id`, `tag_name`, `value_text`, `recorded_at_utc`) VALUES (@tagId, @tagName, @value, @timestamp)";
                await using var command = new MySqlCommand(sql, connection);
                command.Parameters.AddWithValue("@tagId", tag.Id);
                command.Parameters.AddWithValue("@tagName", tag.Name);
                command.Parameters.AddWithValue("@value", entry.Value);
                command.Parameters.AddWithValue("@timestamp", entry.TimestampUtc);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Il logging non deve bloccare il runtime; il valore successivo farà un nuovo tentativo.
            }
        }
    }

    private sealed record LogEntry(string TagId, string Value, DateTime TimestampUtc);
}

public sealed record HistoryQueryOptions(DateTime? FromUtc, DateTime? ToUtc, string SearchText, int MaxRows);
public sealed record HistoryDataPoint(DateTime TimestampUtc, double Value);
