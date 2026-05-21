using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameTrackerPC.Services;

public sealed class DriveBackupFile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset? CreatedTime { get; set; }
    public long? Size { get; set; }

    public string CreatedText => CreatedTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string SizeText => Size is null ? "-" : FormatSize(Size.Value);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024d;
        if (kb < 1024)
        {
            return $"{kb:0.0} KB";
        }

        return $"{kb / 1024d:0.0} MB";
    }
}

public sealed class GoogleDriveBackupService
{
    private const string FolderName = "GameVault";
    private const string ZipMimeType = "application/zip";
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private DriveService? _driveService;
    private string? _folderId;

    public bool IsConnected => _driveService is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var secretPath = AppPaths.FindGoogleClientSecret()
            ?? throw new FileNotFoundException("Google OAuth desktop client JSON was not found in JsonSecret.");

        await using var stream = File.OpenRead(secretPath);
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            [DriveService.Scope.DriveFile],
            "user",
            cancellationToken,
            new FileDataStore(AppPaths.GoogleTokenDirectory, fullPath: true));

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "GameVault Desktop"
        });

        _folderId = await EnsureFolderAsync(cancellationToken);
    }

    public void Disconnect()
    {
        _driveService?.Dispose();
        _driveService = null;
        _folderId = null;

        if (Directory.Exists(AppPaths.GoogleTokenDirectory))
        {
            Directory.Delete(AppPaths.GoogleTokenDirectory, recursive: true);
        }
    }

    public async Task<DriveBackupFile> UploadZipBackupAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        var service = RequireService();
        var folderId = await EnsureFolderAsync(cancellationToken);
        var metadata = new DriveFile
        {
            Name = Path.GetFileName(zipPath),
            Parents = [folderId]
        };

        await using var stream = File.OpenRead(zipPath);
        var request = service.Files.Create(metadata, stream, ZipMimeType);
        request.Fields = "id,name,createdTime,size";
        var progress = await request.UploadAsync(cancellationToken);
        if (progress.Status == UploadStatus.Failed)
        {
            throw progress.Exception ?? new InvalidOperationException("Google Drive upload failed.");
        }

        return MapDriveFile(request.ResponseBody);
    }

    public async Task<IReadOnlyList<DriveBackupFile>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var service = RequireService();
        var folderId = await EnsureFolderAsync(cancellationToken);
        var request = service.Files.List();
        request.Q = $"'{folderId}' in parents and trashed=false and mimeType='{ZipMimeType}'";
        request.OrderBy = "createdTime desc";
        request.Fields = "files(id,name,createdTime,size)";
        request.Spaces = "drive";
        var result = await request.ExecuteAsync(cancellationToken);
        return result.Files.Select(MapDriveFile).ToList();
    }

    public async Task<string> DownloadBackupAsync(
        string fileId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var service = RequireService();
        await using var stream = File.Create(destinationPath);
        var request = service.Files.Get(fileId);
        await request.DownloadAsync(stream, cancellationToken);
        return destinationPath;
    }

    public async Task TrashBackupAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var service = RequireService();
        var file = new DriveFile { Trashed = true };
        await service.Files.Update(file, fileId).ExecuteAsync(cancellationToken);
    }

    private async Task<string> EnsureFolderAsync(CancellationToken cancellationToken)
    {
        if (_folderId is not null)
        {
            return _folderId;
        }

        var service = RequireService();
        var list = service.Files.List();
        list.Q = $"mimeType='{FolderMimeType}' and name='{FolderName}' and 'root' in parents and trashed=false";
        list.Fields = "files(id,name)";
        list.Spaces = "drive";
        var existing = await list.ExecuteAsync(cancellationToken);
        var folder = existing.Files.FirstOrDefault();
        if (folder is not null)
        {
            _folderId = folder.Id;
            return folder.Id;
        }

        var create = service.Files.Create(new DriveFile
        {
            Name = FolderName,
            MimeType = FolderMimeType,
            Parents = ["root"]
        });
        create.Fields = "id";
        var created = await create.ExecuteAsync(cancellationToken);
        _folderId = created.Id;
        return created.Id;
    }

    private DriveService RequireService() =>
        _driveService ?? throw new InvalidOperationException("Connect Google Drive account first.");

    private static DriveBackupFile MapDriveFile(DriveFile file) => new()
    {
        Id = file.Id,
        Name = file.Name,
        CreatedTime = file.CreatedTimeDateTimeOffset,
        Size = file.Size
    };
}
