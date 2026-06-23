using CredentialManagement;

namespace ReleaseNotesHelper.App;

public static class CredentialStore
{
    public static void Save(string target, string username, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return;

        using var credential = new Credential
        {
            Target = target,
            Username = username,
            Password = secret,
            Type = CredentialType.Generic,
            PersistanceType = PersistanceType.LocalComputer
        };

        credential.Save();
    }

    public static string? Load(string target)
    {
        using var credential = new Credential
        {
            Target = target,
            Type = CredentialType.Generic
        };

        return credential.Load()
            ? credential.Password
            : null;
    }

    public static bool Exists(string target)
    {
        using var credential = new Credential
        {
            Target = target,
            Type = CredentialType.Generic
        };

        return credential.Load();
    }

    public static void Delete(string target)
    {
        using var credential = new Credential
        {
            Target = target,
            Type = CredentialType.Generic
        };

        credential.Delete();
    }
}