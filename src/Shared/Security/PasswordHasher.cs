namespace RemoteDesktop.Shared.Security;

public interface IPasswordHasher
{
    string HashPassword(string password);
    bool Verify(string password, string hashedPassword);
}

public sealed class BCryptPasswordHasher : IPasswordHasher
{
    public string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public bool Verify(string password, string hashedPassword) => BCrypt.Net.BCrypt.Verify(password, hashedPassword);
}
