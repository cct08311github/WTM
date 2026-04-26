#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Schema;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Mvc;

/// <summary>
/// Schema introspection endpoints (10.5+) — power the "browse the
/// connected DB to pick a table / columns" UX in the ETL admin pages
/// without forcing operators to type names by hand.
/// </summary>
/// <remarks>
/// Connection strings are resolved via the WTM <see cref="Configs"/>
/// connection registry by key, so a caller can never inject a raw
/// connection string from outside. The connection user determines
/// what tables are visible, matching the existing ETL job-execution
/// permission model.
/// </remarks>
[ActionDescription("ETL Schema")]
public class _EtlSchemaController : BaseController
{
    /// <summary>
    /// List tables on a connection.
    ///
    /// <para>
    /// <c>GET /_EtlSchema/Tables?csKey=Source1&amp;dbType=SqlServer&amp;schema=dbo</c>
    /// </para>
    /// </summary>
    [ActionDescription("列出表")]
    [HttpGet]
    public async Task<IActionResult> Tables(
        string csKey,
        DBTypeEnum dbType,
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csKey))
        {
            return BadRequest(new { error = "csKey is required." });
        }

        var cs = Wtm.ConfigInfo.Connections?.FirstOrDefault(c => c.Key == csKey);
        if (cs == null)
        {
            return NotFound(new { error = $"Connection key '{csKey}' not found in Configs.Connections." });
        }

        try
        {
            var svc = EtlSchemaServiceFactory.Create(dbType);
            var tables = await svc.ListTablesAsync(cs.Value ?? "", schema, cancellationToken);
            return Ok(tables);
        }
        catch (NotSupportedException nse)
        {
            return StatusCode(501, new { error = nse.Message });
        }
        catch (Exception ex)
        {
            // Don't leak exception text to admin browser tab — operator
            // can pull stack from server log.
            return StatusCode(500, new { error = "Schema introspection failed; see server log.", message = ex.Message });
        }
    }

    /// <summary>
    /// List columns of a single table on a connection.
    ///
    /// <para>
    /// <c>GET /_EtlSchema/Columns?csKey=Source1&amp;dbType=SqlServer&amp;table=Orders</c>
    /// </para>
    /// </summary>
    [ActionDescription("列出欄位")]
    [HttpGet]
    public async Task<IActionResult> Columns(
        string csKey,
        DBTypeEnum dbType,
        string table,
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csKey))
        {
            return BadRequest(new { error = "csKey is required." });
        }
        if (string.IsNullOrWhiteSpace(table))
        {
            return BadRequest(new { error = "table is required." });
        }

        var cs = Wtm.ConfigInfo.Connections?.FirstOrDefault(c => c.Key == csKey);
        if (cs == null)
        {
            return NotFound(new { error = $"Connection key '{csKey}' not found in Configs.Connections." });
        }

        try
        {
            var svc = EtlSchemaServiceFactory.Create(dbType);
            var columns = await svc.ListColumnsAsync(cs.Value ?? "", table, schema, cancellationToken);
            return Ok(columns);
        }
        catch (NotSupportedException nse)
        {
            return StatusCode(501, new { error = nse.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Schema introspection failed; see server log.", message = ex.Message });
        }
    }
}
