using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;
using Sharp.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using SqlSugar;
using Timer.RequestManager.Replay;
using Timer.RequestManager.Storage;

namespace Timer.RequestManager;

public class SqlRequestManager : IModSharpModule
{
    private const string TimerConfigFileName            = "timer.jsonc";
    private const string TimerConfigDirectoryName       = "configs";
    private const string ConnectionStringKey            = "Timer";
    private const string ModuleConnectionStringKey      = "Timer.RequestManager";
    private const string ReplayStorageBaseUrlKey        = "Timer:ReplayStorageBaseUrl";
    private const string ReplayUploadNonPersonalBestKey = "Timer:ReplayUploadNonPersonalBest";

    private readonly ISharedSystem              _shared;
    private readonly ILogger<SqlRequestManager> _logger;

    private readonly IRequestManager   _impl;
    private readonly DbReplayProvider? _replayProvider;

    public SqlRequestManager(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        _shared = sharedSystem;
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<SqlRequestManager>();

        // Parse timer.jsonc once; both the DB and replay config readers consume it.
        var       configPath  = Path.Combine(sharpPath, TimerConfigDirectoryName, TimerConfigFileName);
        using var timerConfig = LoadTimerJsonc(configPath);

        var (dbType, connectionString, source) = ResolveDatabaseConnection(timerConfig, configPath, configuration);

        _logger.LogInformation("Resolved SQL config from {source}.", source);

        var storageImpl = new StorageServiceImpl(dbType,
                                                 connectionString,
                                                 sharedSystem.GetLoggerFactory().CreateLogger<StorageServiceImpl>());
        _impl = storageImpl;

        var replayConfig = ResolveReplayConfig(timerConfig, configPath, configuration);

        if (string.IsNullOrWhiteSpace(replayConfig.BaseUrl))
        {
            _logger.LogInformation("Replay storage URL not configured (source={source}). Remote replay upload disabled.",
                                   replayConfig.Source);
            _replayProvider = null;
        }
        else
        {
            _logger.LogInformation("Resolved replay storage URL from {source} (uploadNonPersonalBest={flag}).",
                                   replayConfig.Source,
                                   replayConfig.UploadNonPersonalBest);

            var replayStorage = new HttpReplayStorage(new HttpClient(), replayConfig.BaseUrl);
            _replayProvider = new DbReplayProvider(storageImpl,
                                                   replayStorage,
                                                   replayConfig.UploadNonPersonalBest,
                                                   sharedSystem.GetLoggerFactory().CreateLogger<DbReplayProvider>());
        }
    }

    public bool Init()
    {
        try
        {
            ((StorageServiceImpl) _impl).Init();

            _logger.LogInformation("{module} initialized with SQL storage.",
                                   ((IModSharpModule) this).DisplayName);

            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to initialize SQL request manager.");

            return false;
        }
    }

    public void PostInit()
    {
        _shared.GetSharpModuleManager().RegisterSharpModuleInterface(this, IRequestManager.Identity, _impl);

        if (_replayProvider is not null)
        {
            _shared.GetSharpModuleManager().RegisterSharpModuleInterface<IReplayProvider>(this, IReplayProvider.Identity, _replayProvider);
        }
    }

    public void Shutdown()
    {
        ((StorageServiceImpl) _impl).Shutdown();
    }

    private static JsonDocument? LoadTimerJsonc(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        using var stream = File.OpenRead(configPath);

        return JsonDocument.Parse(stream,
            new JsonDocumentOptions
            {
                CommentHandling     = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
    }

    private static (DbType DbType, string ConnectionString, string Source) ResolveDatabaseConnection(
        JsonDocument?  timerConfig,
        string         configPath,
        IConfiguration configuration)
    {
        if (timerConfig is not null)
        {
            var (dbType, connectionString) = ParseTimerJsonc(timerConfig.RootElement, configPath);
            return (dbType, connectionString, configPath);
        }

        var rawConnectionString = ResolveConnectionString(configuration);
        var parsed = ParseConnectionString(rawConnectionString);

        return (parsed.DbType, parsed.ConnectionString, "IConfiguration:ConnectionStrings");
    }

    private readonly record struct ReplayConfig(string BaseUrl, bool UploadNonPersonalBest, string Source);

    private static ReplayConfig ResolveReplayConfig(JsonDocument? timerConfig, string configPath, IConfiguration configuration)
    {
        if (timerConfig is not null)
        {
            var parsed = ParseReplayConfigFromTimerJsonc(timerConfig.RootElement, configPath);

            if (!string.IsNullOrWhiteSpace(parsed.BaseUrl))
            {
                return parsed with { Source = configPath };
            }
        }

        var fallbackUrl                  = configuration[ReplayStorageBaseUrlKey];
        var fallbackUploadNonBestRaw     = configuration[ReplayUploadNonPersonalBestKey];
        var fallbackUploadNonBest        = !string.IsNullOrWhiteSpace(fallbackUploadNonBestRaw)
                                        && bool.TryParse(fallbackUploadNonBestRaw, out var parsedBool)
                                        && parsedBool;

        if (!string.IsNullOrWhiteSpace(fallbackUrl))
        {
            return new ReplayConfig(fallbackUrl, fallbackUploadNonBest, $"IConfiguration:{ReplayStorageBaseUrlKey}");
        }

        return new ReplayConfig(string.Empty, false, "none");
    }

    private static (DbType DbType, string ConnectionString) ParseTimerJsonc(JsonElement root, string configPath)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(root, "database", out var database)
            || database.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Missing 'database' object in {configPath}.");
        }

        var type         = ReadRequiredString(database, "type");
        var host         = ReadRequiredString(database, "host");
        var databaseName = ReadRequiredString(database, "database_name", "databaseName", "database");
        var userName     = ReadRequiredString(database, "username", "user", "uid");
        var password     = ReadRequiredString(database, "password", "pwd");
        var port         = ReadOptionalInt(database, "port");

        var dbType = ResolveDbType(type);

        return dbType switch
        {
            DbType.MySql => (dbType, BuildMySqlConnectionString(host, databaseName, userName, password, port)),
            DbType.PostgreSQL => (dbType, BuildPostgreSqlConnectionString(host, databaseName, userName, password, port)),
            _ => throw new NotSupportedException($"Unsupported database type '{type}'. Use mysql or postgresql."),
        };
    }

    private static ReplayConfig ParseReplayConfigFromTimerJsonc(JsonElement root, string configPath)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Invalid root object in {configPath}.");
        }

        if (!TryGetPropertyIgnoreCase(root, "replay", out var replaySection)
            || replaySection.ValueKind != JsonValueKind.Object)
        {
            return new ReplayConfig(string.Empty, false, configPath);
        }

        var storageBaseUrl = ReadOptionalString(replaySection,
                                                "storage_base_url",
                                                "storageBaseUrl");

        var uploadNonBest  = ReadOptionalBool(replaySection,
                                              "upload_non_personal_best",
                                              "uploadNonPersonalBest")
                            ?? false;

        return new ReplayConfig(storageBaseUrl ?? string.Empty, uploadNonBest, configPath);
    }

    private static string ResolveConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ModuleConnectionStringKey)
                               ?? configuration.GetConnectionString(ConnectionStringKey);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new
                KeyNotFoundException($"Missing '{ModuleConnectionStringKey}' or '{ConnectionStringKey}' in connection strings.");
        }

        return connectionString;
    }

    private static (DbType DbType, string ConnectionString) ParseConnectionString(string rawConnectionString)
    {
        var parts = rawConnectionString.Split("://", 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            throw new InvalidDataException("Missing database type in connection string (expected '{schema}://{connection}').");
        }

        var schema     = parts[0].Trim().ToLowerInvariant();
        var connection = parts[1];

        var dbType = schema switch
        {
            "mysql" or "mariadb" => DbType.MySql,
            "pgsql" or "postgres" or "postgresql" => DbType.PostgreSQL,
            _ => throw new NotSupportedException($"Unsupported database type '{schema}'. Use mysql or pgsql."),
        };

        return (dbType, connection);
    }

    private static DbType ResolveDbType(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "mysql" or "mariadb" => DbType.MySql,
            "pgsql" or "postgres" or "postgresql" => DbType.PostgreSQL,
            _ => throw new NotSupportedException($"Unsupported database type '{type}'. Use mysql or postgresql."),
        };
    }

    private static string BuildMySqlConnectionString(string host,
                                                     string databaseName,
                                                     string userName,
                                                     string password,
                                                     int?   port)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server   = host,
            Database = databaseName,
            UserID   = userName,
            Password = password,
            Port     = (uint)(port ?? 3306),
        };

        return builder.ConnectionString;
    }

    private static string BuildPostgreSqlConnectionString(string host,
                                                          string databaseName,
                                                          string userName,
                                                          string password,
                                                          int?   port)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host     = host,
            Database = databaseName,
            Username = userName,
            Password = password,
            Port     = port ?? 5432,
        };

        return builder.ConnectionString;
    }

    private static string ReadRequiredString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.GetString();

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidDataException($"Missing required database field: {string.Join(" / ", propertyNames)}.");
    }

    private static string? ReadOptionalString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
            {
                continue;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Invalid string field for config: {name}.");
            }

            var value = property.GetString();

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool? ReadOptionalBool(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
            {
                continue;
            }

            switch (property.ValueKind)
            {
                case JsonValueKind.True:  return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed):
                    return parsed;
                default:
                    throw new InvalidDataException($"Invalid bool field for config: {name}.");
            }
        }

        return null;
    }

    private static int? ReadOptionalInt(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), out intValue))
            {
                return intValue;
            }

            throw new InvalidDataException($"Invalid integer field for database config: {name}.");
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value;
            return true;
        }

        value = default;

        return false;
    }

    string IModSharpModule.DisplayName   => "[Timer] RequestManager - SQL";
    string IModSharpModule.DisplayAuthor => "Nukoooo";
}
