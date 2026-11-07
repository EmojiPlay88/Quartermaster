using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quartermaster.Core;

/// <summary>Fingerprint of a whole game state, to check that two players' games match.</summary>
public static class StateHash
{
    /// <summary>Lower-hex SHA-256 of the state's JSON (<see cref="QuartermasterJson.Options"/>).</summary>
    public static string Of(GameState s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s, QuartermasterJson.Options))));
}
