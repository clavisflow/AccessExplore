using System.Text.RegularExpressions;
using AccessDoctor.Models;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace AccessDoctor.Services;

public sealed partial class AccessDatabaseAnalyzer(IJSRuntime jsRuntime)
{
    private const long MaxFileSize = 300L * 1024 * 1024;

    public async Task<AccessTablePreviewResult> ReadTablePreviewAsync(string tableName, int displayLimit) =>
        await jsRuntime.InvokeAsync<AccessTablePreviewResult>(
            "accessDoctorMdb.readTablePreview",
            tableName,
            displayLimit);

    public async Task DownloadTableCsvAsync(string fileName, string tableName, IReadOnlyList<string> columns) =>
        await jsRuntime.InvokeVoidAsync(
            "accessDoctorMdb.downloadTableCsv",
            fileName,
            tableName,
            columns);

    public async Task<AccessDiagnosticsResult> AnalyzeAsync(IBrowserFile file)
    {
        ValidateFile(file);

        await using var browserStream = file.OpenReadStream(MaxFileSize);
        using var memory = new MemoryStream();
        await browserStream.CopyToAsync(memory);

        var raw = await jsRuntime.InvokeAsync<RawAccessAnalysis>(
            "accessDoctorMdb.analyzeAccessFile",
            memory.ToArray(),
            new { maxQuerySql = 30, largeFileQuerySql = 5, largeFileBytes = 100 * 1024 * 1024 });

        var parsedSchema = ParseSchema(raw.Schema);
        var counts = raw.TableCounts.ToDictionary(
            item => item.TableName,
            item => ParseCount(item.Count),
            StringComparer.OrdinalIgnoreCase);

        var tables = raw.Tables
            .Select(name =>
            {
                parsedSchema.Tables.TryGetValue(name, out var tableDetail);
                return new AccessTableInfo(
                    name,
                    tableDetail?.Columns ?? [],
                    counts.GetValueOrDefault(name),
                    tableDetail?.Indexes ?? [],
                    tableDetail?.Constraints ?? [],
                    tableDetail?.Relationships ?? []);
            })
            .ToList();

        var sqlByQuery = raw.QuerySql
            .Where(item => !string.IsNullOrWhiteSpace(item.Sql))
            .ToDictionary(item => item.QueryName, item => item.Sql, StringComparer.OrdinalIgnoreCase);

        var queries = raw.Queries
            .Select(name =>
            {
                sqlByQuery.TryGetValue(name, out var sql);
                return new AccessQueryInfo(name, sql, DetectQueryKind(sql), !string.IsNullOrWhiteSpace(sql));
            })
            .ToList();

        var summary = new AccessObjectSummary(
            tables.Count,
            queries.Count,
            raw.Forms.Count,
            raw.Reports.Count,
            raw.Macros.Count,
            raw.Modules.Count,
            raw.Relationships.Count,
            raw.LinkedTables.Count,
            tables.Sum(table => table.RecordCount ?? 0),
            queries.Count(query => query.SqlCaptured));

        var objectDetails = raw.ObjectDetails
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var objects = new AccessObjectInventory(
            BuildObjectDetails(raw.Forms, "フォーム", objectDetails),
            BuildObjectDetails(raw.Reports, "レポート", objectDetails),
            BuildObjectDetails(raw.Macros, "マクロ", objectDetails),
            BuildObjectDetails(raw.Modules, "モジュール", objectDetails),
            BuildObjectDetails(raw.Relationships, "リレーション", objectDetails),
            BuildObjectDetails(raw.LinkedTables, "リンクテーブル", objectDetails));

        return new AccessDiagnosticsResult
        {
            FileName = file.Name,
            FileSizeBytes = file.Size,
            AccessVersion = string.IsNullOrWhiteSpace(raw.Version) ? "Unknown" : raw.Version,
            Summary = summary,
            Tables = tables,
            Queries = queries,
            Objects = objects,
            Relationships = parsedSchema.Relationships,
            SchemaText = raw.Schema,
            CommandDiagnostics = raw.CommandDiagnostics
                .Select(item => new CommandDiagnostic(item.Command, item.ElapsedMs, item.Stderr))
                .ToList()
        };
    }

    private static void ValidateFile(IBrowserFile file)
    {
        var extension = Path.GetExtension(file.Name);
        if (!extension.Equals(".accdb", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mdb", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(".accdb または .mdb ファイルを選択してください。");
        }

        if (file.Size > MaxFileSize)
        {
            throw new InvalidOperationException("300MB 以下の Access ファイルを選択してください。");
        }
    }

    private static ParsedSchema ParseSchema(string schemaText)
    {
        var tables = new Dictionary<string, ParsedTable>(StringComparer.OrdinalIgnoreCase);
        var relationships = new List<AccessRelationshipInfo>();
        string? currentTable = null;

        foreach (var rawLine in schemaText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var tableMatch = CreateTableRegex().Match(line);
            if (tableMatch.Success)
            {
                currentTable = tableMatch.Groups["name"].Value;
                EnsureTable(tables, currentTable);
                continue;
            }

            if (currentTable is not null)
            {
                if (line == ");")
                {
                    currentTable = null;
                    continue;
                }

                if (line == "(")
                {
                    continue;
                }

                var table = EnsureTable(tables, currentTable);
                var statement = line.TrimEnd(',');

                var inlineConstraintMatch = InlineConstraintRegex().Match(statement);
                if (inlineConstraintMatch.Success)
                {
                    var definition = inlineConstraintMatch.Groups["definition"].Value.Trim();
                    table.Constraints.Add(new AccessTableConstraintInfo(
                        inlineConstraintMatch.Groups["name"].Value,
                        DetectConstraintKind(definition),
                        definition));
                    continue;
                }

                var column = ParseColumn(statement);
                if (column is not null)
                {
                    table.Columns.Add(column);
                }

                continue;
            }

            var indexMatch = CreateIndexRegex().Match(line);
            if (indexMatch.Success)
            {
                var table = EnsureTable(tables, indexMatch.Groups["table"].Value);
                var indexName = indexMatch.Groups["name"].Value;
                var columns = ParseIdentifierList(indexMatch.Groups["columns"].Value);
                var isUnique = !string.IsNullOrWhiteSpace(indexMatch.Groups["unique"].Value);
                table.Indexes.Add(new AccessIndexInfo(
                    indexName,
                    columns,
                    isUnique,
                    indexName.Contains("primary", StringComparison.OrdinalIgnoreCase)));
                continue;
            }

            var alterConstraintMatch = AlterTableConstraintRegex().Match(line);
            if (!alterConstraintMatch.Success)
            {
                continue;
            }

            var tableName = alterConstraintMatch.Groups["table"].Value;
            var constraintName = alterConstraintMatch.Groups["name"].Value;
            var constraintDefinition = alterConstraintMatch.Groups["definition"].Value.Trim();

            var foreignKeyMatch = ForeignKeyRegex().Match(constraintDefinition);
            if (foreignKeyMatch.Success)
            {
                var primaryTable = foreignKeyMatch.Groups["primaryTable"].Value;
                var relation = new AccessRelationshipInfo(
                    constraintName,
                    primaryTable,
                    ParseIdentifierList(foreignKeyMatch.Groups["primaryColumns"].Value),
                    tableName,
                    ParseIdentifierList(foreignKeyMatch.Groups["foreignColumns"].Value),
                    foreignKeyMatch.Groups["options"].Value.Contains("ON UPDATE CASCADE", StringComparison.OrdinalIgnoreCase),
                    foreignKeyMatch.Groups["options"].Value.Contains("ON DELETE CASCADE", StringComparison.OrdinalIgnoreCase));

                relationships.Add(relation);
                EnsureTable(tables, tableName).Relationships.Add(relation);
                EnsureTable(tables, primaryTable).Relationships.Add(relation);
                EnsureTable(tables, tableName).Constraints.Add(new AccessTableConstraintInfo(
                    constraintName,
                    "FOREIGN KEY",
                    constraintDefinition));
                continue;
            }

            EnsureTable(tables, tableName).Constraints.Add(new AccessTableConstraintInfo(
                constraintName,
                DetectConstraintKind(constraintDefinition),
                constraintDefinition));
        }

        return new ParsedSchema(
            tables.ToDictionary(
                pair => pair.Key,
                pair => new ParsedTableResult(
                    pair.Value.Columns,
                    pair.Value.Indexes,
                    pair.Value.Constraints,
                    pair.Value.Relationships),
                StringComparer.OrdinalIgnoreCase),
            relationships);
    }

    private static ParsedTable EnsureTable(IDictionary<string, ParsedTable> tables, string tableName)
    {
        if (tables.TryGetValue(tableName, out var table))
        {
            return table;
        }

        table = new ParsedTable();
        tables[tableName] = table;
        return table;
    }

    private static AccessColumnInfo? ParseColumn(string statement)
    {
        var columnMatch = ColumnDefinitionRegex().Match(statement);
        if (!columnMatch.Success)
        {
            return null;
        }

        var definition = columnMatch.Groups["definition"].Value.Trim();
        var typePart = TrimColumnConstraintTail(definition);
        var sizeMatch = TypeSizeRegex().Match(typePart);
        int? size = sizeMatch.Success && int.TryParse(sizeMatch.Groups["size"].Value, out var parsedSize)
            ? parsedSize
            : null;
        var dataType = TypeSizeRegex().Replace(typePart, string.Empty).Trim();

        return new AccessColumnInfo(
            columnMatch.Groups["name"].Value,
            string.IsNullOrWhiteSpace(dataType) ? typePart : dataType,
            size,
            definition.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase));
    }

    private static string TrimColumnConstraintTail(string definition)
    {
        string[] markers =
        [
            " NOT NULL",
            " NULL",
            " DEFAULT ",
            " CONSTRAINT ",
            " PRIMARY KEY",
            " REFERENCES ",
            " CHECK "
        ];

        var cutIndex = definition.Length;
        foreach (var marker in markers)
        {
            var index = definition.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && index < cutIndex)
            {
                cutIndex = index;
            }
        }

        return definition[..cutIndex].Trim();
    }

    private static IReadOnlyList<string> ParseIdentifierList(string source)
    {
        return source
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => SortSuffixRegex().Replace(part.Trim(), string.Empty).Trim())
            .Select(part => part.Trim('[', ']', '"'))
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
    }

    private static string DetectConstraintKind(string definition)
    {
        var normalized = definition.TrimStart();
        if (normalized.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
        {
            return "PRIMARY KEY";
        }

        if (normalized.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
        {
            return "FOREIGN KEY";
        }

        if (normalized.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            return "UNIQUE";
        }

        if (normalized.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
        {
            return "CHECK";
        }

        return "CONSTRAINT";
    }

    private static long? ParseCount(string? value) =>
        long.TryParse(value, out var count) ? count : null;

    private static IReadOnlyList<AccessObjectDetail> BuildObjectDetails(
        IEnumerable<string> names,
        string kind,
        IReadOnlyDictionary<string, RawObjectDetail> details)
    {
        return names
            .Select(name =>
            {
                details.TryGetValue(name, out var detail);
                return new AccessObjectDetail(
                    name,
                    kind,
                    detail?.TypeCode,
                    NullIfWhiteSpace(detail?.DateCreate),
                    NullIfWhiteSpace(detail?.DateUpdate),
                    detail?.Flags);
            })
            .ToList();
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DetectQueryKind(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return "未取得";
        }

        var normalized = sql.TrimStart().ToUpperInvariant();
        if (normalized.StartsWith("SELECT INTO", StringComparison.Ordinal))
        {
            return "MAKE TABLE";
        }

        var firstToken = normalized.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstToken switch
        {
            "SELECT" => "SELECT",
            "UPDATE" => "UPDATE",
            "DELETE" => "DELETE",
            "INSERT" => "INSERT",
            "TRANSFORM" => "CROSSTAB",
            _ => "OTHER"
        };
    }

    [GeneratedRegex(@"^CREATE TABLE \[(?<name>.+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableRegex();

    [GeneratedRegex(@"^\[(?<name>.+?)\]\s+(?<definition>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnDefinitionRegex();

    [GeneratedRegex(@"\((?<size>\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex TypeSizeRegex();

    [GeneratedRegex(@"^CONSTRAINT\s+\[(?<name>.+?)\]\s+(?<definition>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InlineConstraintRegex();

    [GeneratedRegex(@"^CREATE\s+(?<unique>UNIQUE\s+)?INDEX\s+\[(?<name>.+?)\]\s+ON\s+\[(?<table>.+?)\]\s*\((?<columns>.+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex CreateIndexRegex();

    [GeneratedRegex(@"^ALTER\s+TABLE\s+\[(?<table>.+?)\]\s+ADD\s+CONSTRAINT\s+\[(?<name>.+?)\]\s+(?<definition>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AlterTableConstraintRegex();

    [GeneratedRegex(@"^FOREIGN\s+KEY\s*\((?<foreignColumns>.+?)\)\s+REFERENCES\s+\[(?<primaryTable>.+?)\]\s*\((?<primaryColumns>.+?)\)(?<options>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ForeignKeyRegex();

    [GeneratedRegex(@"\s+(ASC|DESC)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SortSuffixRegex();

    private sealed class ParsedTable
    {
        public List<AccessColumnInfo> Columns { get; } = [];
        public List<AccessIndexInfo> Indexes { get; } = [];
        public List<AccessTableConstraintInfo> Constraints { get; } = [];
        public List<AccessRelationshipInfo> Relationships { get; } = [];
    }

    private sealed record ParsedTableResult(
        IReadOnlyList<AccessColumnInfo> Columns,
        IReadOnlyList<AccessIndexInfo> Indexes,
        IReadOnlyList<AccessTableConstraintInfo> Constraints,
        IReadOnlyList<AccessRelationshipInfo> Relationships);

    private sealed record ParsedSchema(
        IReadOnlyDictionary<string, ParsedTableResult> Tables,
        IReadOnlyList<AccessRelationshipInfo> Relationships);
}

public sealed class RawAccessAnalysis
{
    public string Version { get; set; } = string.Empty;
    public List<string> Tables { get; set; } = [];
    public List<string> Queries { get; set; } = [];
    public List<string> Forms { get; set; } = [];
    public List<string> Reports { get; set; } = [];
    public List<string> Macros { get; set; } = [];
    public List<string> Modules { get; set; } = [];
    public List<string> Relationships { get; set; } = [];
    public List<string> LinkedTables { get; set; } = [];
    public List<RawObjectDetail> ObjectDetails { get; set; } = [];
    public string Schema { get; set; } = string.Empty;
    public List<RawTableCount> TableCounts { get; set; } = [];
    public List<RawQuerySql> QuerySql { get; set; } = [];
    public List<RawCommandDiagnostic> CommandDiagnostics { get; set; } = [];
}

public sealed class RawTableCount
{
    public string TableName { get; set; } = string.Empty;
    public string Count { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
}

public sealed class RawQuerySql
{
    public string QueryName { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
}

public sealed class RawObjectDetail
{
    public string Name { get; set; } = string.Empty;
    public int? TypeCode { get; set; }
    public int? Flags { get; set; }
    public string DateCreate { get; set; } = string.Empty;
    public string DateUpdate { get; set; } = string.Empty;
}

public sealed class RawCommandDiagnostic
{
    public string Command { get; set; } = string.Empty;
    public int ElapsedMs { get; set; }
    public List<string> Stderr { get; set; } = [];
}
