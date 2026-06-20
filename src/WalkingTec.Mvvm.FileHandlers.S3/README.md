# WalkingTec.Mvvm.FileHandlers.S3

Opt-in S3/MinIO file handler for the WTM framework.  
Add the NuGet package to your app project and register via `AddWtmS3FileHandler`.

## Quick Start

### MinIO (self-hosted)

```csharp
// Program.cs / Startup.cs
services.AddWtmS3FileHandler(o =>
{
    o.ServiceUrl     = "http://localhost:9000";
    o.BucketName     = "my-bucket";
    o.AccessKey      = "minioadmin";
    o.SecretKey      = "minioadmin";
    o.ForcePathStyle = true;          // required for MinIO
});
```

### AWS S3

```csharp
services.AddWtmS3FileHandler(o =>
{
    o.BucketName = "my-aws-bucket";
    o.AccessKey  = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")!;
    o.SecretKey  = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")!;
    o.Region     = "ap-northeast-1";
});
```

## Options

| Property | Default | Description |
|---|---|---|
| `ServiceUrl` | `null` | S3-compatible base URL (MinIO: `http://host:9000`). Omit for AWS. |
| `BucketName` | *(required)* | Target bucket. |
| `AccessKey` | *(required)* | Access Key ID. |
| `SecretKey` | *(required)* | Secret Access Key. |
| `Region` | `"us-east-1"` | AWS region name. Ignored when `ServiceUrl` is set. |
| `ForcePathStyle` | `false` | Set `true` for MinIO and other path-style stores. |
| `KeyPrefix` | `null` | Optional prefix, e.g. `"uploads/"`. |

## How It Works

`WtmS3FileHandler` implements `IWtmFileHandler` and:

- **Upload**: PutObject → returns the generated S3 key as `path` and `"s3"` as `handlerInfo`.
- **GetFileData**: GetObject → streams content into a `MemoryStream`.
- **DeleteFile**: DeleteObject.

`AddWtmS3FileHandler` registers `IAmazonS3` as **singleton** and the handler as **scoped** `IWtmFileHandler`.  
Core does not gain an AWSSDK dependency — it lives only in this package.
