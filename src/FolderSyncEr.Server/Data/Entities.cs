using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolderSyncEr.Server.Data;

[Table("Users")]
public class User
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = "User";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginUtc { get; set; }

    public bool IsDisabled { get; set; }
}

[Table("Devices")]
public class Device
{
    [Key]
    [MaxLength(64)]
    public string Id { get; set; } = string.Empty;

    public int UserId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public bool IsRevoked { get; set; }
}

[Table("RefreshTokens")]
public class RefreshToken
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [Required]
    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }

    public int? ReplacedByTokenId { get; set; }

    [NotMapped]
    public bool IsActive => RevokedUtc == null && DateTime.UtcNow < ExpiresUtc;
}

[Table("Files")]
public class FileRecord
{
    [Key]
    [MaxLength(64)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int UserId { get; set; }

    [Required]
    [MaxLength(1024)]
    public string Path { get; set; } = string.Empty;

    [Required]
    [MaxLength(1024)]
    public string PathKey { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    [MaxLength(64)]
    public string? Sha256 { get; set; }

    public long Version { get; set; } = 1;

    public DateTime LastModifiedUtc { get; set; }

    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? UploadedByDeviceId { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    public long ChangeSeq { get; set; }
}

[Table("FileVersions")]
public class FileVersionRecord
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string FileId { get; set; } = string.Empty;

    public long Version { get; set; }

    [Required]
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    [Required]
    public string BlobRelativePath { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? DeviceId { get; set; }
}

[Table("ActivityLog")]
public class ActivityLog
{
    [Key]
    public int Id { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public int? UserId { get; set; }

    [MaxLength(64)]
    public string? DeviceId { get; set; }

    [Required]
    [MaxLength(50)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Path { get; set; }

    [Required]
    [MaxLength(20)]
    public string Result { get; set; } = string.Empty;

    public string? Message { get; set; }

    [MaxLength(100)]
    public string? CorrelationId { get; set; }
}

[Table("ApiSettings")]
public class ApiSetting
{
    [Key]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

[Table("UserCounters")]
public class UserCounter
{
    [Key]
    public int UserId { get; set; }

    public long LastChangeSeq { get; set; }
}

