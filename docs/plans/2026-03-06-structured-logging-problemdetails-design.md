# Phase 1 Design: Structured Logging + ProblemDetails (v8.2.0)

> Date: 2026-03-06
> Status: Approved
> Scope: Non-breaking, opt-in additions

## Context

WTM's current logging (`WTMLogger`) writes only to database (`ActionLog` table) and silently swallows persistence errors. Production debugging requires querying the DB, which is slow and unreliable when the DB itself is the problem. Exception responses for API routes return plain text, making programmatic error handling difficult for frontend consumers.

## Decision

Add Serilog structured logging and RFC 7807 ProblemDetails as **opt-in, additive** features. Existing `WTMLogger` and `FrameworkFilter` remain untouched.

## Architecture

```
User Application (Startup.cs)
  │
  ├─ services.AddWtmSerilog(ConfigRoot)     ← NEW (opt-in)
  ├─ app.UseWtmProblemDetails()             ← NEW (opt-in)
  │
  ├─ WTMLogger → ActionLog DB              ← UNCHANGED
  └─ FrameworkFilter                        ← UNCHANGED

Serilog Pipeline:
  ILoggerFactory ──► Console Sink (dev)
                 ──► File Sink (JSON, rolling daily, 30-day retention)
                 ──► Custom Sinks (user-configurable: Seq, Elasticsearch, etc.)

ProblemDetails Middleware:
  Request ──► Is API route? ──► Yes → RFC 7807 JSON response
                             └─ No  → Pass through to FrameworkFilter / UseExceptionHandler
```

## Package Dependencies

Added to `WalkingTec.Mvvm.Mvc.csproj`:

```xml
<PackageReference Include="Serilog.AspNetCore" Version="8.0.3" />
```

This meta-package includes: `Serilog.Sinks.Console`, `Serilog.Sinks.File`, `Serilog.Extensions.Hosting`.

## New Files

| File | Purpose |
|------|---------|
| `src/WalkingTec.Mvvm.Mvc/Helper/WtmSerilogExtension.cs` | `AddWtmSerilog()` + `UseWtmSerilog()` extension methods |
| `src/WalkingTec.Mvvm.Mvc/Helper/WtmProblemDetailsExtension.cs` | `AddWtmProblemDetails()` + `UseWtmProblemDetails()` extension methods |
| `docs/structured-logging.md` | Developer usage manual with code examples |
| `test/WalkingTec.Mvvm.Core.Test/SerilogExtensionTests.cs` | Extension method registration tests |
| `test/WalkingTec.Mvvm.Core.Test/ProblemDetailsTests.cs` | Error response format tests |

## Modified Files

None. This is a purely additive change.

## API Design

### WtmSerilogOptions

```csharp
public class WtmSerilogOptions
{
    public bool EnableConsole { get; set; } = true;
    public bool EnableFile { get; set; } = true;
    public string FilePath { get; set; } = "logs/wtm-.log";
    public RollingInterval FileRollingInterval { get; set; } = RollingInterval.Day;
    public int FileRetainedCount { get; set; } = 30;
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public bool EnableRequestLogging { get; set; } = true;
    public Action<LoggerConfiguration>? CustomConfig { get; set; }
}
```

### AddWtmSerilog Usage

```csharp
// Zero-config (sensible defaults)
services.AddWtmSerilog(ConfigRoot);

// Custom
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.FilePath = "D:/logs/wtm-.log";
    options.FileRetainedCount = 60;
    options.CustomConfig = cfg => cfg.WriteTo.Seq("http://localhost:5341");
});
```

### AddWtmSerilog Behavior

| Feature | Detail |
|---------|--------|
| Console Sink | `[HH:mm:ss Level] Message {Properties}` |
| File Sink | JSON format, daily rolling, configurable retention |
| Request Logging | HTTP Method/Path/StatusCode/Duration per request |
| Enrichment | MachineName, ThreadId, RequestId (= traceId) |
| Coexistence | WTMLogger continues to receive log events alongside Serilog |

### File Sink Output Example

```json
{
  "Timestamp": "2026-03-06T14:23:01.234+08:00",
  "Level": "Error",
  "MessageTemplate": "Unhandled exception in {ActionName}",
  "Properties": {
    "ActionName": "StudentController.Edit",
    "RequestId": "0HN8Q1...",
    "RequestPath": "/api/Student/Edit",
    "StatusCode": 500,
    "MachineName": "WEB-01",
    "ITCode": "admin"
  },
  "Exception": "System.NullReferenceException: ..."
}
```

## ProblemDetails Design

### Route Detection

1. Request path starts with `/api/` → ProblemDetails JSON
2. Request `Accept` header contains `application/json` → ProblemDetails JSON
3. Otherwise → pass through to downstream handlers (unchanged behavior)

### Response Examples

**400 Validation Error:**
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "traceId": "0HN8Q1ABC123",
  "errors": {
    "Name": ["The Name field is required."]
  }
}
```

**500 Internal Error (production):**
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "Internal Server Error",
  "status": 500,
  "detail": "An unexpected error occurred.",
  "traceId": "0HN8Q1ABC456"
}
```

**500 Internal Error (IsQuickDebug=true):**
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "Internal Server Error",
  "status": 500,
  "detail": "System.NullReferenceException: Object reference...",
  "traceId": "0HN8Q1ABC456",
  "exception": "System.NullReferenceException: Object reference..."
}
```

### QuickDebug Behavior

| Scenario | IsQuickDebug=true | IsQuickDebug=false |
|----------|-------------------|-------------------|
| 500 detail | Full Exception.ToString() | Fixed "An unexpected error occurred." |
| 500 exception field | Included | Omitted |
| 400 detail | Validation details | Validation details (same) |
| traceId | Included | Included |

### TraceId Correlation Flow

```
Frontend receives 500 → response contains traceId: "0HN8Q1ABC456"
  ↓
Backend log query: cat logs/wtm-20260306.log | jq 'select(.Properties.RequestId=="0HN8Q1ABC456")'
  ↓
Full Exception stack trace + request context found
```

## Test Plan

### SerilogExtensionTests (6 tests)

| Test | Validates |
|------|-----------|
| `AddWtmSerilog_registers_serilog_provider` | ILoggerFactory contains Serilog provider |
| `AddWtmSerilog_default_options_are_sane` | Defaults: Console=true, File=true, 30-day retention |
| `AddWtmSerilog_custom_options_applied` | Custom FilePath, MinimumLevel honored |
| `AddWtmSerilog_coexists_with_WTMLogger` | Both providers registered simultaneously |
| `AddWtmSerilog_custom_config_action_invoked` | CustomConfig delegate called |
| `not_calling_AddWtmSerilog_preserves_existing_behavior` | No behavioral change without opt-in |

### ProblemDetailsTests (7 tests)

| Test | Validates |
|------|-----------|
| `api_route_exception_returns_problemdetails_json` | /api/ path 500 → RFC 7807 JSON |
| `api_route_400_includes_validation_errors` | Validation errors in `errors` field |
| `api_route_500_hides_exception_in_production` | No stack trace when IsQuickDebug=false |
| `api_route_500_shows_exception_in_debug` | Exception detail when IsQuickDebug=true |
| `mvc_route_exception_falls_through` | Non-API exceptions not intercepted |
| `problemdetails_includes_traceId` | traceId always present |
| `accept_json_header_triggers_problemdetails` | Accept: application/json → ProblemDetails |

## Documentation

`docs/structured-logging.md` — developer usage manual containing:

1. Quick Start (5-minute setup with Startup.cs example)
2. Configuration Options (WtmSerilogOptions property table)
3. Production Recommendations (IIS paths, disk estimation, retention)
4. Log Query Techniques (jq, PowerShell, traceId lookup)
5. ProblemDetails API Error Format (400/401/403/404/500 examples)
6. Advanced: Custom Sinks (Seq, Elasticsearch examples)
7. FAQ (coexistence with WTMLogger, disabling WTMLogger, file size management)

Estimated ~250 lines Markdown with 15+ copy-paste code examples.

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Serilog package size bloat | `Serilog.AspNetCore` is ~2MB total, acceptable |
| Existing behavior change | Pure opt-in; zero changes without calling AddWtmSerilog |
| Log file disk usage | Default 30-day retention + daily rolling; documented sizing guidance |
| ProblemDetails breaks frontend | Only affects API routes; MVC pages unchanged |

## Rollback

Remove the two `AddWtmSerilog` / `UseWtmProblemDetails` lines from Startup.cs. No data migration needed.
