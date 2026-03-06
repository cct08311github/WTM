# Structured Logging & ProblemDetails (v8.2.0)

> **Opt-in features.** Existing applications work unchanged without these additions.
> WTMLogger and ActionLog continue to function as before.

---

## 1. Quick Start

Add three lines to your `Startup.cs` to enable structured logging and RFC 7807 error responses.

### ConfigureServices

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddDistributedMemoryCache();
    services.AddWtmSession(3600, ConfigRoot);
    services.AddWtmCrossDomain(ConfigRoot);
    services.AddWtmAuthentication(ConfigRoot);
    services.AddWtmHttpClient(ConfigRoot);
    services.AddWtmSwagger(true);
    services.AddWtmMultiLanguages(ConfigRoot);
    services.AddWtmSerilog(ConfigRoot);                          // <-- NEW

    services.AddMvc(options => { options.UseWtmMvcOptions(); })
        .AddJsonOptions(options => { options.UseWtmJsonOptions(); })
        .ConfigureApiBehaviorOptions(options => { options.UseWtmApiOptions(); })
        .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
        .AddWtmDataAnnotationsLocalization(typeof(Program));

    services.AddWtmContext(ConfigRoot, (options) => { /* ... */ });
}
```

### Configure

```csharp
public void Configure(IApplicationBuilder app, IOptionsMonitor<Configs> configs)
{
    app.UseWtmProblemDetails();                                  // <-- NEW (before ExceptionHandler)
    app.UseExceptionHandler(configs.CurrentValue.ErrorHandler);
    app.UseStaticFiles();
    app.UseWtmStaticFiles();
    app.UseRouting();
    app.UseWtmSerilog();                                         // <-- NEW (after Routing)
    app.UseWtmMultiLanguages();
    app.UseWtmCrossDomain();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseSession();
    app.UseWtmSwagger();
    app.UseWtm();
    app.UseEndpoints(endpoints => { /* ... */ });
    app.UseWtmContext();
}
```

### Expected Console Output

```
[14:23:01 INF] Now listening on: https://localhost:5001
[14:23:05 INF] HTTP GET /api/Student 200 in 42.3ms
[14:23:06 INF] HTTP POST /api/Student/Add 200 in 118.7ms
[14:23:07 ERR] HTTP GET /api/Student/999 500 in 5.1ms
```

A JSON log file is also written to `logs/wtm-20260306.log` (daily rolling).

### Required Using

Add this to the top of `Startup.cs`:

```csharp
using WalkingTec.Mvvm.Mvc;
```

The `AddWtmSerilog`, `UseWtmSerilog`, and `UseWtmProblemDetails` extension methods are in the `WalkingTec.Mvvm.Mvc` namespace.

---

## 2. Configuration Options

### WtmSerilogOptions Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EnableConsole` | `bool` | `true` | Write human-readable logs to stdout |
| `EnableFile` | `bool` | `true` | Write JSON logs to a rolling file |
| `FilePath` | `string` | `"logs/wtm-.log"` | File sink path. Serilog appends the date before `.log` |
| `FileRollingInterval` | `RollingInterval` | `Day` | How often to roll the log file |
| `FileRetainedCount` | `int` | `30` | Number of old log files to keep |
| `MinimumLevel` | `LogLevel` | `Information` | Minimum severity to record |
| `EnableRequestLogging` | `bool` | `true` | Log each HTTP request (method, path, status, duration) |
| `CustomConfig` | `Action<LoggerConfiguration>?` | `null` | Escape hatch: add custom sinks or enrichers |

### Custom Configuration Example

```csharp
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.FilePath = "D:/logs/myapp/wtm-.log";
    options.FileRetainedCount = 60;
    options.MinimumLevel = LogLevel.Debug;
    options.EnableConsole = false;  // disable console in production
});
```

---

## 3. Production Recommendations (IIS)

### Use Absolute Paths for FilePath

IIS sets the working directory to the .NET runtime folder, not your application folder. Relative paths like `logs/wtm-.log` will write to an unexpected location or fail silently.

```csharp
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.FilePath = @"D:\logs\MyApp\wtm-.log";   // absolute path
});
```

### Disk Space Estimation

Each JSON log line is roughly 500 bytes. The table below estimates daily log file sizes based on request volume.

| Concurrent Users | Requests/Day (est.) | Daily Log Size | 30-Day Total |
|------------------|---------------------|----------------|--------------|
| 50 | ~50,000 | ~25 MB | ~750 MB |
| 100 | ~100,000 | ~50 MB | ~1.5 GB |
| 500 | ~500,000 | ~250 MB | ~7.5 GB |

Adjust `FileRetainedCount` to match your available disk space.

### IIS App Pool Permissions

The IIS application pool identity (typically `IIS AppPool\YourAppPool`) must have **write permission** on the log directory.

```powershell
# PowerShell (run as Administrator)
$acl = Get-Acl "D:\logs\MyApp"
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "IIS AppPool\YourAppPool", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl "D:\logs\MyApp" $acl
```

### Retention Strategy

For most deployments, 30 days of logs is sufficient. If you need longer retention for compliance, consider shipping logs to a centralized system (see Section 6) and keeping local retention short.

---

## 4. Querying Logs

Log files are JSON (one object per line), making them easy to query with `jq` or PowerShell.

### Filter Errors

```bash
cat logs/wtm-20260306.log | jq 'select(.Level == "Error")'
```

### Find by TraceId

When a user reports an error, the API response includes a `traceId`. Use it to find the full server-side context:

```bash
cat logs/wtm-20260306.log | jq 'select(.Properties.RequestId == "0HN8Q1ABC456")'
```

### Find Slow Requests (over 1 second)

```bash
cat logs/wtm-20260306.log | jq 'select(.Properties.Elapsed > 1000)'
```

### Filter by URL Path

```bash
cat logs/wtm-20260306.log | jq 'select(.Properties.RequestPath | test("/api/Student"))'
```

### PowerShell Alternative

```powershell
Get-Content logs\wtm-20260306.log |
    ConvertFrom-Json |
    Where-Object { $_.Level -eq "Error" } |
    Format-Table Timestamp, Properties
```

### TraceId Correlation Flow

```
1. Frontend calls POST /api/Student/Add
2. Server returns 500 with JSON body:
   { "status": 500, "traceId": "0HN8Q1ABC456", ... }
3. Frontend logs or displays the traceId to the user
4. Developer queries:
   cat logs/wtm-20260306.log | jq 'select(.Properties.RequestId == "0HN8Q1ABC456")'
5. Full exception stack trace + request context found
```

---

## 5. ProblemDetails API Error Format

`UseWtmProblemDetails()` intercepts error responses for API routes and returns [RFC 7807](https://tools.ietf.org/html/rfc7807) JSON.

### When Does It Activate?

- Request path starts with `/api/` **OR**
- Request `Accept` header contains `application/json`

MVC page requests (HTML) are **not affected** and continue to use the existing `UseExceptionHandler` behavior.

### 400 Bad Request

```json
{
  "type": "https://httpstatuses.io/400",
  "title": "Bad Request",
  "status": 400,
  "detail": "Bad Request.",
  "traceId": "0HN8Q1ABC123"
}
```

### 401 Unauthorized

```json
{
  "type": "https://httpstatuses.io/401",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Unauthorized.",
  "traceId": "0HN8Q1ABC789"
}
```

### 500 Internal Server Error

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "Internal Server Error",
  "status": 500,
  "detail": "An unexpected error occurred.",
  "traceId": "0HN8Q1ABC456"
}
```

### IsQuickDebug Behavior

The `IsQuickDebug` setting in `appsettings.json` controls how much detail is exposed in 500 responses.

| Field | `IsQuickDebug = true` | `IsQuickDebug = false` |
|-------|----------------------|------------------------|
| `detail` | Full `Exception.ToString()` | `"An unexpected error occurred."` |
| `exception` | Full stack trace included | Field omitted |
| `traceId` | Always included | Always included |

**With IsQuickDebug enabled (development):**

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "Internal Server Error",
  "status": 500,
  "detail": "System.NullReferenceException: Object reference not set...",
  "traceId": "0HN8Q1ABC456",
  "exception": "System.NullReferenceException: Object reference not set..."
}
```

### Frontend Error Handling Example

```javascript
async function wtmApiFetch(url, options = {}) {
    const resp = await fetch(url, {
        ...options,
        headers: {
            'Accept': 'application/json',
            'Content-Type': 'application/json',
            ...options.headers
        }
    });

    if (!resp.ok) {
        const problem = await resp.json();
        // problem.status  = 400, 401, 500, etc.
        // problem.title   = "Bad Request", "Unauthorized", etc.
        // problem.traceId = server correlation ID
        console.error(`[${problem.status}] ${problem.title} (traceId: ${problem.traceId})`);
        throw problem;
    }

    return resp.json();
}
```

---

## 6. Advanced: Custom Sinks

Use the `CustomConfig` callback to add any Serilog sink alongside the built-in Console and File sinks.

### Seq

Install the NuGet package in your application project:

```bash
dotnet add package Serilog.Sinks.Seq
```

```csharp
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.CustomConfig = cfg =>
        cfg.WriteTo.Seq("http://localhost:5341");
});
```

### Elasticsearch

```bash
dotnet add package Serilog.Sinks.Elasticsearch
```

```csharp
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.CustomConfig = cfg =>
        cfg.WriteTo.Elasticsearch(new Serilog.Sinks.Elasticsearch.ElasticsearchSinkOptions(
            new Uri("http://localhost:9200"))
        {
            IndexFormat = "wtm-logs-{0:yyyy.MM.dd}",
            AutoRegisterTemplate = true
        });
});
```

### appsettings.json-Driven Configuration

For environments where sink configuration must change without recompilation, use `ReadFrom.Configuration`:

```csharp
services.AddWtmSerilog(ConfigRoot, options =>
{
    options.CustomConfig = cfg =>
        cfg.ReadFrom.Configuration(ConfigRoot);
});
```

Then in `appsettings.json`:

```json
{
  "Serilog": {
    "WriteTo": [
      {
        "Name": "Seq",
        "Args": { "serverUrl": "http://seq-server:5341" }
      }
    ]
  }
}
```

This adds the `appsettings.json`-defined sinks **in addition to** the built-in Console and File sinks. To use only `appsettings.json`-driven sinks, set `EnableConsole = false` and `EnableFile = false`.

---

## 7. FAQ

### Does this replace ActionLog / WTMLogger?

No. `AddWtmSerilog` registers Serilog as an **additional** logging provider. WTMLogger continues to write to the `ActionLog` database table. Both systems coexist without conflict.

### Can I disable WTMLogger if I only want Serilog?

WTMLogger is registered internally by `AddWtmContext`. You can suppress it by setting `ActionLogLevel = ActionLogTypesEnum.None` in your `appsettings.json`:

```json
{
  "ActionLogLevel": "None"
}
```

This stops ActionLog database writes while Serilog handles all logging.

### How do I manage log file size?

Three controls are available:

- **`FileRetainedCount`** (default 30): Serilog deletes files older than this count automatically.
- **`FileRollingInterval`** (default `Day`): Set to `Hour` for high-traffic systems for smaller individual files.
- **`MinimumLevel`** (default `Information`): Set to `Warning` in production to reduce volume significantly.

### Does UseWtmProblemDetails affect MVC Razor pages?

No. ProblemDetails only activates for requests where the path starts with `/api/` or the `Accept` header contains `application/json`. Standard MVC page requests continue to use `UseExceptionHandler` and return HTML error pages as before.

### Can I configure everything from appsettings.json instead of code?

Yes. Use the `CustomConfig` callback with `ReadFrom.Configuration` as shown in Section 6. The built-in options (`EnableConsole`, `FilePath`, etc.) are code-only, but any additional sinks, enrichers, or level overrides can be driven from `appsettings.json`.
