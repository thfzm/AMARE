using Windows.Security.Credentials;

namespace Amare.Windows;

internal static class AiCredentialStore
{
    private const string Resource = "AMARE.AI.OpenAI";
    private const string UserName = "openai";

    public static string? GetKey()
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.FindAllByResource(Resource)
                .FirstOrDefault(item => item.UserName == UserName);
            if (credential is null) return null;
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password) ? null : credential.Password;
        }
        catch
        {
            return null;
        }
    }

    public static bool Save(string key)
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var old in vault.FindAllByResource(Resource).ToArray())
            {
                vault.Remove(old);
            }
            vault.Add(new PasswordCredential(Resource, UserName, key));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Delete()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var old in vault.FindAllByResource(Resource).ToArray())
            {
                vault.Remove(old);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
