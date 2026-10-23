using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace PMS.DataAccess.Persistence;

/// <summary>
/// Resolves a generic database connection string dynamically per developer machine:
/// 1. Replaces {UserName} / {User} with sanitized Environment.UserName so each developer gets their own database.
/// 2. Replaces {MachineName} with Environment.MachineName.
/// 3. If AutoDetectLocalServer is enabled (or server is unreachable), automatically probes standard local SQL Server
///    instances ((localdb)\mssqllocaldb, ., localhost, .\SQLEXPRESS, localhost\SQLEXPRESS, (local)) to connect without manual editing.
/// </summary>
public static class DatabaseConnectionStringResolver
{
    private static readonly string[] CandidateLocalServers =
    [
        @"(localdb)\mssqllocaldb",
        @".",
        @"localhost",
        @".\SQLEXPRESS",
        @"localhost\SQLEXPRESS",
        @"(local)",
        @"127.0.0.1"
    ];

    private static readonly object Lock = new();
    private static string _cachedResolvedConnectionString;

    /// <summary>
    /// Resolves the effective connection string for the current developer environment.
    /// Result is cached for the process lifetime so probing only happens once.
    /// </summary>
    public static string Resolve(DatabaseConfiguration config)
    {
        if (config == null) return string.Empty;
        if (config.UseInMemoryDatabase) return string.Empty;

        lock (Lock)
        {
            if (!string.IsNullOrEmpty(_cachedResolvedConnectionString))
            {
                return _cachedResolvedConnectionString;
            }

            var raw = Environment.GetEnvironmentVariable("PMS_CONNECTION_STRING")
                      ?? Environment.GetEnvironmentVariable("Database__ConnectionString")
                      ?? config.ConnectionString;

            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = @"Server=(localdb)\mssqllocaldb;Database=parking_management_system;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True;";
            }

            var resolved = ResolveInternal(raw, config.UsePerUserDatabase, config.AutoDetectLocalServer);
            _cachedResolvedConnectionString = resolved;
            return resolved;
        }
    }

    private static string ResolveInternal(string connectionString, bool usePerUserDatabase, bool autoDetectServer)
    {
        var userName = SanitizeSqlIdentifier(Environment.UserName);
        var machineName = SanitizeSqlIdentifier(Environment.MachineName);

        // Replace literal placeholders if present
        connectionString = connectionString
            .Replace("{UserName}", userName, StringComparison.OrdinalIgnoreCase)
            .Replace("{User}", userName, StringComparison.OrdinalIgnoreCase)
            .Replace("{MachineName}", machineName, StringComparison.OrdinalIgnoreCase);

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch
        {
            // If parsing fails, return as-is
            return connectionString;
        }

        // Apply per-user database name if requested and not already suffixed
        if (usePerUserDatabase && !string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            var currentDb = builder.InitialCatalog;
            if (!currentDb.EndsWith($"_{userName}", StringComparison.OrdinalIgnoreCase) &&
                !currentDb.Equals(userName, StringComparison.OrdinalIgnoreCase))
            {
                builder.InitialCatalog = $"{currentDb}_{userName}";
            }
        }

        // Auto-detect or fallback local SQL Server instance if needed
        if (autoDetectServer)
        {
            builder.DataSource = DetermineWorkingServer(builder);
        }

        var finalString = builder.ConnectionString;

        // Print helpful notification to standard console
        try
        {
            var maskedBuilder = new SqlConnectionStringBuilder(finalString);
            if (!string.IsNullOrEmpty(maskedBuilder.Password)) maskedBuilder.Password = "******";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[Database] Local database resolved: Server='{maskedBuilder.DataSource}', Database='{maskedBuilder.InitialCatalog}'");
            Console.ResetColor();
        }
        catch
        {
            // Ignore masking failure
        }

        return finalString;
    }

    /// <summary>
    /// Checks if the configured server is reachable; if not, probes candidate local instances.
    /// </summary>
    private static string DetermineWorkingServer(SqlConnectionStringBuilder original)
    {
        var configuredServer = original.DataSource;

        // If configured server is a placeholder, empty or 'auto', start testing candidate servers immediately
        var isGeneric = string.IsNullOrWhiteSpace(configuredServer)
                        || configuredServer.Equals("{Server}", StringComparison.OrdinalIgnoreCase)
                        || configuredServer.Equals("auto", StringComparison.OrdinalIgnoreCase);

        if (!isGeneric && CanConnectToServer(original, configuredServer, timeoutSeconds: 2))
        {
            return configuredServer;
        }

        // Probe candidate servers
        foreach (var candidate in CandidateLocalServers)
        {
            if (string.Equals(candidate, configuredServer, StringComparison.OrdinalIgnoreCase))
            {
                continue; // already tested above
            }

            if (CanConnectToServer(original, candidate, timeoutSeconds: 1))
            {
                return candidate;
            }
        }

        // If none could be reached, return configured server (normal SQL exception will guide the dev)
        return isGeneric ? CandidateLocalServers[0] : configuredServer;
    }

    private static bool CanConnectToServer(SqlConnectionStringBuilder template, string server, int timeoutSeconds)
    {
        try
        {
            var probe = new SqlConnectionStringBuilder(template.ConnectionString)
            {
                DataSource = server,
                InitialCatalog = "master", // master always exists if server is running
                ConnectTimeout = timeoutSeconds
            };

            using var conn = new SqlConnection(probe.ConnectionString);
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sanitizes an input string to be a safe SQL Server identifier (letters, digits, underscores).
    /// </summary>
    public static string SanitizeSqlIdentifier(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "dev";
        var cleaned = Regex.Replace(input, @"[^\w]", "_");
        cleaned = Regex.Replace(cleaned, @"_+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "dev" : cleaned;
    }
}
