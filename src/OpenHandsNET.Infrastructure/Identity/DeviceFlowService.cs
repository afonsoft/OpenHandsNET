using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>
/// RFC 8628 device flow (SPEC-08 RF-009). Device codes are stored hashed;
/// approval issues a sk-* API key with <c>Auth:DeviceKeyExpirationDays</c>
/// lifetime (default 7d) named "Device Link Access Key".
/// </summary>
public sealed class DeviceFlowService(
    AppDbContext db,
    IApiKeyService apiKeys,
    IOptions<AuthOptions> options) : IDeviceFlowService
{
    private const int ExpiresInSeconds = 600;
    private const int IntervalSeconds = 5;

    /// <inheritdoc />
    public async Task<DeviceAuthorization> StartAsync(
        string baseUrl, CancellationToken cancellationToken = default)
    {
        var deviceCode = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var userCode = GenerateUserCode();
        var record = new DeviceCodeRecord
        {
            Id = Guid.NewGuid(),
            DeviceCodeHash = Hash(deviceCode),
            UserCode = userCode,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ExpiresInSeconds),
            LastPolledAt = DateTimeOffset.MinValue,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.DeviceCodes.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        var verification = baseUrl.TrimEnd('/') + "/oauth/device/verify";
        return new DeviceAuthorization(
            deviceCode, userCode, verification,
            verification + "?user_code=" + userCode,
            ExpiresInSeconds, IntervalSeconds);
    }

    /// <inheritdoc />
    public async Task<DeviceTokenResult> PollAsync(
        string deviceCode, CancellationToken cancellationToken = default)
    {
        var record = await db.DeviceCodes.FirstOrDefaultAsync(
            d => d.DeviceCodeHash == Hash(deviceCode), cancellationToken);
        if (record is null)
            return new DeviceTokenResult(DeviceTokenStatus.Invalid);

        record.PollCount++;
        record.LastPolledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
            return new DeviceTokenResult(DeviceTokenStatus.Expired);
        if (record.Denied)
            return new DeviceTokenResult(DeviceTokenStatus.Denied);
        if (record.ApprovedByUserId is not { } userId)
            return new DeviceTokenResult(DeviceTokenStatus.Pending);

        db.DeviceCodes.Remove(record);

        var user = await db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            return new DeviceTokenResult(DeviceTokenStatus.Invalid);
        var authUser = new AuthUser(
            user.Id, user.Email, user.DisplayName,
            IsSuperAdmin: false, user.CurrentOrgId, AuthType.Bearer);
        var key = await apiKeys.CreateAsync(
            authUser, "Device Link Access Key", null,
            DateTimeOffset.UtcNow.AddDays(options.Value.DeviceKeyExpirationDays),
            Guid.TryParse(user.CurrentOrgId, out var bound) ? bound : null,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new DeviceTokenResult(DeviceTokenStatus.Approved, key.Plaintext);
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(
        AuthUser user, string userCode, bool approve,
        CancellationToken cancellationToken = default)
    {
        var normalized = userCode.Trim().ToUpperInvariant();
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var record = await db.DeviceCodes.FirstOrDefaultAsync(
            d => d.UserCode == normalized && d.ExpiresAtTicks > nowTicks,
            cancellationToken);
        if (record is null)
            return false;
        record.ApprovedByUserId = user.UserId;
        record.ApprovedAt = DateTimeOffset.UtcNow;
        record.Denied = !approve;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string GenerateUserCode()
    {
        const string alphabet = "BCDFGHJKLMNPQRSTVWXZ";
        var chars = new char[8];
        var bytes = RandomNumberGenerator.GetBytes(8);
        for (var i = 0; i < 8; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars[..4]) + "-" + new string(chars[4..]);
    }

    private static string Hash(string deviceCode) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceCode)));
}