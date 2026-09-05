using System.Security.Cryptography;
using System.Text;

namespace StoryForge.Core.Changelist;

public static class GraphHashUtility
{
    public static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
