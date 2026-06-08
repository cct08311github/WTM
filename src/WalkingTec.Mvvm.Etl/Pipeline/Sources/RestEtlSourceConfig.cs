#nullable enable
using System;
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Etl.Pipeline.Sources;

/// <summary>
/// Pagination strategy for <see cref="RestEtlSource"/>.
/// </summary>
public enum RestPaginationStrategy
{
    /// <summary>
    /// Page-number based: appends <c>?{PageParam}=N</c> to every request;
    /// stops when fewer than <see cref="RestEtlSourceConfig.BatchSize"/> records are returned
    /// or the page response is empty.
    /// </summary>
    PageNumber = 0,

    /// <summary>
    /// Offset/limit based: appends <c>?{OffsetParam}=N&amp;{LimitParam}=batchSize</c>;
    /// stops when the page is empty or shorter than the requested limit.
    /// </summary>
    Offset = 1,

    /// <summary>
    /// Next-link / cursor based: reads the link for the next page from a configurable
    /// JSON field in the response root (e.g. <c>"next"</c>, <c>"next_page_token"</c>);
    /// stops when that field is absent, null, or an empty string.
    /// </summary>
    NextLink = 2,
}

/// <summary>
/// Configuration for <see cref="RestEtlSource"/>.
/// Serialise this as JSON and pass as the <c>connectionString</c> parameter of
/// <see cref="WalkingTec.Mvvm.Etl.Pipeline.IEtlSource.ExtractBatchesAsync"/>.
/// </summary>
public sealed class RestEtlSourceConfig
{
    // ── Endpoint ────────────────────────────────────────────────────────

    /// <summary>Base URL of the REST endpoint. Must be absolute (https:// by default).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Allow plain http:// URLs.  Defaults to <c>false</c>.
    /// Enable only for internal / trusted endpoints.
    /// </summary>
    public bool AllowHttp { get; set; } = false;

    // ── Authentication ──────────────────────────────────────────────────

    /// <summary>
    /// Additional request headers (e.g. <c>"Authorization": "Bearer …"</c>).
    /// Values are never logged — treat as secrets.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    // ── JSON mapping ────────────────────────────────────────────────────

    /// <summary>
    /// Dot-separated JSON path to the records array within the response body.
    /// Supports a leading <c>$.</c> prefix (e.g. <c>"$.data"</c> or <c>"data"</c>).
    /// Leave empty or <c>"$"</c> to treat the entire response as the array.
    /// If the target is an array of objects, each object becomes a DataTable row;
    /// if the target is a plain array of scalars, rows will have a single <c>value</c> column.
    /// </summary>
    public string RecordsPath { get; set; } = string.Empty;

    // ── Pagination ──────────────────────────────────────────────────────

    /// <summary>Pagination strategy. Default <see cref="RestPaginationStrategy.PageNumber"/>.</summary>
    public RestPaginationStrategy PaginationStrategy { get; set; } = RestPaginationStrategy.PageNumber;

    /// <summary>
    /// Query-string parameter name for the page number (used when
    /// <see cref="PaginationStrategy"/> is <see cref="RestPaginationStrategy.PageNumber"/>).
    /// Default <c>"page"</c>.
    /// </summary>
    public string PageParam { get; set; } = "page";

    /// <summary>
    /// First page number.  Typically <c>1</c> (most APIs) or <c>0</c>.
    /// Default <c>1</c>.
    /// </summary>
    public int FirstPage { get; set; } = 1;

    /// <summary>
    /// Query-string parameter name for the offset
    /// (<see cref="RestPaginationStrategy.Offset"/> mode). Default <c>"offset"</c>.
    /// </summary>
    public string OffsetParam { get; set; } = "offset";

    /// <summary>
    /// Query-string parameter name for the page size
    /// (<see cref="RestPaginationStrategy.Offset"/> mode). Default <c>"limit"</c>.
    /// </summary>
    public string LimitParam { get; set; } = "limit";

    /// <summary>
    /// Dot-separated JSON path within the response root that holds the URL or
    /// cursor token for the next page
    /// (<see cref="RestPaginationStrategy.NextLink"/> mode).
    /// Typical values: <c>"next"</c>, <c>"meta.next_cursor"</c>.
    /// Default <c>"next"</c>.
    /// </summary>
    public string NextLinkField { get; set; } = "next";

    // ── Rate limiting ────────────────────────────────────────────────────

    /// <summary>
    /// Delay in milliseconds between page requests.  Zero means no delay.
    /// Clamped to [0, 60 000]. Default <c>0</c>.
    /// </summary>
    public int PageDelayMs { get; set; } = 0;

    // ── HTTP tuning ──────────────────────────────────────────────────────

    /// <summary>
    /// Per-request timeout in seconds. Clamped to [1, 300]. Default <c>30</c>.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum response body in bytes. Clamped to [1 024, 100 MiB]. Default 10 MiB.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum number of pages to fetch per run. Zero means unlimited.
    /// Default <c>0</c> (unlimited).
    /// </summary>
    public int MaxPages { get; set; } = 0;
}
