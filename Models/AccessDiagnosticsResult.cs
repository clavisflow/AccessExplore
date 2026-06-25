namespace AccessDoctor.Models;

public sealed record AccessDiagnosticsResult
{
    public required string FileName { get; init; }
    public required long FileSizeBytes { get; init; }
    public required string AccessVersion { get; init; }
    public required AccessObjectSummary Summary { get; init; }
    public required IReadOnlyList<AccessTableInfo> Tables { get; init; }
    public required IReadOnlyList<AccessQueryInfo> Queries { get; init; }
    public required AccessObjectInventory Objects { get; init; }
    public required IReadOnlyList<AccessRelationshipInfo> Relationships { get; init; }
    public required string SchemaText { get; init; }
    public required IReadOnlyList<CommandDiagnostic> CommandDiagnostics { get; init; }
}

public sealed record AccessObjectSummary(
    int TableCount,
    int QueryCount,
    int FormCount,
    int ReportCount,
    int MacroCount,
    int ModuleCount,
    int RelationshipCount,
    int LinkedTableCount,
    long TotalRecordCount,
    int QuerySqlCapturedCount);

public sealed record AccessTableInfo(
    string Name,
    IReadOnlyList<AccessColumnInfo> Columns,
    long? RecordCount,
    IReadOnlyList<AccessIndexInfo> Indexes,
    IReadOnlyList<AccessTableConstraintInfo> Constraints,
    IReadOnlyList<AccessRelationshipInfo> Relationships);

public sealed record AccessColumnInfo(
    string Name,
    string DataType,
    int? Size,
    bool IsNotNull);

public sealed record AccessIndexInfo(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    bool IsPrimaryKey);

public sealed record AccessTableConstraintInfo(
    string Name,
    string Kind,
    string Definition);

public sealed record AccessRelationshipInfo(
    string Name,
    string PrimaryTable,
    IReadOnlyList<string> PrimaryColumns,
    string ForeignTable,
    IReadOnlyList<string> ForeignColumns,
    bool CascadeUpdate,
    bool CascadeDelete);

public sealed record AccessQueryInfo(
    string Name,
    string? Sql,
    string Kind,
    bool SqlCaptured);

public sealed record AccessObjectDetail(
    string Name,
    string Kind,
    int? TypeCode,
    string? DateCreate,
    string? DateUpdate,
    int? Flags);

public sealed record AccessObjectInventory(
    IReadOnlyList<AccessObjectDetail> Forms,
    IReadOnlyList<AccessObjectDetail> Reports,
    IReadOnlyList<AccessObjectDetail> Macros,
    IReadOnlyList<AccessObjectDetail> Modules,
    IReadOnlyList<AccessObjectDetail> Relationships,
    IReadOnlyList<AccessObjectDetail> LinkedTables);

public sealed record CommandDiagnostic(
    string Command,
    int ElapsedMs,
    IReadOnlyList<string> Stderr);

public sealed class AccessTablePreviewResult
{
    public List<string> Columns { get; set; } = [];
    public List<Dictionary<string, string?>> Rows { get; set; } = [];
    public int DisplayLimit { get; set; }
    public bool IsTruncated { get; set; }
    public CommandDiagnostic? Diagnostic { get; set; }
}
