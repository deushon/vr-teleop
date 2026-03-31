using System;

[Serializable]
public class TeleopLoginRequest
{
    public string login;
    public string password;
}

[Serializable]
public class TeleopUserDto
{
    public string id;
    public string login;
    public string walletPublicKey;
    public string createdAt;
}

[Serializable]
public class TeleopLoginResponse
{
    public bool ok;
    public TeleopUserDto user;
    public string accessToken;
}

public static class TeleopAuthSession
{
    public static bool IsAuthorized { get; private set; }

    public static bool Ok { get; private set; }
    public static string AccessToken { get; private set; }

    public static string UserId { get; private set; }
    public static string UserLogin { get; private set; }
    public static string WalletPublicKey { get; private set; }
    public static string CreatedAtRaw { get; private set; }

    public static DateTime? CreatedAtUtc
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CreatedAtRaw))
                return null;

            if (DateTime.TryParse(
                    CreatedAtRaw,
                    null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var dt))
            {
                return dt;
            }

            return null;
        }
    }

    public static void SetFromResponse(TeleopLoginResponse response)
    {
        if (response == null)
        {
            Clear();
            return;
        }

        Ok = response.ok;
        AccessToken = response.accessToken ?? string.Empty;

        UserId = response.user?.id ?? string.Empty;
        UserLogin = response.user?.login ?? string.Empty;
        WalletPublicKey = response.user?.walletPublicKey ?? string.Empty;
        CreatedAtRaw = response.user?.createdAt ?? string.Empty;

        IsAuthorized = response.ok && !string.IsNullOrWhiteSpace(AccessToken);
    }

    public static void Clear()
    {
        IsAuthorized = false;
        Ok = false;
        AccessToken = string.Empty;

        UserId = string.Empty;
        UserLogin = string.Empty;
        WalletPublicKey = string.Empty;
        CreatedAtRaw = string.Empty;
    }
}