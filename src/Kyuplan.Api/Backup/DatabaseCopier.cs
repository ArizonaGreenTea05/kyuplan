using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Kyuplan.Api.Backup;

/// <summary>
/// Reads every table of a context into rows (JSON objects, column name to value) and writes such rows into a context, replacing what it held.
/// It works on the model, not on SQL, so a copy can go from SQLite to PostgreSQL and back. Rows are plain entities (no shadow
/// properties) or the property bags of many-to-many tables; a model that has anything else makes <see cref="Tables"/> throw, so a test notices it.
/// </summary>
internal static class DatabaseCopier
{
    private static readonly MethodInfo LoadMethod = typeof(DatabaseCopier).GetMethod(nameof(LoadAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo DeleteMethod = typeof(DatabaseCopier).GetMethod(nameof(DeleteAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo SetByType = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;
    private static readonly MethodInfo SetByName = typeof(DbContext).GetMethod(nameof(DbContext.Set), [typeof(string)])!;

    /// <summary>The name a table has in a backup file.</summary>
    public static string NameOf(IEntityType type) => type.ShortName();

    /// <summary>The tables of the context, the ones others point to first (the order rows can be inserted in).</summary>
    public static IReadOnlyList<IEntityType> Tables(DbContext db)
    {
        var ordered = new List<IEntityType>();
        var visiting = new HashSet<IEntityType>();

        void Visit(IEntityType type)
        {
            if (ordered.Contains(type) || !visiting.Add(type)) return;

            foreach (var key in type.GetForeignKeys())
                if (key.PrincipalEntityType != type) Visit(key.PrincipalEntityType);
            ordered.Add(type);
        }

        foreach (var type in db.Model.GetEntityTypes().OrderBy(NameOf, StringComparer.Ordinal))
        {
            if (!type.HasSharedClrType && type.GetProperties().FirstOrDefault(p => p.IsShadowProperty()) is { } shadow)
                throw new NotSupportedException($"{NameOf(type)}.{shadow.Name} is a shadow property: backups cannot hold it.");
            Visit(type);
        }

        return ordered;
    }

    /// <summary>All rows of the table.</summary>
    public static async Task<List<JsonObject>> ReadAsync(DbContext db, IEntityType type, CancellationToken cancellationToken)
    {
        var task = (Task<List<object>>)LoadMethod.MakeGenericMethod(type.ClrType).Invoke(null, [QueryOf(db, type), cancellationToken])!;
        var rows = new List<JsonObject>();
        foreach (var entity in await task)
        {
            var row = new JsonObject();
            foreach (var property in type.GetProperties())
            {
                var value = type.HasSharedClrType
                    ? ((IDictionary<string, object>)entity).TryGetValue(property.Name, out var v) ? v : null
                    : (property.PropertyInfo ?? throw new NotSupportedException($"{NameOf(type)}.{property.Name} has no property."))
                        .GetValue(entity);
                row[property.Name] = JsonSerializer.SerializeToNode(value, property.ClrType);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Replaces everything the context holds with these rows (by table name) in one transaction: nothing changes when a row is refused.
    /// A property a row does not have keeps its default (a backup of an older version), one the model does not know is ignored.
    /// </summary>
    public static async Task ReplaceAsync(DbContext db, IReadOnlyDictionary<string, List<JsonObject>> tables, CancellationToken cancellationToken)
    {
        var types = Tables(db);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var type in types.Reverse())
        {
            if (type.HasSharedClrType) await db.Set<Dictionary<string, object>>(type.Name).ExecuteDeleteAsync(cancellationToken);
            else await (Task)DeleteMethod.MakeGenericMethod(type.ClrType).Invoke(null, [QueryOf(db, type), cancellationToken])!;
        }

        db.ChangeTracker.Clear();
        foreach (var type in types)
        {
            if (!tables.TryGetValue(NameOf(type), out var rows)) continue;

            foreach (var row in rows)
            {
                if (type.HasSharedClrType)
                {
                    var bag = new Dictionary<string, object>();
                    foreach (var property in type.GetProperties())
                        if (Convert(row, property) is { } value) bag[property.Name] = value;
                    db.Set<Dictionary<string, object>>(type.Name).Add(bag);
                }
                else
                {
                    var entity = Activator.CreateInstance(type.ClrType, nonPublic: true)!;
                    foreach (var property in type.GetProperties())
                        (property.PropertyInfo ?? throw new NotSupportedException($"{NameOf(type)}.{property.Name} has no property."))
                            .SetValue(entity, Convert(row, property));
                    db.Add(entity);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        await ResetSequencesAsync(db, types, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static object? Convert(JsonObject row, IProperty property)
    {
        if (!row.TryGetPropertyValue(property.Name, out var node) || node is null)
            return property.ClrType.IsValueType && Nullable.GetUnderlyingType(property.ClrType) is null ? Activator.CreateInstance(property.ClrType) : null;

        return node.Deserialize(property.ClrType);
    }

    private static IQueryable QueryOf(DbContext db, IEntityType type) => (IQueryable)(type.HasSharedClrType
        ? SetByName.MakeGenericMethod(type.ClrType).Invoke(db, [type.Name])!
        : SetByType.MakeGenericMethod(type.ClrType).Invoke(db, null)!);

    private static async Task<List<object>> LoadAsync<T>(IQueryable query, CancellationToken cancellationToken) where T : class =>
        [.. await ((IQueryable<T>)query).IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken)]; // hidden members belong to the backup, too

    private static Task DeleteAsync<T>(IQueryable query, CancellationToken cancellationToken) where T : class =>
        ((IQueryable<T>)query).IgnoreQueryFilters().ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// PostgreSQL does not move an identity column's counter when a row is inserted with an id of its own: the next new row would get
    /// an id that exists. Sets every counter behind the highest id.
    /// </summary>
    private static async Task ResetSequencesAsync(DbContext db, IReadOnlyList<IEntityType> types, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql()) return;

        foreach (var type in types)
        {
            var table = type.GetTableName();
            if (table is null || type.FindPrimaryKey()?.Properties is not [{ ClrType: var clr, ValueGenerated: ValueGenerated.OnAdd } key] || clr != typeof(int)) continue;

            var qualified = (type.GetSchema() is { } schema ? $"\"{schema}\"." : "") + $"\"{table}\"";
            var column = key.GetColumnName();
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT setval(pg_get_serial_sequence('{qualified.Replace("'", "''")}', '{column}'), COALESCE((SELECT MAX(\"{column}\") FROM {qualified}), 0) + 1, false)",
                cancellationToken);
        }
    }
}
