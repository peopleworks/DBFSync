using System.Data;
using System.Data.Odbc;
using System.Globalization;
using System.IO.Enumeration;

namespace PeopleWorks.DBFSync;

public sealed class DbfSource
{
    private static readonly DateTime EmptyFoxProDate = new(1899, 12, 30);

    static DbfSource()
    {
        System.Text.Encoding.RegisterProvider(
            System.Text.CodePagesEncodingProvider.Instance);
    }

    public IReadOnlyList<string> ResolveTables(
        string sourceDirectory,
        IReadOnlyList<string> requestedTables,
        IReadOnlyList<string> excludedTables,
        bool allTables)
    {
        string directory = ValidateDirectory(sourceDirectory);
        string[] requested = SplitPatterns(requestedTables);
        string[] inlineExclusions = requested
            .Where(pattern => pattern.StartsWith('!'))
            .Select(pattern => pattern[1..])
            .ToArray();
        string[] inclusions = requested
            .Where(pattern => !pattern.StartsWith('!'))
            .Select(NormalizePattern)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] exclusions = SplitPatterns(excludedTables)
            .Concat(inlineExclusions)
            .Select(NormalizePattern)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allTables && inclusions.Length > 0)
            throw new ArgumentException(L10n.T("UseAllOrPatterns"));
        if (!allTables && inclusions.Length == 0)
            throw new ArgumentException(L10n.T("TablesOrAllRequired"));

        string[] available = Directory
            .EnumerateFiles(directory, "*.dbf", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path)
                ?? throw new InvalidDataException(L10n.T("InvalidDbfPath", path)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (available.Length == 0)
            throw new FileNotFoundException(L10n.T("NoDbfFiles", directory));

        var selected = new List<string>();
        if (allTables)
        {
            selected.AddRange(available);
        }
        else
        {
            var unmatched = new List<string>();
            foreach (string pattern in inclusions)
            {
                string[] matches = available
                    .Where(name => Matches(pattern, name))
                    .ToArray();
                if (matches.Length == 0)
                    unmatched.Add(pattern);
                selected.AddRange(matches);
            }
            if (unmatched.Count > 0)
                throw new FileNotFoundException(L10n.T(
                    "UnmatchedPatterns",
                    directory,
                    string.Join(", ", unmatched)));
        }

        string[] result = selected
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !exclusions.Any(pattern => Matches(pattern, name)))
            .ToArray();
        if (result.Length == 0)
            throw new FileNotFoundException(L10n.T("EmptySelection"));
        return result;
    }

    public DbfTableSchema ReadSchema(string sourceDirectory, string tableName)
    {
        string directory = ValidateDirectory(sourceDirectory);
        string sourceName = ValidateSourceName(tableName);
        string filePath = ResolveFile(directory, sourceName);

        using OdbcConnection connection = Open(directory, excludeDeleted: true);
        using var command = new OdbcCommand(
            $"SELECT * FROM {sourceName} WHERE 1 = 0",
            connection)
        {
            CommandTimeout = 0
        };
        using OdbcDataReader reader = command.ExecuteReader(CommandBehavior.SchemaOnly);
        DataTable schema = reader.GetSchemaTable()
            ?? throw new InvalidDataException(L10n.T("OdbcNoSchema", sourceName));

        var columns = new List<DbfColumn>(schema.Rows.Count);
        foreach (DataRow row in schema.Rows.Cast<DataRow>()
                     .OrderBy(row => ReadInt(row, "ColumnOrdinal")))
        {
            string columnName = Convert.ToString(
                    row["ColumnName"],
                    CultureInfo.InvariantCulture)?.Trim()
                ?? throw new InvalidDataException(L10n.T("UnnamedDbfColumn", sourceName));
            ValidateSourceName(columnName);
            Type clrType = (Type)row["DataType"];
            int providerType = ReadInt(row, "ProviderType");
            columns.Add(new DbfColumn(
                columnName,
                SqlIdentifier.Normalize(columnName, "IdentifierDbfField"),
                ResolveKind(clrType, providerType),
                ReadInt(row, "ColumnSize"),
                ReadInt(row, "NumericPrecision"),
                ReadInt(row, "NumericScale"),
                clrType));
        }

        if (columns.Count == 0)
            throw new InvalidDataException(L10n.T("DbfNoColumns", sourceName));
        string[] reserved = ["_dbfsync_recno", "_dbfsync_hash", "_dbfsync_at"];
        if (columns.Any(column => reserved.Contains(
                column.TargetName,
                StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException(L10n.T("ReservedDbfColumn", sourceName));
        return new DbfTableSchema(
            sourceName,
            SqlIdentifier.Normalize(sourceName, "IdentifierDbfTable"),
            filePath,
            columns);
    }

    public long CountRows(string sourceDirectory, string tableName, bool excludeDeleted)
    {
        string directory = ValidateDirectory(sourceDirectory);
        string sourceName = ValidateSourceName(tableName);
        using OdbcConnection connection = Open(directory, excludeDeleted);
        using var command = new OdbcCommand(
            $"SELECT COUNT(*) FROM {sourceName}",
            connection)
        {
            CommandTimeout = 0
        };
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IEnumerable<DbfRow> ReadRows(
        string sourceDirectory,
        DbfTableSchema table,
        CancellationToken cancellationToken)
    {
        string directory = ValidateDirectory(sourceDirectory);
        using OdbcConnection connection = Open(directory, excludeDeleted: true);
        string selectedColumns = string.Join(
            ", ",
            table.Columns.Select(column => column.SourceName));
        using var command = new OdbcCommand(
            $"SELECT RECNO('{table.SourceName}') AS recno, {selectedColumns} FROM {table.SourceName}",
            connection)
        {
            CommandTimeout = 0
        };
        using OdbcDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        using var hasher = new RowHasher();
        if (reader.FieldCount != table.Columns.Count + 1)
        {
            string returned = string.Join(
                ", ",
                Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
            throw new InvalidDataException(L10n.T(
                "OdbcColumnCount",
                reader.FieldCount,
                table.SourceName,
                table.Columns.Count + 1,
                returned));
        }
        string[] returnedNames = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();
        string[] expectedNames = new[] { "recno" }
            .Concat(table.Columns.Select(column => column.SourceName))
            .ToArray();
        if (!returnedNames.SequenceEqual(expectedNames, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(L10n.T(
                "OdbcProjectionChanged",
                table.SourceName,
                string.Join(", ", expectedNames),
                string.Join(", ", returnedNames)));

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            object rawRecordNumber = reader.GetValue(0);
            if (!long.TryParse(
                    Convert.ToString(rawRecordNumber, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long recordNumber))
                throw new InvalidDataException(L10n.T(
                    "OdbcInvalidRecno",
                    rawRecordNumber,
                    rawRecordNumber.GetType().Name,
                    reader.GetName(0)));
            var values = new object?[table.Columns.Count];
            for (int i = 0; i < table.Columns.Count; i++)
                values[i] = ReadValue(reader, i + 1, table.Columns[i]);
            yield return new DbfRow(recordNumber, hasher.Compute(table.Columns, values), values);
        }
    }

    private static object? ReadValue(OdbcDataReader reader, int ordinal, DbfColumn column)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        object value = reader.GetValue(ordinal);
        return column.Kind switch
        {
            DbfColumnKind.Text => TextSanitizer.Sanitize(
                (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).TrimEnd()),
            DbfColumnKind.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            DbfColumnKind.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            DbfColumnKind.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            DbfColumnKind.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            DbfColumnKind.Date => NormalizeDate(
                Convert.ToDateTime(value, CultureInfo.InvariantCulture),
                dateOnly: true),
            DbfColumnKind.DateTime => NormalizeDate(
                Convert.ToDateTime(value, CultureInfo.InvariantCulture),
                dateOnly: false),
            DbfColumnKind.Binary when value is byte[] bytes => bytes,
            DbfColumnKind.Binary => throw new InvalidDataException(L10n.T(
                "BinaryValueExpected",
                column.SourceName,
                value.GetType().Name)),
            _ => throw new NotSupportedException(L10n.T(
                "UnsupportedColumnKind",
                column.SourceName,
                column.Kind))
        };
    }

    private static DateTime? NormalizeDate(DateTime value, bool dateOnly)
    {
        if (value.Date == EmptyFoxProDate)
            return null;
        DateTime normalized = dateOnly ? value.Date : value;
        return DateTime.SpecifyKind(normalized, DateTimeKind.Unspecified);
    }

    private static DbfColumnKind ResolveKind(Type clrType, int providerType)
    {
        if (clrType == typeof(string))
            return DbfColumnKind.Text;
        if (clrType == typeof(bool))
            return DbfColumnKind.Boolean;
        if (clrType == typeof(byte[]))
            return DbfColumnKind.Binary;
        if (clrType == typeof(DateTime))
            return providerType is 9 or 91 || providerType == (int)OdbcType.Date
                ? DbfColumnKind.Date
                : DbfColumnKind.DateTime;
        if (clrType == typeof(decimal))
            return DbfColumnKind.Decimal;
        if (clrType == typeof(float) || clrType == typeof(double))
            return DbfColumnKind.Double;
        if (clrType == typeof(byte) || clrType == typeof(short) ||
            clrType == typeof(int) || clrType == typeof(long))
            return DbfColumnKind.Integer;
        throw new NotSupportedException(L10n.T("UnsupportedOdbcType", clrType.Name));
    }

    private static OdbcConnection Open(string directory, bool excludeDeleted)
    {
        var builder = new OdbcConnectionStringBuilder
        {
            Driver = "Microsoft FoxPro VFP Driver (*.dbf)"
        };
        builder["SourceType"] = "DBF";
        builder["SourceDB"] = directory;
        builder["Deleted"] = excludeDeleted ? "Yes" : "No";
        builder["Null"] = "No";
        builder["Collate"] = "Machine";
        builder["Exclusive"] = "No";
        builder["BackgroundFetch"] = "No";

        var connection = new OdbcConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (OdbcException ex)
        {
            connection.Dispose();
            throw new InvalidOperationException(
                L10n.T("FoxProDriverRequired"),
                ex);
        }
    }

    private static string ValidateDirectory(string sourceDirectory)
    {
        string fullPath = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(L10n.T("DbfDirectoryMissing", fullPath));
        return fullPath;
    }

    private static string ValidateSourceName(string value)
    {
        string name = Path.GetFileNameWithoutExtension(value.Trim());
        if (string.IsNullOrWhiteSpace(name) ||
            name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new ArgumentException(L10n.T("InvalidDbfName", value));
        return name;
    }

    private static string[] SplitPatterns(IReadOnlyList<string> values) =>
        values
            .SelectMany(value => value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

    private static string NormalizePattern(string value)
    {
        string pattern = Path.GetFileName(value.Trim());
        if (pattern.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase))
            pattern = pattern[..^4];
        if (string.IsNullOrWhiteSpace(pattern) ||
            pattern.Any(c => !(char.IsLetterOrDigit(c) ||
                               c is '_' or '-' or '.' or '*' or '?')))
            throw new ArgumentException(L10n.T("InvalidDbfPattern", value));
        return pattern;
    }

    private static bool Matches(string pattern, string tableName) =>
        FileSystemName.MatchesSimpleExpression(
            pattern,
            tableName,
            ignoreCase: true);

    private static string ResolveFile(string directory, string sourceName)
    {
        string? path = Directory.EnumerateFiles(directory, "*.dbf", SearchOption.TopDirectoryOnly)
            .SingleOrDefault(file => Path.GetFileNameWithoutExtension(file)
                .Equals(sourceName, StringComparison.OrdinalIgnoreCase));
        return path ?? throw new FileNotFoundException(L10n.T(
            "DbfFileMissing",
            sourceName,
            directory));
    }

    private static int ReadInt(DataRow row, string columnName) =>
        row.Table.Columns.Contains(columnName) && row[columnName] != DBNull.Value
            ? Convert.ToInt32(row[columnName], CultureInfo.InvariantCulture)
            : 0;
}
