using System.Security.Cryptography;

namespace Quartermaster.Net;

public static class GameCode
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int Length = 6;

    public static string New()
    {
        var c = new char[Length];
        for (int i = 0; i < Length; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(c);
    }

    public static bool IsValid(string code) =>
        code is { Length: Length } && code.All(ch => Alphabet.Contains(ch));

    public static string Normalize(string input) =>
        new(input.Where(ch => ch is not (' ' or '-' or '\t')).Select(char.ToUpperInvariant).ToArray());
}
