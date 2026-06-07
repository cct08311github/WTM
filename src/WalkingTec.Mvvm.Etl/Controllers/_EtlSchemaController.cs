#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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
///
/// When <see cref="IMemoryCache"/> is registered in the DI container,
/// schema results are cached for 60 seconds
/// (<see cref="CachingEtlSchemaService.DefaultTtlSeconds"/>) to reduce
/// repeated round-trips to the source DB during the "browse tables"
/// UX flow. Behavior without <c>IMemoryCache</c> is unchanged (opt-in, 10.6+).
/// </remarks>
[ActionDescription("ETL Schema")]
public class _EtlSchemaController : BaseController
{
    private readonly ILogger<_EtlSchemaController> _logger;
    private readonly IMemoryCache? _cache;

    /// <param name="logger">Logger (required).</param>
    /// <param name="cache">
    /// Optional memory cache. When present, schema results are cached with a
    /// 60 s TTL (<see cref="CachingEtlSchemaService.DefaultTtlSeconds"/>).
    /// Inject via <c>AddMemoryCache()</c> in your startup to opt in.
    /// When <c>null</c>, behavior is identical to 10.5.x (no cache).
    /// </param>
    public _EtlSchemaController(
        ILogger<_EtlSchemaController> logger,
        IMemoryCache? cache = null)
    {
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Picks the schema service: caching when IMemoryCache is available,
    /// plain otherwise. Behavior without cache is identical to 10.5.x.
    /// </summary>
    private IEtlSchemaService CreateSchemaService(DBTypeEnum dbType) =>
        _cache != null
            ? EtlSchemaServiceFactory.CreateWithCache(dbType, _cache)
            : EtlSchemaServiceFactory.Create(dbType);

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
            var svc = CreateSchemaService(dbType);
            var tables = await svc.ListTablesAsync(cs.Value ?? "", schema, cancellationToken);
            return Ok(tables);
        }
        catch (NotSupportedException nse)
        {
            return StatusCode(501, new { error = nse.Message });
        }
        catch (Exception ex)
        {
            // Never expose connection-string or DB exception detail to the client.
            // Full detail is in the server log; the client receives a generic message.
            _logger.LogError(ex, "Schema introspection (Tables) failed for key '{CsKey}', dbType '{DbType}'", csKey, dbType);
            return StatusCode(500, new { error = "Schema introspection failed; see server log." });
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
            var svc = CreateSchemaService(dbType);
            var columns = await svc.ListColumnsAsync(cs.Value ?? "", table, schema, cancellationToken);
            return Ok(columns);
        }
        catch (NotSupportedException nse)
        {
            return StatusCode(501, new { error = nse.Message });
        }
        catch (Exception ex)
        {
            // Never expose connection-string or DB exception detail to the client.
            // Full detail is in the server log; the client receives a generic message.
            _logger.LogError(ex, "Schema introspection (Columns) failed for key '{CsKey}', table '{Table}', dbType '{DbType}'", csKey, table, dbType);
            return StatusCode(500, new { error = "Schema introspection failed; see server log." });
        }
    }
}
