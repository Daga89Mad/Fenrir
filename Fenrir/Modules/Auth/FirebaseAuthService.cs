using FirebaseAdmin.Auth;

public class FirebaseAuthService
{
    public async Task<string?> LoginAsync(string email, string password)
    {
        try
        {
            // Firebase no permite login directo desde backend
            // pero sí permite verificar tokens o crear usuarios.
            // Para login, usamos el endpoint REST de Firebase Auth.

            using var client = new HttpClient();

            var apiKey = "AIzaSyD9jA4lkL-WJiPtlLBC1Dl-_RFrppJUY6s"; // la de Web API Key
            var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={apiKey}";

            var payload = new
            {
                email,
                password,
                returnSecureToken = true
            };

            var response = await client.PostAsJsonAsync(url, payload);

            if (!response.IsSuccessStatusCode)
                return null;

            var data = await response.Content.ReadFromJsonAsync<LoginResponse>();

            return data?.LocalId; // UID del usuario
        }
        catch
        {
            return null;
        }
    }
}

public class LoginResponse
{
    public string LocalId { get; set; }
    public string IdToken { get; set; }
}
