using System;
using System.Reflection;
using System.Security.Cryptography;

namespace ManualDiceRoller
{
    // Code that repeats a small part of a game method (to plan its dice, or to
    // replay a skipped tail) is pinned to the exact original IL it repeats.
    internal static class NativeMethodPin
    {
        internal static string Hash(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static void Verify(MethodBase method, string hash)
        {
            var body = method == null ? null : method.GetMethodBody();
            if (body == null || Hash(body.GetILAsByteArray()) != hash)
                throw new InvalidOperationException("Unsupported native method version: " + (method == null ? "missing" : method.DeclaringType.Name + "." + method.Name));
        }
    }
}
