using System;
using Windows.Security.Credentials;

namespace SmartScreenshotManager.Services
{
    public sealed class ApiKeyStore
    {
        private const string Resource = "SmartScreenshotManager.OpenAI";
        private const string User = "OpenAI API key";
        public string Read()
        {
            try
            {
                var credential = new PasswordVault().Retrieve(Resource, User);
                credential.RetrievePassword();
                return credential.Password;
            }
            catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490))
            {
                return string.Empty; // Credential not found.
            }
        }
        public void Save(string key) => new PasswordVault().Add(new PasswordCredential(Resource, User, key));
        public void Remove()
        {
            var vault = new PasswordVault();
            try { vault.Remove(vault.Retrieve(Resource, User)); }
            catch (Exception exception) when (exception.HResult == unchecked((int)0x80070490)) { }
        }
    }
}
