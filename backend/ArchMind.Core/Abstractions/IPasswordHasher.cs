namespace ArchMind.Core.Abstractions;

/// <summary>
/// Abstraction over password hashing so callers don't depend on a specific algorithm.
/// Implementations should be safe to register as a singleton.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hash a plaintext password. Returns an opaque, self-describing hash string.</summary>
    string Hash(string password);

    /// <summary>Verify a plaintext password against a previously produced hash.</summary>
    bool Verify(string password, string hash);
}
