using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Modules.Inventory.Features.Imports;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

// The write side's direct database work (STAGE_5_PLAN section 2.1): the SECURITY DEFINER functions that complete a
// transfer step and the raw-row COPY of an import. Features decide whether a step is allowed and call these in the
// command's transaction; an architecture test keeps raw SQL out of Features.

/// <summary>The transfer workflow functions of <c>db/sql/inventory/0002_inventory_logic.sql</c>.</summary>
internal sealed class TransferFunctions(InventoryDbContext db)
{
    public Task ApproveAsync(long transferId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT inventory.approve_transfer({transferId})", cancellationToken);

    public Task CancelAsync(long transferId, string reason, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT inventory.cancel_transfer({transferId}, {reason})", cancellationToken);

    public Task DispatchAsync(long transferId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT inventory.dispatch_transfer({transferId})", cancellationToken);

    public Task ReceiveAsync(long transferId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT inventory.receive_transfer({transferId})", cancellationToken);
}

/// <summary>The raw layer of an import and the staging pipeline (DATABASE_DESIGN section 3).</summary>
internal sealed class ImportStore(InventoryDbContext db)
{
    private const string DryRunSavepoint = "import_dry_run";

    /// <summary>Marks the start of a dry run inside the command's transaction (STAGE_7_PLAN 2.6).</summary>
    public Task BeginDryRunAsync(CancellationToken cancellationToken) =>
        db.Database.CurrentTransaction!.CreateSavepointAsync(DryRunSavepoint, cancellationToken);

    /// <summary>Undoes everything since <see cref="BeginDryRunAsync"/>: the batch, its raw and staging rows and the load.</summary>
    public async Task EndDryRunAsync(CancellationToken cancellationToken)
    {
        await db.Database.CurrentTransaction!.RollbackToSavepointAsync(DryRunSavepoint, cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// The core data of a source in its import columns: the active parts, or the historical demand of the ledger, so an
    /// export can be edited and imported again (STAGE_7_PLAN 2.6).
    /// </summary>
    public async Task<List<string?[]>> ExportRowsAsync(string source, CancellationToken cancellationToken)
    {
        var sql = source == ImportSources.PartCatalog
            ? """
              SELECT b.code, p.oem_part_number, p.description, c.code, u.code, p.weight_kg::text, p.unit_cost::text,
                     CASE WHEN p.is_condition_based THEN 'true' ELSE 'false' END, m.code
              FROM catalog.part p
              LEFT JOIN catalog.manufacturer m ON m.id = p.manufacturer_id
              JOIN catalog.brand b ON b.id = p.brand_id
              JOIN ref.part_category c ON c.id = p.category_id
              JOIN ref.unit_of_measure u ON u.id = p.uom_id
              WHERE NOT p.is_archived
              ORDER BY b.code, p.oem_part_number
              """
            : """
              SELECT to_char(m.occurred_at, 'YYYY-MM-DD'), w.code, b.code, p.oem_part_number, m.quantity::text, m.external_doc_ref
              FROM inventory.stock_movement m
              JOIN inventory.warehouse w ON w.id = m.warehouse_id
              JOIN catalog.part p ON p.id = m.part_id
              JOIN catalog.brand b ON b.id = p.brand_id
              WHERE m.movement_type_code = 'HISTORICAL_DEMAND'
              ORDER BY m.occurred_at, w.code, b.code, p.oem_part_number
              """;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<string?[]>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetString(i);
                }

                rows.Add(row);
            }

            return rows;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary><c>staging.process_batch</c>: validate into staging and load core with the 5 % quality gate.</summary>
    public Task ProcessBatchAsync(long batchId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT staging.process_batch({batchId})", cancellationToken);

    /// <summary>A binary COPY on the context's connection, inside its transaction: 50,000 rows in well under a second.</summary>
    public async Task CopyRowsAsync(long batchId, string source, SmartSpare.Infrastructure.Csv.CsvFile.Parsed parsed, CancellationToken cancellationToken)
    {
        var table = source == ImportSources.PartCatalog ? "raw.part_catalog_row" : "raw.demand_history_row";
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var writer = await connection.BeginBinaryImportAsync(
            $"COPY {table} (import_batch_id, line_no, {string.Join(", ", parsed.Columns)}) FROM STDIN (FORMAT BINARY)", cancellationToken)
            .ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            foreach (var (lineNo, values) in parsed.Rows)
            {
                await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(batchId, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(lineNo, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                foreach (var value in values)
                {
                    if (value is null)
                    {
                        await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
