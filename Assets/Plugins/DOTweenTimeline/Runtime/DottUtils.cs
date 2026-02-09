using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dott
{
    public static class DottUtils
    {
        private static readonly Dictionary<string, Texture2D> Cache = new();

        // Converts a base64 string to a Texture2D and caches it for future use
        public static Texture2D ImageFromString(string source)
        {
            if (Cache.TryGetValue(source, out var cachedTexture) && cachedTexture != null)
            {
                return cachedTexture;
            }

            var bytes = Convert.FromBase64String(source);
            var texture = new Texture2D(1, 1);
            texture.LoadImage(bytes);
            Cache[source] = texture;

            return texture;
        }
    }
}