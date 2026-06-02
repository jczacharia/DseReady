// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text;

namespace Dse.Shared;

public static class Utils
{
    public static string EncodeBasicAuth(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        string credentials = $"{username}:{password}";
        byte[] bytes = Encoding.UTF8.GetBytes(credentials);
        return Convert.ToBase64String(bytes);
    }

    public static string IfEmpty(this string? source, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return defaultValue;
        }

        return source;
    }
}
