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
    /// <summary><c>staging.process_batch</c>: validate into staging and load core with the 5 % quality gate.</summary>
    public Task ProcessBatchAsync(long batchId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT staging.process_batch({batchId})", cancellationToken);

    /// <summary>A binary COPY on the context's connection, inside its transaction: 50,000 rows in well under a second.</summary>
    public async Task CopyRowsAsync(long batchId, string source, CsvImportFile.Parsed parsed, CancellationToken cancellationToken)
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
